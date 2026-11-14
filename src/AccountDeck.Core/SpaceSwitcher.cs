using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AccountDeck.Core;

/// <summary>What the setup guide hands over: the spaces, the choice for every row, and which space each project goes to.</summary>
public sealed class SpaceSetupPlan
{
    public required List<Space> Spaces { get; init; }
    /// <summary>The space whose data stays in ~/.claude after setup (the one in use now).</summary>
    public required string LiveSpaceId { get; init; }
    public required List<SpaceItem> Items { get; init; }
    /// <summary>Item key to choice; rows not listed are shared.</summary>
    public required Dictionary<string, ItemChoice> Choices { get; init; }
    /// <summary>Folder name under ~/.claude/projects to space id; folders not listed stay with the live space.</summary>
    public required Dictionary<string, string> ProjectOwners { get; init; }
}

/// <summary>
/// Keeps several spaces' private parts of ~/.claude apart by moving them between ~/.claude (the live space)
/// and <see cref="AppPaths.SpacesStore"/>\&lt;space id&gt; (every other space). Shared items never move.
/// Every move is a rename on the same drive. A journal written before a switch lets an interrupted one be undone.
/// </summary>
public sealed class SpaceSwitcher(AppPaths paths)
{
    public const string MarkerName = ".accountdeck-space.json";

    private sealed record Move(string From, string To);

    private string Live(string key) => SpaceItems.LivePath(paths, key);
    private string Parked(string spaceId, string key) => SpaceItems.ParkedPath(paths, spaceId, key);

    /// <summary>The items swapped in and out with a space.</summary>
    public static IEnumerable<string> PrivateKeys(SpaceSettings s, string spaceId) =>
        SpaceItems.AlwaysSeparate.Concat(s.Items.Where(kv => kv.Value.BelongsTo(spaceId)).Select(kv => kv.Key));

    // ---------------------------------------------------------------- switching

    public void Switch(AppState state, string targetId)
    {
        var s = state.Spaces;
        if (!s.Enabled) throw new DeckException("Spaces are not set up yet.");
        var target = s.Find(targetId) ?? throw new DeckException("That space no longer exists.");
        var from = s.Active ?? throw new DeckException("Account Deck does not know which space is in use. Choose Refresh and try again.");
        if (from.Id == target.Id) return;

        var outgoing = PrivateKeys(s, from.Id).Distinct().ToList();
        var incoming = PrivateKeys(s, target.Id).Distinct().ToList();
        var moves = new List<Move>();
        foreach (var key in outgoing)
        {
            if (!SpaceItems.Exists(Live(key))) continue;
            if (SpaceItems.Exists(Parked(from.Id, key)))
                throw new DeckException($"\"{key}\" is already saved for \"{from.Name}\" in Account Deck's data folder, so it was not moved. Nothing was changed.");
            moves.Add(new Move(Live(key), Parked(from.Id, key)));
        }
        var leaving = new HashSet<string>(moves.Select(m => m.From), StringComparer.OrdinalIgnoreCase);
        foreach (var key in incoming)
        {
            if (!SpaceItems.Exists(Parked(target.Id, key))) continue;
            if (SpaceItems.Exists(Live(key)) && !leaving.Contains(Live(key)))
                throw new DeckException($"\"{key}\" exists in ~/.claude and is also saved for \"{target.Name}\". " +
                                        "Move or rename one of them, or mark it as shared in Settings > Spaces. Nothing was changed.");
            moves.Add(new Move(Parked(target.Id, key), Live(key)));
        }

        WriteJournal(from.Id, target.Id, moves);
        Run(moves);
        s.ActiveId = target.Id;
        WriteMarker(target);
        File.Delete(paths.SpaceJournal);
    }

    /// <summary>Repairs an interrupted switch and makes sure the state knows which space is live. Safe to call any time.</summary>
    public void Reconcile(AppState state, Action<string>? log = null)
    {
        var s = state.Spaces;
        if (!s.Enabled) return;

        var journal = FileUtil.ReadJsonObject(paths.SpaceJournal);
        if (journal is not null)
        {
            var moves = (journal["moves"] as JsonArray ?? [])
                .OfType<JsonObject>()
                .Select(m => new Move(m["from"]!.GetValue<string>(), m["to"]!.GetValue<string>()))
                .ToList();
            Undo(moves);
            var fromId = journal["from"]?.GetValue<string>();
            if (s.Find(fromId) is { } back)
            {
                s.ActiveId = back.Id;
                WriteMarker(back);
                log?.Invoke($"An interrupted switch was undone; \"{back.Name}\" is in use again.");
            }
            File.Delete(paths.SpaceJournal);
        }

        var markerId = FileUtil.ReadJsonObject(Path.Combine(paths.ClaudeDir, MarkerName))?["id"]?.GetValue<string>();
        if (s.Find(markerId) is { } live && live.Id != s.ActiveId) s.ActiveId = live.Id;
    }

    // ---------------------------------------------------------------- first setup

    /// <summary>
    /// Splits today's mixed ~/.claude into spaces: cuts CLAUDE.md sections into rule files, moves every project
    /// (with its file history and prompt history) to its space, and parks or copies the chosen items.
    /// The files that get rewritten (CLAUDE.md, history.jsonl) are copied to a backup folder first.
    /// If anything fails, what already happened is undone.
    /// </summary>
    public string SetUp(AppState state, SpaceSetupPlan plan)
    {
        var s = state.Spaces;
        if (s.Enabled) throw new DeckException("Spaces are already set up.");
        if (plan.Spaces.Count < 2) throw new DeckException("Make at least two spaces.");
        if (plan.Spaces.Select(x => x.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.Spaces.Count || plan.Spaces.Any(x => x.Name.Trim().Length == 0))
            throw new DeckException("Every space needs its own name.");
        var live = plan.Spaces.FirstOrDefault(x => x.Id == plan.LiveSpaceId) ?? throw new DeckException("Choose which space is in use now.");
        foreach (var other in plan.Spaces)
            if (Directory.Exists(Path.Combine(paths.SpacesStore, other.Id)) && Directory.EnumerateFileSystemEntries(Path.Combine(paths.SpacesStore, other.Id)).Any())
                throw new DeckException($"Account Deck's data folder already holds files for \"{other.Name}\". Nothing was changed.");

        var undo = new Stack<Action>();
        var backup = Path.Combine(paths.SpacesStore, "backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        try
        {
            Directory.CreateDirectory(backup);
            foreach (var name in new[] { SpaceItems.ClaudeMd, "history.jsonl" })
            {
                var file = Path.Combine(paths.ClaudeDir, name);
                if (File.Exists(file)) File.Copy(file, Path.Combine(backup, name));
            }

            // 1. CLAUDE.md sections become rule files.
            CutSections(plan.Items, undo, backup);

            // 2. Projects, their file history and prompt history go to their space.
            var owners = plan.ProjectOwners.Where(kv => kv.Value != live.Id).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            var sessionOwner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (dir, spaceId) in owners)
            {
                var src = Path.Combine(paths.ClaudeDir, "projects", dir);
                if (!Directory.Exists(src)) continue;
                foreach (var f in Directory.GetFiles(src, "*.jsonl")) sessionOwner[Path.GetFileNameWithoutExtension(f)] = spaceId;
                Do(new Move(src, Parked(spaceId, "projects/" + dir)), undo);
            }
            foreach (var (session, spaceId) in sessionOwner)
            {
                var fh = Path.Combine(paths.ClaudeDir, "file-history", session);
                if (Directory.Exists(fh)) Do(new Move(fh, Parked(spaceId, "file-history/" + session)), undo);
                var todos = Path.Combine(paths.ClaudeDir, "todos");
                if (Directory.Exists(todos))
                    foreach (var t in Directory.GetFiles(todos, session + "*"))
                        Do(new Move(t, Parked(spaceId, "todos/" + Path.GetFileName(t))), undo);
            }
            SplitHistory(owners, backup, undo);

            // 3. Chosen items: park what belongs to another space, copy what every space keeps its own copy of.
            foreach (var (key, choice) in plan.Choices)
            {
                if (!SpaceItems.Exists(Live(key))) continue;
                if (choice.Mode == ItemMode.OnlyIn && choice.SpaceId != live.Id)
                {
                    if (plan.Spaces.All(x => x.Id != choice.SpaceId)) throw new DeckException($"\"{key}\" was given to a space that does not exist.");
                    Do(new Move(Live(key), Parked(choice.SpaceId!, key)), undo);
                }
                else if (choice.Mode == ItemMode.Separate)
                {
                    foreach (var other in plan.Spaces.Where(x => x.Id != live.Id))
                    {
                        var copy = Parked(other.Id, key);
                        Copy(Live(key), copy);
                        undo.Push(() => Delete(copy));
                    }
                }
            }
        }
        catch
        {
            while (undo.Count > 0)
            {
                try { undo.Pop()(); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or DeckException) { }
            }
            throw;
        }

        s.List = plan.Spaces;
        s.Items = plan.Choices.Where(kv => kv.Value.Mode != ItemMode.Shared).ToDictionary(kv => kv.Key, kv => kv.Value);
        s.ActiveId = live.Id;
        s.Enabled = true;
        WriteMarker(live);
        return backup;
    }

    private void CutSections(List<SpaceItem> items, Stack<Action> undo, string backup)
    {
        var sections = items.Where(i => i.SectionText is not null).ToList();
        if (sections.Count == 0) return;
        var claudeMd = Path.Combine(paths.ClaudeDir, SpaceItems.ClaudeMd);
        var (preamble, _) = SpaceItems.SplitClaudeMd(File.Exists(claudeMd) ? File.ReadAllText(claudeMd) : "");
        foreach (var section in sections)
        {
            var target = Live(section.Key);
            if (File.Exists(target)) throw new DeckException($"{section.Key} already exists. Nothing was changed.");
            FileUtil.WriteAtomic(target, section.SectionText!);
            undo.Push(() => File.Delete(target));
        }
        FileUtil.WriteAtomic(claudeMd, preamble);
        undo.Push(() => File.Copy(Path.Combine(backup, SpaceItems.ClaudeMd), claudeMd, true));
    }

    /// <summary>history.jsonl holds every prompt typed, each line with its "project" path: lines go to the space of their project.</summary>
    private void SplitHistory(Dictionary<string, string> owners, string backup, Stack<Action> undo)
    {
        var history = Path.Combine(paths.ClaudeDir, "history.jsonl");
        if (owners.Count == 0 || !File.Exists(history)) return;

        var keep = new StringBuilder();
        var parked = new Dictionary<string, StringBuilder>();
        foreach (var line in File.ReadAllLines(history))
        {
            string? owner = null;
            try
            {
                if (JsonNode.Parse(line)?["project"]?.GetValue<string>() is { Length: > 0 } project)
                    owners.TryGetValue(SpaceItems.ProjectDirName(project), out owner);
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException) { }
            (owner is null ? keep : parked.TryGetValue(owner, out var sb) ? sb : parked[owner] = new StringBuilder()).Append(line).Append('\n');
        }
        foreach (var (spaceId, text) in parked)
        {
            var dest = Parked(spaceId, "history.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.AppendAllText(dest, text.ToString(), new UTF8Encoding(false));
            undo.Push(() => File.Delete(dest));
        }
        FileUtil.WriteAtomic(history, keep.ToString());
        undo.Push(() => File.Copy(Path.Combine(backup, "history.jsonl"), history, true));
    }

    // ---------------------------------------------------------------- turning off

    /// <summary>
    /// Brings every parked space back into ~/.claude. Projects and file history are merged folder by folder,
    /// prompt history is merged by time. Anything that would overwrite something already live stays parked,
    /// and is returned so the user can see it.
    /// </summary>
    public List<string> TurnOff(AppState state)
    {
        var s = state.Spaces;
        if (!s.Enabled) return [];
        var leftovers = new List<string>();
        foreach (var space in s.List.Where(x => x.Id != s.ActiveId))
        {
            var root = Path.Combine(paths.SpacesStore, space.Id);
            if (!Directory.Exists(root)) continue;

            foreach (var folder in new[] { "projects", "file-history", "todos", "plans", "paste-cache" })
            {
                var parkedDir = Path.Combine(root, folder);
                if (!Directory.Exists(parkedDir)) continue;
                foreach (var entry in Directory.EnumerateFileSystemEntries(parkedDir).ToList())
                {
                    var dest = Path.Combine(paths.ClaudeDir, folder, Path.GetFileName(entry));
                    if (SpaceItems.Exists(dest)) { leftovers.Add(entry); continue; }
                    MovePath(entry, dest);
                }
                DeleteIfEmpty(parkedDir);
            }

            var parkedHistory = Path.Combine(root, "history.jsonl");
            if (File.Exists(parkedHistory))
            {
                var liveHistory = Path.Combine(paths.ClaudeDir, "history.jsonl");
                var lines = (File.Exists(liveHistory) ? File.ReadAllLines(liveHistory) : []).Concat(File.ReadAllLines(parkedHistory))
                    .Where(l => l.Length > 0)
                    .OrderBy(Timestamp)
                    .ToList();
                FileUtil.WriteAtomic(liveHistory, string.Join("\n", lines) + (lines.Count > 0 ? "\n" : ""));
                File.Delete(parkedHistory);
            }

            // Items only this space had come back; separate copies of settings stay parked (the live copy is kept).
            foreach (var (key, choice) in s.Items.Where(kv => kv.Value.Mode == ItemMode.OnlyIn && kv.Value.SpaceId == space.Id))
            {
                var parked = Parked(space.Id, key);
                if (!SpaceItems.Exists(parked)) continue;
                if (SpaceItems.Exists(Live(key))) { leftovers.Add(parked); continue; }
                MovePath(parked, Live(key));
            }
            // Whatever is still here (separate copies of settings, clashing folders) stays for the user to look at.
            if (Directory.EnumerateFileSystemEntries(root).Any() && !leftovers.Any(l => l.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
                leftovers.Add(root);
        }

        s.Enabled = false;
        s.ActiveId = null;
        s.Items.Clear();
        File.Delete(Path.Combine(paths.ClaudeDir, MarkerName));
        return leftovers;
    }

    private static long Timestamp(string line)
    {
        try { return JsonNode.Parse(line)?["timestamp"]?.GetValue<long>() ?? 0; }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { return 0; }
    }

    // ---------------------------------------------------------------- changing a row later

    /// <summary>Moves one item so that it matches a new choice, wherever it is right now.</summary>
    public void ChangeItem(AppState state, string key, ItemChoice next)
    {
        var s = state.Spaces;
        if (!s.Enabled) throw new DeckException("Spaces are not set up yet.");
        if (SpaceItems.AlwaysSeparate.Contains(key)) throw new DeckException("Conversations and history are always kept separate.");
        if (next.Mode == ItemMode.OnlyIn && s.Find(next.SpaceId) is null) throw new DeckException("That space no longer exists.");
        var active = s.ActiveId!;
        var prev = s.ChoiceFor(key);

        // Where each space's version is now, if it has one.
        string? Where(ItemChoice c, string spaceId) => c.Mode switch
        {
            ItemMode.Shared => Live(key),
            _ when !c.BelongsTo(spaceId) => null,
            _ => spaceId == active ? Live(key) : Parked(spaceId, key),
        };
        string? Source(string spaceId) => Where(prev, spaceId) is { } p && SpaceItems.Exists(p) ? p : null;

        switch (next.Mode)
        {
            case ItemMode.Shared:
            {
                if (prev.Mode == ItemMode.OnlyIn && prev.SpaceId != active && Source(prev.SpaceId!) is { } src)
                {
                    if (SpaceItems.Exists(Live(key))) throw new DeckException($"\"{key}\" already exists in ~/.claude. Rename one of them first.");
                    MovePath(src, Live(key));
                }
                // From Separate: the live copy becomes the shared one; other copies stay parked, unused.
                break;
            }
            case ItemMode.OnlyIn:
            {
                var owner = next.SpaceId!;
                // Shared: the live copy. Only in A: A's copy. Separate: the new owner's own copy.
                var src = Source(prev.Mode switch { ItemMode.OnlyIn => prev.SpaceId!, ItemMode.Separate => owner, _ => active });
                var dest = owner == active ? Live(key) : Parked(owner, key);
                if (src is not null && !string.Equals(src, dest, StringComparison.OrdinalIgnoreCase))
                {
                    if (SpaceItems.Exists(dest)) throw new DeckException($"\"{key}\" is already saved for that space. Nothing was changed.");
                    MovePath(src, dest);
                }
                // A separate copy of the space in use that is no longer wanted leaves ~/.claude.
                if (prev.Mode == ItemMode.Separate && owner != active && SpaceItems.Exists(Live(key)) && !SpaceItems.Exists(Parked(active, key)))
                    MovePath(Live(key), Parked(active, key));
                break;
            }
            case ItemMode.Separate:
            {
                var src = prev.Mode == ItemMode.OnlyIn ? Source(prev.SpaceId!) : Source(active);
                if (src is null) break;
                foreach (var space in s.List)
                {
                    var dest = space.Id == active ? Live(key) : Parked(space.Id, key);
                    if (!SpaceItems.Exists(dest)) Copy(src, dest);
                }
                break;
            }
        }

        if (next.Mode == ItemMode.Shared) s.Items.Remove(key);
        else s.Items[key] = next;
    }

    // ---------------------------------------------------------------- one conversation

    /// <summary>
    /// Moves one conversation to another space: its transcript and the folder next to it (tool results, title),
    /// its file history, todos and its lines in the prompt history. The project's memory stays where it is,
    /// because it belongs to the whole project. Returns an undo action, used when a later step fails.
    /// </summary>
    public Action MoveConversation(AppState state, string sessionId, string toSpaceId)
    {
        var s = state.Spaces;
        if (!s.Enabled) throw new DeckException("Spaces are not set up yet.");
        var to = s.Find(toSpaceId) ?? throw new DeckException("That space no longer exists.");
        var (from, dirName) = FindConversation(state, sessionId) ?? throw new DeckException("That conversation was not found. It may have been moved or deleted.");
        if (from.Id == to.Id) return () => { };

        var src = SpaceItems.SpaceRoot(paths, s, from.Id);
        var dst = SpaceItems.SpaceRoot(paths, s, to.Id);
        string In(string root, string rel) => Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));

        var rels = new List<string> { $"projects/{dirName}/{sessionId}.jsonl", $"projects/{dirName}/{sessionId}", $"file-history/{sessionId}" };
        var todos = In(src, "todos");
        if (Directory.Exists(todos)) rels.AddRange(Directory.GetFiles(todos, sessionId + "*").Select(f => "todos/" + Path.GetFileName(f)));
        var moves = rels.Where(r => SpaceItems.Exists(In(src, r))).Select(r => new Move(In(src, r), In(dst, r))).ToList();
        var clash = moves.FirstOrDefault(m => SpaceItems.Exists(m.To));
        if (clash is not null) throw new DeckException($"\"{to.Name}\" already has a copy of this conversation ({clash.To}). Nothing was moved.");

        var srcHistory = In(src, "history.jsonl");
        var dstHistory = In(dst, "history.jsonl");
        var srcBefore = File.Exists(srcHistory) ? File.ReadAllText(srcHistory) : null;
        var dstBefore = File.Exists(dstHistory) ? File.ReadAllText(dstHistory) : null;
        void Undo()
        {
            SpaceSwitcher.Undo(moves);
            if (srcBefore is not null) FileUtil.WriteAtomic(srcHistory, srcBefore);
            if (dstBefore is not null) FileUtil.WriteAtomic(dstHistory, dstBefore);
            else if (File.Exists(dstHistory)) File.Delete(dstHistory);
        }

        Run(moves);
        try
        {
            if (srcBefore is not null)
            {
                var lines = srcBefore.Split('\n').Where(l => l.Length > 0).ToList();
                var mine = lines.Where(l => SessionOf(l) == sessionId).ToList();
                if (mine.Count > 0)
                {
                    FileUtil.WriteAtomic(srcHistory, string.Concat(lines.Except(mine).Select(l => l + "\n")));
                    Directory.CreateDirectory(dst);
                    File.AppendAllText(dstHistory, string.Concat(mine.Select(l => l + "\n")), new UTF8Encoding(false));
                }
            }
        }
        catch
        {
            Undo();
            throw;
        }
        return Undo;
    }

    /// <summary>The space and project folder a conversation is in.</summary>
    public (Space Space, string DirName)? FindConversation(AppState state, string sessionId)
    {
        foreach (var space in state.Spaces.List)
        {
            var projects = Path.Combine(SpaceItems.SpaceRoot(paths, state.Spaces, space.Id), "projects");
            if (!Directory.Exists(projects)) continue;
            foreach (var dir in Directory.GetDirectories(projects))
                if (File.Exists(Path.Combine(dir, sessionId + ".jsonl"))) return (space, Path.GetFileName(dir));
        }
        return null;
    }

    private static string? SessionOf(string line)
    {
        try { return JsonNode.Parse(line)?["sessionId"]?.GetValue<string>(); }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return null; }
    }

    // ---------------------------------------------------------------- helpers

    private void WriteJournal(string fromId, string toId, List<Move> moves) =>
        FileUtil.WriteAtomic(paths.SpaceJournal, new JsonObject
        {
            ["from"] = fromId,
            ["to"] = toId,
            ["moves"] = new JsonArray(moves.Select(m => (JsonNode)new JsonObject { ["from"] = m.From, ["to"] = m.To }).ToArray()),
        }.ToJsonString());

    private void WriteMarker(Space space) =>
        FileUtil.WriteAtomic(Path.Combine(paths.ClaudeDir, MarkerName), new JsonObject { ["id"] = space.Id, ["name"] = space.Name }.ToJsonString());

    /// <summary>Runs the moves in order; if one fails, the ones already done are reversed.</summary>
    private static void Run(List<Move> moves)
    {
        var done = new List<Move>();
        try
        {
            foreach (var m in moves)
            {
                MovePath(m.From, m.To);
                done.Add(m);
            }
        }
        catch
        {
            Undo(done);
            throw;
        }
    }

    /// <summary>Reverses moves that completed (destination present, source gone), newest first. Best effort.</summary>
    private static void Undo(IReadOnlyList<Move> moves)
    {
        foreach (var m in moves.Reverse())
        {
            if (!SpaceItems.Exists(m.To) || SpaceItems.Exists(m.From)) continue;
            try { MovePath(m.To, m.From); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or DeckException) { }
        }
    }

    private static void Do(Move m, Stack<Action> undo)
    {
        MovePath(m.From, m.To);
        undo.Push(() => MovePath(m.To, m.From));
    }

    private static void MovePath(string from, string to)
    {
        if (Directory.Exists(from)) { FileUtil.MoveDirectory(from, to); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        for (var i = 1; ; i++)
        {
            try { File.Move(from, to); return; }
            catch (IOException) when (i < 24) { Thread.Sleep(FileUtil.RetryDelayOverrideMs ?? 500); }
            catch (IOException e)
            {
                throw new DeckException("Another program is still using Claude's files, so nothing was switched. Close Claude Code terminals and editors and try again.", e);
            }
        }
    }

    private static void Copy(string from, string to)
    {
        if (File.Exists(from))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(from, to);
            return;
        }
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
    }

    private static void Delete(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, true);
        else if (File.Exists(path)) File.Delete(path);
    }

    private static void DeleteIfEmpty(string dir)
    {
        if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
    }
}
