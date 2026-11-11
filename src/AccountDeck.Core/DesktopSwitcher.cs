using System.Text.Json.Nodes;

namespace AccountDeck.Core;

/// <summary>Where Claude Desktop keeps its data and how to start it.</summary>
public sealed record DesktopInstall(string DataDir, bool IsStore, string? Aumid, string? ExePath);

/// <summary>The parts of Claude Desktop handling that touch real processes. Faked in tests.</summary>
public interface IDesktopHost
{
    DesktopInstall? Detect();
    bool IsRunning(DesktopInstall install);
    /// <summary>Closes Claude Desktop and everything it started. Returns false if something survived.</summary>
    bool Close(DesktopInstall install, TimeSpan timeout);
    void Launch(DesktopInstall install);
    void DeleteFolder(string dir);
}

/// <summary>
/// Switches Claude Desktop accounts by swapping its whole data folder.
/// The live folder belongs to the active profile; every other profile is parked in
/// <see cref="AppPaths.DesktopStore"/>. A marker file inside each folder says which profile it is,
/// so the state can be rebuilt if a switch is interrupted.
/// </summary>
public sealed class DesktopSwitcher(AppPaths paths, IDesktopHost host)
{
    public const string MarkerName = ".accountdeck-profile.json";
    private const string SettingsFile = "claude_desktop_config.json";

    public DesktopInstall? Install { get; private set; }

    private string Parked(string id) => Path.Combine(paths.DesktopStore, id);

    public bool IsRunning => Install is not null && host.IsRunning(Install);

    /// <summary>Brings the saved state in line with what is on disk. Safe to call at any time.</summary>
    public void Reconcile(AppState state, Action<string>? log = null)
    {
        Install = host.Detect();
        if (Install is null)
        {
            state.ActiveIds.Remove(AccountKind.Desktop);
            return;
        }

        var live = Install.DataDir;

        // Parked folders the state has forgotten about (for example after state.json was lost).
        if (Directory.Exists(paths.DesktopStore))
        {
            foreach (var dir in Directory.GetDirectories(paths.DesktopStore))
            {
                var marker = ReadMarker(dir);
                var id = Path.GetFileName(dir);
                if (state.Find(id) is not null) continue;
                state.Profiles.Add(new Profile { Id = id, Kind = AccountKind.Desktop, Name = marker?.Name ?? state.NextName(AccountKind.Desktop) });
                log?.Invoke($"Found a saved Claude Desktop account that was not listed and added it back.");
            }
        }

        var liveHasData = Directory.Exists(live) && (ReadMarker(live) is not null || Directory.EnumerateFileSystemEntries(live).Any());

        if (!liveHasData)
        {
            // A switch was interrupted after parking the old folder: put the active one back.
            var active = state.Active(AccountKind.Desktop);
            if (active is not null && Directory.Exists(Parked(active.Id)))
            {
                if (Directory.Exists(live)) Directory.Delete(live);
                FileUtil.MoveDirectory(Parked(active.Id), live, attempts: 4);
                log?.Invoke($"Restored Claude Desktop account \"{active.Name}\" after an interrupted switch.");
            }
            else
            {
                state.ActiveIds.Remove(AccountKind.Desktop);
            }
        }
        else
        {
            var marker = ReadMarker(live);
            var profile = marker is null ? null : state.Find(marker.Id);
            if (profile is null)
            {
                // First run, or the marker was lost: the live folder is the active profile
                // unless that profile is demonstrably parked elsewhere.
                var active = state.Active(AccountKind.Desktop);
                profile = active is not null && !Directory.Exists(Parked(active.Id))
                    ? active
                    : new Profile { Id = marker?.Id ?? new Profile().Id, Kind = AccountKind.Desktop, Name = marker?.Name ?? state.NextName(AccountKind.Desktop) };
                if (state.Find(profile.Id) is null)
                {
                    state.Profiles.Add(profile);
                    log?.Invoke($"Saved the current Claude Desktop sign-in as \"{profile.Name}\".");
                }
                WriteMarker(live, profile);
            }
            state.ActiveIds[AccountKind.Desktop] = profile.Id;
        }

        // Drop profiles whose folder no longer exists anywhere.
        foreach (var p in state.Of(AccountKind.Desktop).ToList())
        {
            if (state.IsActive(p) || Directory.Exists(Parked(p.Id))) continue;
            state.Profiles.Remove(p);
            log?.Invoke($"Claude Desktop account \"{p.Name}\" was removed because its data folder is gone.");
        }

        foreach (var p in state.Of(AccountKind.Desktop))
            ReadDetails(p, state.IsActive(p) ? live : Parked(p.Id), state);
    }

    public void Switch(AppState state, string targetId, bool relaunch = true)
    {
        var (install, current) = Prepare(state);
        var target = state.Find(targetId) ?? throw new DeckException("That account no longer exists.");
        if (current?.Id == target.Id) return;
        if (!Directory.Exists(Parked(target.Id)))
            throw new DeckException($"The saved data for \"{target.Name}\" is missing.");

        Swap(state, install, current, () => FileUtil.MoveDirectory(Parked(target.Id), install.DataDir), target);
        if (relaunch) host.Launch(install);
    }

    /// <summary>Parks the current account and starts Claude Desktop with a clean sign-in screen.</summary>
    public Profile AddNew(AppState state, string name, bool copySettings, bool relaunch = true)
    {
        var (install, current) = Prepare(state);
        var created = new Profile { Kind = AccountKind.Desktop, Name = string.IsNullOrWhiteSpace(name) ? state.NextName(AccountKind.Desktop) : name.Trim() };

        string? settings = null;
        var settingsPath = Path.Combine(install.DataDir, SettingsFile);
        if (copySettings && File.Exists(settingsPath)) settings = File.ReadAllText(settingsPath);

        state.Profiles.Add(created);
        try
        {
            Swap(state, install, current, () =>
            {
                Directory.CreateDirectory(install.DataDir);
                if (settings is not null) File.WriteAllText(Path.Combine(install.DataDir, SettingsFile), settings);
            }, created);
        }
        catch
        {
            state.Profiles.Remove(created);
            throw;
        }
        if (relaunch) host.Launch(install);
        return created;
    }

    public void Remove(AppState state, string id)
    {
        var p = state.Find(id) ?? throw new DeckException("That account no longer exists.");
        if (state.IsActive(p))
            throw new DeckException("This is the account Claude Desktop is using right now. Switch to another account first, then remove it.");
        if (Directory.Exists(Parked(p.Id))) host.DeleteFolder(Parked(p.Id));
        state.Profiles.Remove(p);
    }

    public void Rename(AppState state, Profile p, string name)
    {
        p.Name = name;
        var dir = state.IsActive(p) ? Install?.DataDir : Parked(p.Id);
        if (dir is not null && Directory.Exists(dir)) WriteMarker(dir, p);
    }

    private (DesktopInstall, Profile?) Prepare(AppState state)
    {
        Reconcile(state);
        var install = Install ?? throw new DeckException("Claude Desktop was not found on this computer.");
        var storeRoot = Path.GetPathRoot(Path.GetFullPath(paths.DesktopStore));
        var dataRoot = Path.GetPathRoot(Path.GetFullPath(install.DataDir));
        if (!string.Equals(storeRoot, dataRoot, StringComparison.OrdinalIgnoreCase))
            throw new DeckException("Claude Desktop keeps its data on a different drive than this app, so accounts cannot be switched instantly.");
        return (install, state.Active(AccountKind.Desktop));
    }

    /// <summary>Close Claude, park the live folder, bring the new one in. Rolls back if the second half fails.</summary>
    private void Swap(AppState state, DesktopInstall install, Profile? current, Action bringIn, Profile target)
    {
        if (!host.Close(install, TimeSpan.FromSeconds(20)))
            throw new DeckException("Claude Desktop did not close. Quit it from its tray icon and try again.");

        var live = install.DataDir;
        var parkedCurrent = false;
        if (current is not null && Directory.Exists(live))
        {
            WriteMarker(live, current);
            FileUtil.MoveDirectory(live, Parked(current.Id));
            parkedCurrent = true;
        }
        else if (Directory.Exists(live) && !Directory.EnumerateFileSystemEntries(live).Any())
        {
            Directory.Delete(live);
        }

        try
        {
            bringIn();
            WriteMarker(live, target);
        }
        catch
        {
            if (parkedCurrent && !Directory.Exists(live))
                FileUtil.MoveDirectory(Parked(current!.Id), live, attempts: 6);
            throw;
        }

        state.ActiveIds[AccountKind.Desktop] = target.Id;
        target.LastUsedAt = DateTimeOffset.Now;
    }

    private sealed record Marker(string Id, string Name);

    private static Marker? ReadMarker(string dir)
    {
        var json = FileUtil.ReadJsonObject(Path.Combine(dir, MarkerName));
        var id = json?["id"]?.GetValue<string>();
        return string.IsNullOrEmpty(id) ? null : new Marker(id, json?["name"]?.GetValue<string>() ?? "");
    }

    private static void WriteMarker(string dir, Profile p) =>
        FileUtil.WriteAtomic(Path.Combine(dir, MarkerName), new JsonObject { ["id"] = p.Id, ["name"] = p.Name }.ToJsonString());

    /// <summary>Reads what Claude Desktop itself recorded: which account it is and its recent plan usage.</summary>
    private static void ReadDetails(Profile p, string dir, AppState state)
    {
        try
        {
            var uuid = FileUtil.ReadJsonObject(Path.Combine(dir, "config.json"))?["lastKnownAccountUuid"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(uuid)) p.AccountKey = uuid;
            // Claude Desktop does not store the e-mail in plain text; borrow it from a Claude Code sign-in of the same account.
            var twin = state.Of(AccountKind.ClaudeCode).FirstOrDefault(c => c.AccountKey == p.AccountKey && c.Email is not null);
            if (twin is not null) { p.Email = twin.Email; p.Plan ??= twin.Plan; }

            var samples = FileUtil.ReadJsonObject(Path.Combine(dir, "plan-usage-history.json"))?["samples"] as JsonArray;
            var last = samples?.OfType<JsonObject>().LastOrDefault();
            if (last?["u"] is JsonObject u && last["t"] is JsonValue t)
            {
                p.Usage = new UsageSnapshot
                {
                    FiveHour = u["fh"]?.GetValue<double>(),
                    SevenDay = u["sd"]?.GetValue<double>(),
                    At = DateTimeOffset.FromUnixTimeMilliseconds(t.GetValue<long>()).ToLocalTime(),
                };
            }
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or IOException)
        {
            // Details are a nicety; a file in an unexpected shape must never block switching.
        }
    }
}
