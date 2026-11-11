using System.Text.Json.Nodes;

namespace AccountDeck.Core;

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
        ["Network", "Local Storage", "Session Storage", "IndexedDB", "WebStorage", "SharedStorage", "SharedStorage-wal"];

    private static bool IsSignInKey(string key) => key.StartsWith("oauth:", StringComparison.Ordinal) || key == "lastKnownAccountUuid";

    /// <summary>Close Claude, park the current sign-in, bring in the target's (or none, for a new account).</summary>
    private void SwapSignIn(AppState state, DesktopInstall install, Profile? current, Profile target, bool restore)
    {
        if (!host.Close(install, TimeSpan.FromSeconds(20)))
            throw new DeckException("Claude Desktop did not close. Quit it from its tray icon and try again.");

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
            try
            {
                if (restoring && restore) ParkSignIn(live, Parked(target.Id), target);
                if (current is not null) RestoreSignIn(Parked(current.Id), live);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or DeckException) { }
            throw;
        }

        state.ActiveIds[AccountKind.Desktop] = target.Id;
        target.LastUsedAt = DateTimeOffset.Now;
        SyncSessions(state, live);
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

    private sealed record SessionFile(string Account, string Org, string Name, string Path, DateTime Time);

    /// <summary>
    /// Claude Desktop files each Code session under the account and organisation that started it
    /// (claude-code-sessions\account\organisation\local_id.json), so another account does not list it.
    /// This copies each session of an account managed here into the folders of the other managed accounts, newest version wins.
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
            var mine = files.Where(f => f.Account == account).ToList();
            var orgs = Directory.GetDirectories(Path.Combine(root, account)).Select(d => Path.GetFileName(d)!).ToList();
            if (orgs.Count == 0) continue;
            // Sessions from an organisation this account is not part of go to the one it used last.
            var home = orgs.OrderByDescending(org => mine.Where(f => f.Org == org).Select(f => f.Time)
                .DefaultIfEmpty(Directory.GetLastWriteTimeUtc(Path.Combine(root, account, org))).Max()).First();

            foreach (var source in newest)
            {
                var id = source.Name["local_".Length..^".json".Length];
                if (source.Account == account || deleted.Contains(id)) continue;
                var existing = mine.FirstOrDefault(f => f.Name == source.Name);
                if (existing is not null && existing.Time >= source.Time) continue;
                var destination = existing?.Path ?? Path.Combine(root, account, orgs.Contains(source.Org) ? source.Org : home, source.Name);
                try
                {
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
