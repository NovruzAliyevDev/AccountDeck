using System.Text.Json.Nodes;

namespace AccountDeck.Core;

/// <summary>How Claude Desktop's sidebar shows a Code conversation.</summary>
public enum DesktopListing
{
    /// <summary>Listed under the account that shows it.</summary>
    Listed,
    /// <summary>Removed from the list in Claude Desktop. The conversation itself is still on disk.</summary>
    Removed,
    /// <summary>Listed only under an account that is not the one showing it.</summary>
    OtherAccount,
}

/// <summary>
/// "Shared sessions" mode. Claude Desktop keeps one data folder for every account, so sessions,
/// settings and files stay where they are and their paths never change. A switch moves only the
/// sign-in: the browser-side login (cookies and web storage) and the token entries in config.json.
/// A parked account is therefore a small folder holding just those pieces.
/// </summary>
public sealed partial class DesktopSwitcher
{
    private const string AuthConfigName = "auth-config.json";
    private const string SessionsFolder = "claude-code-sessions";

    /// <summary>Everything in the data folder that says who is signed in on the web side.</summary>
    private static readonly string[] SignInItems =
        [
            "Network", "Local Storage", "Session Storage", "IndexedDB", "WebStorage", "SharedStorage", "SharedStorage-wal",
            // macOS builds keep the cookie jar at the top of the data folder instead of under Network.
            "Cookies", "Cookies-journal",
        ];

    private static bool IsSignInKey(string key) => key.StartsWith("oauth:", StringComparison.Ordinal) || key == "lastKnownAccountUuid";

    /// <summary>Close Claude, park the current sign-in, bring in the target's (or none, for a new account).</summary>
    private void SwapSignIn(AppState state, DesktopInstall install, Profile? current, Profile target, bool restore)
    {
        if (!host.Close(install, TimeSpan.FromSeconds(20)))
            throw new DeckException("Claude Desktop did not close. Quit it from its tray or menu bar icon and try again.");

        var live = install.DataDir;
        Directory.CreateDirectory(live);
        var restoring = false;
        try
        {
            if (current is not null) ParkSignIn(live, Parked(current.Id), current);
            restoring = true;
            if (restore) RestoreSignIn(Parked(target.Id), live);
            WriteMarker(live, target);
        }
        catch
        {
            // Put back whatever already moved, in reverse order. Best effort: the original error is what matters.
            // Each step is tried on its own, so a failure in the first never stops the current sign-in coming back.
            if (restoring && restore) BestEffort(() => ParkSignIn(live, Parked(target.Id), target));
            if (current is not null) BestEffort(() => RestoreSignIn(Parked(current.Id), live));
            throw;
        }

        state.ActiveIds[AccountKind.Desktop] = target.Id;
        target.LastUsedAt = DateTimeOffset.Now;
        if (!state.Spaces.Enabled) SyncSessions(state, live);
    }

    private static void BestEffort(Action undo)
    {
        try { undo(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DeckException) { }
    }

    private static void ParkSignIn(string live, string dir, Profile profile)
    {
        Directory.CreateDirectory(dir);
        foreach (var item in SignInItems) MoveItem(Path.Combine(live, item), Path.Combine(dir, item));

        var configPath = Path.Combine(live, "config.json");
        var authPath = Path.Combine(dir, AuthConfigName);
        if (FileUtil.ReadJsonObject(configPath) is { } config)
        {
            var auth = new JsonObject();
            foreach (var key in config.Select(entry => entry.Key).Where(IsSignInKey).ToList())
            {
                var value = config[key];
                config.Remove(key);
                auth[key] = value;
            }
            // Never replace previously saved tokens with nothing (this also runs while undoing a failed switch).
            if (auth.Count > 0 || !File.Exists(authPath)) FileUtil.WriteAtomic(authPath, auth.ToJsonString());
            if (auth.Count > 0) FileUtil.WriteAtomic(configPath, config.ToJsonString(FileUtil.Pretty));
        }
        WriteMarker(dir, profile, authOnly: true);
    }

    private static void RestoreSignIn(string dir, string live)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var item in SignInItems) MoveItem(Path.Combine(dir, item), Path.Combine(live, item));

        if (FileUtil.ReadJsonObject(Path.Combine(dir, AuthConfigName)) is { Count: > 0 } auth)
        {
            var configPath = Path.Combine(live, "config.json");
            var config = FileUtil.ReadJsonObject(configPath);
            if (config is null && File.Exists(configPath))
                throw new DeckException("Claude Desktop's settings file (config.json) could not be read, so the sign-in was not restored.");
            config ??= new JsonObject();
            foreach (var (key, value) in auth.ToList()) config[key] = value?.DeepClone();
            FileUtil.WriteAtomic(configPath, config.ToJsonString(FileUtil.Pretty));
        }
        Directory.Delete(dir, true);
    }

    private static void MoveItem(string from, string to)
    {
        if (Directory.Exists(from))
        {
            if (Directory.Exists(to)) Directory.Delete(to, true);
            FileUtil.MoveDirectory(from, to);
        }
        else if (File.Exists(from))
        {
            File.Move(from, to, true);
        }
    }

    /// <summary>
    /// Re-files Claude Desktop's sidebar entry for one Code conversation (matched by its Claude Code session id)
    /// from wherever it is to <paramref name="to"/>'s account, in every organisation folder of that account.
    /// Works in both modes: with shared sessions every account's folder is in the live data folder; otherwise a
    /// parked account's folder is in its parked copy. Returns how many entries were filed, and an undo action.
    /// Claude Desktop must be closed.
    /// </summary>
    public (int Filed, Action Undo) MoveSessionEntry(AppState state, string cliSessionId, Profile to)
    {
        var install = (Install ??= host.Detect()) ?? throw new DeckException("Claude Desktop was not found on this computer.");
        var shared = state.Settings.ShareDesktopSessions;
        string RootOf(Profile p) => Path.Combine(shared || state.IsActive(p) ? install.DataDir : Parked(p.Id), SessionsFolder);

        // Every place an entry for this conversation can be: all accounts' folders we know of.
        var roots = state.Of(AccountKind.Desktop).Select(RootOf).Append(Path.Combine(install.DataDir, SessionsFolder))
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists).ToList();
        var sources = new List<(string Path, string Text)>();
        foreach (var root in roots)
        foreach (var file in Directory.EnumerateFiles(root, "local_*.json", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            if (!text.Contains(cliSessionId, StringComparison.Ordinal)) continue;
            if (FileUtil.ReadJsonObject(file)?["cliSessionId"]?.GetValue<string>() == cliSessionId) sources.Add((file, text));
        }
        if (sources.Count == 0 || string.IsNullOrEmpty(to.AccountKey)) return (0, () => { });

        var accountDir = Path.Combine(RootOf(to), to.AccountKey);
        var orgs = Directory.Exists(accountDir) ? Directory.GetDirectories(accountDir) : [];
        if (orgs.Length == 0) return (0, () => { });

        var newest = sources.MaxBy(s => File.GetLastWriteTimeUtc(s.Path));
        var name = Path.GetFileName(newest.Path);
        var created = new List<string>();
        var removed = new List<(string Path, string Text)>();
        void Undo()
        {
            foreach (var f in created) BestEffort(() => File.Delete(f));
            foreach (var (path, text) in removed) BestEffort(() => File.WriteAllText(path, text));
        }
        try
        {
            foreach (var org in orgs)
            {
                var dest = Path.Combine(org, name);
                if (sources.Any(s => string.Equals(s.Path, dest, StringComparison.OrdinalIgnoreCase))) continue;
                File.WriteAllText(dest, newest.Text);
                created.Add(dest);
            }
            var targetFolders = new HashSet<string>(orgs, StringComparer.OrdinalIgnoreCase);
            foreach (var source in sources.Where(s => !targetFolders.Contains(Path.GetDirectoryName(s.Path)!)))
            {
                File.Delete(source.Path);
                removed.Add(source);
            }
        }
        catch
        {
            Undo();
            throw;
        }
        return (orgs.Length, Undo);
    }

    /// <summary>
    /// Moves Claude Desktop's sidebar entries of the given Code conversations (in every account) into
    /// <paramref name="staging"/>, so they go to the Recycle Bin with the conversations. Claude Desktop must be closed.
    /// </summary>
    public (int Removed, Action Undo) RemoveSessionEntries(AppState state, IReadOnlyCollection<string> cliSessionIds, string staging)
    {
        var install = Install ??= host.Detect();
        if (install is null) return (0, () => { });
        var shared = state.Settings.ShareDesktopSessions;
        var roots = state.Of(AccountKind.Desktop)
            .Select(p => Path.Combine(shared || state.IsActive(p) ? install.DataDir : Parked(p.Id), SessionsFolder))
            .Append(Path.Combine(install.DataDir, SessionsFolder))
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists).ToList();
        var wanted = new HashSet<string>(cliSessionIds, StringComparer.Ordinal);
        var moved = new List<(string From, string To)>();
        void Undo()
        {
            foreach (var (from, to) in Enumerable.Reverse(moved)) BestEffort(() => File.Move(to, from));
        }
        try
        {
            var n = 0;
            foreach (var root in roots)
            foreach (var file in Directory.EnumerateFiles(root, "local_*.json", SearchOption.AllDirectories).ToList())
            {
                var id = FileUtil.ReadJsonObject(file)?["cliSessionId"]?.GetValue<string>();
                if (id is null || !wanted.Contains(id)) continue;
                var dest = Path.Combine(staging, "claude-desktop", (++n).ToString("000") + "-" + Path.GetFileName(file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Move(file, dest);
                moved.Add((file, dest));
            }
        }
        catch
        {
            Undo();
            throw;
        }
        return (moved.Count, Undo);
    }

    /// <summary>Where sidebar entries of another space's conversations wait, as &lt;account&gt;\&lt;organisation&gt;\local_id.json.</summary>
    private string HiddenSessions => Path.Combine(paths.AppDataDir, "desktop-hidden-sessions");

    private string SessionsRoot(AppState state, DesktopInstall install, Profile p) =>
        Path.Combine(state.Settings.ShareDesktopSessions || state.IsActive(p) ? install.DataDir : Parked(p.Id), SessionsFolder);

    /// <summary>
    /// Makes each space's Claude Desktop account list only that space's Code conversations: an entry of a conversation
    /// that is in another space is filed under that space's account (if it has none yet) and set aside in
    /// <see cref="HiddenSessions"/>, so <see cref="RestoreHiddenSessions"/> can bring it back. Entries of conversations
    /// no space holds are left alone. Claude Desktop must be closed. Returns how many entries were set aside.
    /// </summary>
    public int FileSessionsBySpace(AppState state, IReadOnlyDictionary<string, string> spaceOfSession)
    {
        var install = Install ??= host.Detect();
        if (install is null || !state.Spaces.Enabled) return 0;
        var accounts = SpaceAccounts(state, install);

        var hidden = 0;
        foreach (var (spaceId, accountDir) in accounts)
        {
            if (!Directory.Exists(accountDir)) continue;
            foreach (var file in Directory.EnumerateFiles(accountDir, "local_*.json", SearchOption.AllDirectories).ToList())
            {
                var id = FileUtil.ReadJsonObject(file)?["cliSessionId"]?.GetValue<string>();
                if (id is null || !spaceOfSession.TryGetValue(id, out var owner) || owner == spaceId) continue;
                if (accounts.TryGetValue(owner, out var to)) EnsureListed(to, file);
                var dest = Path.Combine(HiddenSessions, Path.GetFileName(accountDir), Path.GetRelativePath(accountDir, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Move(file, dest, true);
                hidden++;
            }
        }
        return hidden;
    }

    /// <summary>
    /// Lists every conversation of a space in that space's Claude Desktop account, wherever Claude Desktop filed it:
    /// under another managed account, or under an account this PC signed in to before and that is no longer managed here.
    /// Only copies; entries the user deleted in any account stay deleted. Returns how many conversations were added.
    /// </summary>
    public int ListOwnConversations(AppState state, IReadOnlyDictionary<string, string> spaceOfSession)
    {
        var install = Install ??= host.Detect();
        if (install is null || !state.Spaces.Enabled || spaceOfSession.Count == 0) return 0;
        var accounts = SpaceAccounts(state, install);
        if (accounts.Count == 0) return 0;

        // Every entry Claude Desktop keeps, in every account, live and parked; newest copy per conversation.
        var roots = state.Of(AccountKind.Desktop).Select(p => SessionsRoot(state, install, p)).Append(Path.Combine(install.DataDir, SessionsFolder))
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists);
        var newest = new Dictionary<string, (string Path, DateTime Time)>(StringComparer.OrdinalIgnoreCase);
        var deleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var listedIn = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase); // account folder -> conversations
        try
        {
            foreach (var root in roots)
            foreach (var accountDir in Directory.GetDirectories(root))
            {
                var here = listedIn.TryGetValue(accountDir, out var set) ? set : listedIn[accountDir] = new(StringComparer.OrdinalIgnoreCase);
                foreach (var file in Directory.EnumerateFiles(accountDir, "*", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileName(file);
                    if (name.StartsWith("deleted_", StringComparison.Ordinal)) { deleted.Add(name["deleted_".Length..]); continue; }
                    if (!name.StartsWith("local_", StringComparison.Ordinal) || !name.EndsWith(".json", StringComparison.Ordinal)) continue;
                    if (FileUtil.ReadJsonObject(file)?["cliSessionId"]?.GetValue<string>() is not { } id) continue;
                    here.Add(id);
                    var time = File.GetLastWriteTimeUtc(file);
                    if (!newest.TryGetValue(id, out var known) || time > known.Time) newest[id] = (file, time);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0; // a folder Claude is writing right now; the next pass picks it up
        }

        var added = 0;
        foreach (var (id, (path, _)) in newest)
        {
            if (!spaceOfSession.TryGetValue(id, out var owner) || !accounts.TryGetValue(owner, out var accountDir)) continue;
            if (listedIn.TryGetValue(accountDir, out var listed) && listed.Contains(id)) continue;
            var localId = Path.GetFileName(path)["local_".Length..^".json".Length];
            if (deleted.Contains(localId)) continue;
            try
            {
                if (EnsureListed(accountDir, path)) added++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return added;
    }

    /// <summary>
    /// How Claude Desktop's sidebar shows each Code conversation (by Claude Code session id): listed under the account that
    /// shows it (its space's account, or without spaces the one signed in now), removed there (Claude Desktop only hid it),
    /// or listed under another account only. Conversations Claude Desktop never listed (started in a terminal or editor)
    /// are not in the result. Reads only.
    /// </summary>
    public Dictionary<string, DesktopListing> Listings(AppState state, IReadOnlyDictionary<string, string> spaceOfSession)
    {
        var result = new Dictionary<string, DesktopListing>(StringComparer.OrdinalIgnoreCase);
        var install = Install ??= host.Detect();
        if (install is null) return result;
        var spaceAccounts = state.Spaces.Enabled ? SpaceAccounts(state, install) : new Dictionary<string, string>();
        var activeAccount = state.Active(AccountKind.Desktop) is { AccountKey: { Length: > 0 } key } active
            ? Path.Combine(SessionsRoot(state, install, active), key) : null;

        var roots = state.Of(AccountKind.Desktop).Select(p => SessionsRoot(state, install, p)).Append(Path.Combine(install.DataDir, SessionsFolder))
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists);
        // Claude Desktop marks a removed entry with deleted_<id> in the same organisation folder, so removal counts per folder.
        var entries = new List<(string Id, string AccountDir, string Removed)>();
        var deleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var root in roots)
            foreach (var accountDir in Directory.GetDirectories(root))
            foreach (var file in Directory.EnumerateFiles(accountDir, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                var org = Path.GetDirectoryName(file)!;
                if (name.StartsWith("deleted_", StringComparison.Ordinal)) deleted.Add(Path.Combine(org, name));
                else if (name.StartsWith("local_", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal)
                         && FileUtil.ReadJsonObject(file)?["cliSessionId"]?.GetValue<string>() is { } id)
                    entries.Add((id, accountDir, Path.Combine(org, "deleted_" + name["local_".Length..^".json".Length])));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return result;
        }

        foreach (var g in entries.GroupBy(e => e.Id, StringComparer.OrdinalIgnoreCase))
        {
            var shownIn = spaceOfSession.TryGetValue(g.Key, out var space) && spaceAccounts.TryGetValue(space, out var dir) ? dir : activeAccount;
            var live = g.Where(e => !deleted.Contains(e.Removed)).ToList();
            result[g.Key] = live.Any(e => string.Equals(e.AccountDir, shownIn, StringComparison.OrdinalIgnoreCase)) ? DesktopListing.Listed
                : live.Count < g.Count() ? DesktopListing.Removed
                : DesktopListing.OtherAccount;
        }
        return result;
    }

    /// <summary>
    /// Puts conversations back in Claude Desktop's list, under the account that shows them (their space's account, or
    /// without spaces the one signed in now): the newest entry Claude Desktop ever kept for each, from any account or from
    /// the entries set aside by space, goes into every organisation folder of that account, and its "removed" mark there
    /// is cleared. Conversations Claude Desktop never listed (started in a terminal or an editor) get a new entry, made from one it wrote.
    /// Claude Desktop must be closed. Returns how many conversations were put back.
    /// </summary>
    public int ShowInSidebar(AppState state, IReadOnlyCollection<string> sessionIds, IReadOnlyDictionary<string, string> spaceOfSession)
    {
        var install = Install ??= host.Detect();
        if (install is null || sessionIds.Count == 0) return 0;
        var wanted = new HashSet<string>(sessionIds, StringComparer.OrdinalIgnoreCase);
        var spaceAccounts = state.Spaces.Enabled ? SpaceAccounts(state, install) : new Dictionary<string, string>();
        var activeAccount = state.Active(AccountKind.Desktop) is { AccountKey: { Length: > 0 } key } active
            ? Path.Combine(SessionsRoot(state, install, active), key) : null;

        var sources = state.Of(AccountKind.Desktop).Select(p => SessionsRoot(state, install, p)).Append(Path.Combine(install.DataDir, SessionsFolder))
            .Append(HiddenSessions).Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists);
        var newest = new Dictionary<string, (string Path, DateTime Time)>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in sources)
        foreach (var file in Directory.EnumerateFiles(root, "local_*.json", SearchOption.AllDirectories))
        {
            if (FileUtil.ReadJsonObject(file)?["cliSessionId"]?.GetValue<string>() is not { } id || !wanted.Contains(id)) continue;
            var time = File.GetLastWriteTimeUtc(file);
            if (!newest.TryGetValue(id, out var known) || time > known.Time) newest[id] = (file, time);
        }

        var shown = 0;
        foreach (var (id, (source, time)) in newest)
        {
            var accountDir = spaceOfSession.TryGetValue(id, out var space) && spaceAccounts.TryGetValue(space, out var dir) ? dir : activeAccount;
            if (accountDir is null || !Directory.Exists(accountDir)) continue;
            var name = Path.GetFileName(source);
            var orgs = Directory.GetDirectories(accountDir);
            if (orgs.Length == 0) continue;
            foreach (var org in orgs)
            {
                var mark = Path.Combine(org, "deleted_" + name["local_".Length..^".json".Length]);
                if (File.Exists(mark)) File.Delete(mark);
                var dest = Path.Combine(org, name);
                if (File.Exists(dest)) continue;
                File.Copy(source, dest);
                File.SetLastWriteTimeUtc(dest, time);
            }
            shown++;
        }

        // Conversations Claude Desktop never listed (started in a terminal or an editor) get a new entry,
        // made from one Claude Desktop wrote itself, with this conversation's details.
        var templates = sources.SelectMany(root => Directory.EnumerateFiles(root, "local_*.json", SearchOption.AllDirectories))
            .OrderByDescending(File.GetLastWriteTimeUtc).ToList();
        foreach (var id in wanted.Where(id => !newest.ContainsKey(id)))
        {
            var accountDir = spaceOfSession.TryGetValue(id, out var space) && spaceAccounts.TryGetValue(space, out var dir) ? dir : activeAccount;
            if (accountDir is null || !Directory.Exists(accountDir) || Directory.GetDirectories(accountDir).Length == 0) continue;
            var transcript = SpaceItems.Roots(paths, state.Spaces)
                .Select(r => Path.Combine(r.Root, "projects"))
                .Where(Directory.Exists)
                .SelectMany(p => Directory.EnumerateFiles(p, id + ".jsonl", SearchOption.AllDirectories))
                .FirstOrDefault();
            // Prefer an entry of the same account, as it carries that account's settings.
            var template = templates.FirstOrDefault(t => t.StartsWith(accountDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                           ?? templates.FirstOrDefault();
            if (transcript is null || template is null || FileUtil.ReadJsonObject(template) is not { } entry) continue;
            if (!FillEntry(entry, id, transcript, out var lastUsed)) continue;
            var name = entry["sessionId"]!.GetValue<string>() + ".json";
            foreach (var org in Directory.GetDirectories(accountDir))
            {
                var dest = Path.Combine(org, name);
                File.WriteAllText(dest, entry.ToJsonString());
                File.SetLastWriteTimeUtc(dest, lastUsed);
            }
            shown++;
        }
        return shown;
    }

    /// <summary>Entry fields that describe the template's own conversation and are dropped from a new entry.</summary>
    private static readonly string[] TemplateOnlyKeys =
        ["prs", "postTurnSummary", "postTurnSummaryFor", "turnWrapUp", "writtenRepositories", "shellFolders", "previousTitles"];

    /// <summary>
    /// Turns a copy of an entry Claude Desktop wrote into one for the conversation in <paramref name="transcript"/>: a new
    /// local id, the conversation's id, folder, title, model, times and last answer. Returns false when the transcript has
    /// nothing to show (no folder or no times).
    /// </summary>
    private static bool FillEntry(JsonObject entry, string id, string transcript, out DateTime lastUsed)
    {
        lastUsed = default;
        string? cwd = null, title = null, firstPrompt = null, lastAnswer = null, model = null;
        long first = long.MaxValue, last = long.MinValue;
        var turns = 0;
        foreach (var line in File.ReadLines(transcript))
        {
            if (line.Length == 0) continue;
            JsonObject? o;
            try { o = JsonNode.Parse(line) as JsonObject; }
            catch (System.Text.Json.JsonException) { continue; }
            if (o is null) continue;
            if (o["timestamp"]?.GetValue<string>() is { } ts && DateTimeOffset.TryParse(ts, out var at))
            {
                first = Math.Min(first, at.ToUnixTimeMilliseconds());
                last = Math.Max(last, at.ToUnixTimeMilliseconds());
            }
            cwd ??= o["cwd"]?.GetValue<string>();
            if (o["customTitle"]?.GetValue<string>() is { Length: > 0 } t) title = t;
            var type = o["type"]?.GetValue<string>();
            if (type == "assistant" && o["uuid"]?.GetValue<string>() is { } uuid)
            {
                lastAnswer = uuid;
                model = o["message"]?["model"]?.GetValue<string>() ?? model;
            }
            if (type == "user" && o["message"]?["content"] is JsonValue content && content.TryGetValue<string>(out var prompt))
            {
                turns++;
                firstPrompt ??= prompt;
            }
        }
        if (cwd is null || last == long.MinValue) return false;

        foreach (var key in TemplateOnlyKeys) entry.Remove(key);
        title ??= firstPrompt is null ? "Conversation" : firstPrompt.Length <= 80 ? firstPrompt : firstPrompt[..77] + "…";
        entry["sessionId"] = "local_" + Guid.NewGuid();
        entry["cliSessionId"] = id;
        entry["lastAssistantCliSessionId"] = id;
        entry["cwd"] = cwd;
        entry["originCwd"] = cwd;
        entry["gitAnchorsFolderRealpath"] = cwd;
        entry["folderPlace"] = new JsonObject { ["cwd"] = cwd, ["realPath"] = cwd };
        entry["createdAt"] = first;
        foreach (var key in new[] { "lastActivityAt", "lastTurnEdgeAt", "lastFocusedAt", "latestUserFrameAt" }) entry[key] = last;
        if (model is not null) entry["model"] = model;
        entry["title"] = title;
        entry["titleSource"] = "tool";
        entry["isArchived"] = false;
        entry["completedTurns"] = Math.Max(1, turns);
        entry["titleTurn"] = 0;
        entry["lastAssistantUuid"] = lastAnswer;
        entry["interruptedAwaitingPerson"] = null;
        lastUsed = DateTimeOffset.FromUnixTimeMilliseconds(last).UtcDateTime;
        return true;
    }

    /// <summary>
    /// Each space's Claude Desktop account folder (…\claude-code-sessions\&lt;account&gt;), by space id. Two spaces signed in
    /// to the same account cannot be told apart, so such an account is left out.
    /// </summary>
    private Dictionary<string, string> SpaceAccounts(AppState state, DesktopInstall install)
    {
        var accounts = new Dictionary<string, string>();
        foreach (var space in state.Spaces.List)
            if (space.DesktopProfileId is { } id && state.Find(id) is { AccountKey: { Length: > 0 } key } p)
                accounts[space.Id] = Path.Combine(SessionsRoot(state, install, p), key);
        var clashing = accounts.Values.GroupBy(a => a, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return accounts.Where(kv => !clashing.Contains(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    /// <summary>Copies an entry into every organisation folder of an account that does not list it yet. Returns whether it was copied.</summary>
    private static bool EnsureListed(string accountDir, string entry)
    {
        if (!Directory.Exists(accountDir)) return false;
        var name = Path.GetFileName(entry);
        if (Directory.EnumerateFiles(accountDir, name, SearchOption.AllDirectories).Any()) return false;
        var copied = false;
        foreach (var org in Directory.GetDirectories(accountDir))
        {
            var dest = Path.Combine(org, name);
            File.Copy(entry, dest);
            File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(entry));
            copied = true;
        }
        return copied;
    }

    /// <summary>Puts every entry <see cref="FileSessionsBySpace"/> set aside back in its account. Entries that are listed again meanwhile stay aside. Claude Desktop must be closed.</summary>
    public int RestoreHiddenSessions(AppState state)
    {
        var install = Install ??= host.Detect();
        if (install is null || !Directory.Exists(HiddenSessions)) return 0;
        var restored = 0;
        foreach (var keyDir in Directory.GetDirectories(HiddenSessions))
        {
            var key = Path.GetFileName(keyDir);
            var profile = state.Of(AccountKind.Desktop).FirstOrDefault(p => p.AccountKey == key);
            var root = profile is null ? Path.Combine(install.DataDir, SessionsFolder) : SessionsRoot(state, install, profile);
            foreach (var file in Directory.EnumerateFiles(keyDir, "*", SearchOption.AllDirectories).ToList())
            {
                var dest = Path.Combine(root, key, Path.GetRelativePath(keyDir, file));
                if (File.Exists(dest)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Move(file, dest);
                restored++;
            }
        }
        foreach (var dir in Directory.EnumerateDirectories(HiddenSessions, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        return restored;
    }

    private sealed record SessionFile(string Account, string Org, string Name, string Path, DateTime Time);

    /// <summary>
    /// Claude Desktop files each Code session under the account and organisation that started it
    /// (claude-code-sessions\account\organisation\local_id.json), so another account does not list it.
    /// This copies each session of an account managed here into every organisation folder of all managed accounts, newest version wins.
    /// Sessions the user deleted in any account are left alone. Returns how many files were copied.
    /// </summary>
    public int SyncSessions(AppState state, string live)
    {
        var root = Path.Combine(live, SessionsFolder);
        if (!Directory.Exists(root)) return 0;
        var targets = state.Of(AccountKind.Desktop).Select(p => p.AccountKey).OfType<string>()
            .Where(key => key.Length > 0 && Directory.Exists(Path.Combine(root, key))).Distinct().ToList();
        if (targets.Count == 0) return 0;

        var files = new List<SessionFile>();
        var deleted = new HashSet<string>();
        try
        {
            foreach (var accountDir in Directory.GetDirectories(root))
            foreach (var orgDir in Directory.GetDirectories(accountDir))
            foreach (var file in Directory.EnumerateFiles(orgDir))
            {
                var name = Path.GetFileName(file);
                if (name.StartsWith("deleted_", StringComparison.Ordinal)) deleted.Add(name["deleted_".Length..]);
                else if (name.StartsWith("local_", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal))
                    files.Add(new SessionFile(Path.GetFileName(accountDir), Path.GetFileName(orgDir), name, file, File.GetLastWriteTimeUtc(file)));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        // Only accounts managed here take part; other sign-ins this PC has seen keep their sessions to themselves.
        files.RemoveAll(f => !targets.Contains(f.Account));
        var newest = files.GroupBy(f => f.Name).Select(g => g.MaxBy(f => f.Time)!).ToList();
        var copied = 0;
        foreach (var account in targets)
        {
            var orgs = Directory.GetDirectories(Path.Combine(root, account)).Select(d => Path.GetFileName(d)!).ToList();
            // Claude Desktop only lists the folder of the organisation the account is working in, and which
            // one that is cannot be read from disk, so every organisation folder of the account gets a copy.
            foreach (var org in orgs)
            foreach (var source in newest)
            {
                var id = source.Name["local_".Length..^".json".Length];
                if (deleted.Contains(id)) continue;
                var existing = files.FirstOrDefault(f => f.Account == account && f.Org == org && f.Name == source.Name);
                if (existing is not null && (existing.Path == source.Path || existing.Time >= source.Time)) continue;
                try
                {
                    var destination = Path.Combine(root, account, org, source.Name);
                    File.Copy(source.Path, destination, true);
                    File.SetLastWriteTimeUtc(destination, source.Time);
                    copied++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // A session Claude is writing right now is picked up on the next pass.
                }
            }
        }
        return copied;
    }
}
