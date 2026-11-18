namespace AccountDeck.Core;

/// <summary>
/// The one object the UI talks to. Every operation takes the same lock, saves the state
/// when it is done and raises <see cref="Changed"/>.
/// </summary>
public sealed class DeckService
{
    private readonly StateStore _store;
    private readonly ClaudeApi _api;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IDesktopHost _host;

    public AppPaths Paths { get; }
    public AppState State { get; private set; }
    public DesktopSwitcher Desktop { get; }
    public ClaudeCodeSwitcher ClaudeCode { get; }
    public CodexSwitcher Codex { get; }
    public SpaceSwitcher Spaces { get; }

    /// <summary>Raised (on any thread) after the state changed.</summary>
    public event Action? Changed;
    /// <summary>Raised when something worth a notification happened: title, text.</summary>
    public event Action<string, string>? Notify;

    /// <param name="claudeCredentials">Where Claude Code keeps its tokens; null means ~/.claude/.credentials.json (Windows, Linux, tests).</param>
    public DeckService(AppPaths paths, IDesktopHost host, ISecretProtector protector, HttpClient http, ICredentialStore? claudeCredentials = null)
    {
        Paths = paths;
        _host = host;
        _store = new StateStore(paths);
        _api = new ClaudeApi(http);
        State = _store.Load();
        Desktop = new DesktopSwitcher(paths, host);
        ClaudeCode = new ClaudeCodeSwitcher(paths, protector, claudeCredentials);
        Codex = new CodexSwitcher(paths, protector);
        Spaces = new SpaceSwitcher(paths);
    }

    public CredentialSwitcher Cli(AccountKind kind) => kind == AccountKind.Codex ? Codex : ClaudeCode;

    /// <summary>Re-reads what is on disk: new sign-ins are picked up, interrupted switches are repaired.</summary>
    public Task ReloadAsync() => RunAsync(() => { Scan(); return Task.CompletedTask; });

    /// <summary>Reload, then fetch plan usage from Anthropic and act on it (notify, auto-switch).</summary>
    public Task RefreshUsageAsync() => RunAsync(async () =>
    {
        Scan();
        await ClaudeCode.RefreshUsageAsync(State, _api, State.Settings.KeepSignInsFresh);
        Desktop.Reconcile(State); // lets Desktop profiles borrow e-mails found above
        CheckLimits();
    });

    public Task SwitchAsync(string id) => RunAsync(() =>
    {
        var p = State.Find(id) ?? throw new DeckException("That account no longer exists.");
        if (State.Spaces.Enabled && State.Spaces.Owner(p) is { } owner && owner.Id != State.Spaces.ActiveId)
            throw new DeckException($"\"{p.Name}\" belongs to the space \"{owner.Name}\". Switch to that space instead, so its conversations come with it.");
        if (p.Kind == AccountKind.Desktop) Desktop.Switch(State, id);
        else Cli(p.Kind).Switch(State, id);
        Log($"Switched {Label(p.Kind)} to \"{p.Name}\".");
        return Task.CompletedTask;
    });

    /// <summary>
    /// Switches everything that belongs to a space at once: closes Claude Desktop, swaps the private parts of
    /// ~/.claude, then the Claude Desktop and Claude Code sign-ins, and starts Claude Desktop again.
    /// </summary>
    public Task SwitchSpaceAsync(string spaceId) => RunAsync(() =>
    {
        var s = State.Spaces;
        var target = s.Find(spaceId) ?? throw new DeckException("That space no longer exists.");
        if (s.ActiveId == target.Id) return Task.CompletedTask;
        var from = s.Active;

        WithClaudeClosed(relaunch: true, work: () =>
        {
            // Save the Claude Code sign-in now: after the swap ~/.claude.json may be the other space's copy.
            ClaudeCode.Sync(State);
            Spaces.Switch(State, target.Id);
            try
            {
                UseSignIns(target);
            }
            catch when (from is not null)
            {
                try { Spaces.Switch(State, from.Id); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or DeckException) { }
                throw;
            }
            FileSidebarBySpace();
        });
        Log($"Switched to the space \"{target.Name}\"" + (from is null ? "." : $" (was \"{from.Name}\")."));
        return Task.CompletedTask;
    });

    /// <summary>Signs Claude Desktop and Claude Code in to the accounts of <paramref name="space"/>. Claude must be closed.</summary>
    private void UseSignIns(Space space)
    {
        if (space.DesktopProfileId is { } d && State.Find(d) is not null && State.ActiveIds.GetValueOrDefault(AccountKind.Desktop) != d)
            Desktop.Switch(State, d, relaunch: false);
        // Written after the swap, so a per-space ~/.claude.json gets this space's account.
        if (space.ClaudeCodeProfileId is { } c && State.Find(c) is not null)
            ClaudeCode.Switch(State, c, syncFirst: false);
    }

    /// <summary>Each space's Claude Desktop account lists only that space's conversations. A failure only leaves extra entries, so it is logged, not thrown.</summary>
    private void FileSidebarBySpace()
    {
        try
        {
            var hidden = Desktop.FileSessionsBySpace(State, SpaceItems.SessionOwners(Paths, State.Spaces));
            var listed = Desktop.ListOwnConversations(State, SpaceItems.SessionOwners(Paths, State.Spaces));
            if (hidden + listed > 0)
                Log($"Claude Desktop's list now shows each space's own conversations ({listed} added, {hidden} of the other space set aside).");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log($"Claude Desktop's list could not be sorted by space: {e.Message}", error: true);
        }
    }

    /// <summary>Runs the first-time split of ~/.claude into spaces. Returns the backup folder.</summary>
    public async Task<string> SetUpSpacesAsync(SpaceSetupPlan plan)
    {
        var backup = "";
        await RunAsync(() =>
        {
            string? signInError = null;
            WithClaudeClosed(relaunch: false, work: () =>
            {
                ClaudeCode.Sync(State);
                backup = Spaces.SetUp(State, plan);
                // The space in use must also be signed in with its own accounts, as after a switch.
                try { UseSignIns(State.Spaces.Active!); }
                catch (DeckException e) { signInError = e.Message; }
                FileSidebarBySpace();
            });
            var live = State.Spaces.Active!;
            Log($"Spaces are set up. \"{live.Name}\" is in use now. Files that were rewritten are backed up in {backup}.");
            if (signInError is not null)
                Log($"The accounts of \"{live.Name}\" could not be signed in: {signInError} Switch them on the Accounts tab.", error: true);
            return Task.CompletedTask;
        });
        return backup;
    }

    /// <summary>Moves rows of the shared-items list to new choices, with Claude closed once for all of them.</summary>
    public Task ChangeSpaceItemsAsync(IReadOnlyDictionary<string, ItemChoice> changes) => RunAsync(() =>
    {
        if (changes.Count == 0) return Task.CompletedTask;
        WithClaudeClosed(relaunch: false, work: () =>
        {
            foreach (var (key, choice) in changes) Spaces.ChangeItem(State, key, choice);
        });
        Log($"Changed what the spaces share ({changes.Count} item(s)).");
        return Task.CompletedTask;
    });

    /// <summary>
    /// Moves one conversation to another space, so it can be continued there: its transcript, file history, to-dos,
    /// prompt history and its entry in Claude Desktop's sidebar. Claude Desktop is closed while it moves.
    /// </summary>
    public Task MoveConversationAsync(string sessionId, string toSpaceId) => RunAsync(() =>
    {
        var target = State.Spaces.Find(toSpaceId) ?? throw new DeckException("That space no longer exists.");
        var from = Spaces.FindConversation(State, sessionId)?.Space;
        if (from is null) throw new DeckException("That conversation was not found. It may have been moved or deleted.");
        if (from.Id == target.Id) return Task.CompletedTask;

        var filed = 0;
        WithClaudeClosed(relaunch: false, work: () =>
        {
            var undo = Spaces.MoveConversation(State, sessionId, target.Id);
            try
            {
                if (Desktop.Install is not null && target.DesktopProfileId is { } d && State.Find(d) is { } account)
                    filed = Desktop.MoveSessionEntry(State, sessionId, account).Filed;
            }
            catch
            {
                undo();
                throw;
            }
        });
        Log($"Moved a conversation from \"{from.Name}\" to \"{target.Name}\"" +
            (filed > 0 ? " (also in Claude Desktop's list)." : "."));
        return Task.CompletedTask;
    });

    /// <summary>
    /// Deletes conversations with everything that belongs to them (transcript, file history, to-dos, prompt
    /// history lines, Claude Desktop sidebar entries). It all goes to the Recycle Bin as one folder.
    /// Claude Desktop is closed while it happens. Returns how many conversations were deleted.
    /// </summary>
    public async Task<int> DeleteConversationsAsync(IReadOnlyCollection<string> sessionIds)
    {
        var found = 0;
        await RunAsync(() =>
        {
            if (sessionIds.Count == 0) return Task.CompletedTask;
            var staging = Path.Combine(Paths.AppDataDir, "deleted", "Claude conversations " + DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss"));
            WithClaudeClosed(relaunch: false, work: () =>
            {
                var (count, undo) = Spaces.GatherForDelete(State, sessionIds, staging);
                try
                {
                    Desktop.RemoveSessionEntries(State, sessionIds, staging);
                }
                catch
                {
                    undo();
                    throw;
                }
                found = count;
            });
            if (Directory.Exists(staging))
            {
                try { _host.DeleteFolder(staging); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Log($"Deleted {found} conversation(s), but the Recycle Bin could not take them. They are in {staging}.", error: true);
                    return Task.CompletedTask;
                }
            }
            Log($"Deleted {found} conversation(s). They are in the Recycle Bin.");
            return Task.CompletedTask;
        });
        return found;
    }

    /// <summary>
    /// Puts conversations back in Claude Desktop's sidebar. Claude Desktop reads its list only when it starts, so it is
    /// closed while this happens and started again if it was open. Returns how many were put back.
    /// </summary>
    public async Task<int> ShowInDesktopAsync(IReadOnlyCollection<string> sessionIds)
    {
        var shown = 0;
        await RunAsync(() =>
        {
            Desktop.Reconcile(State);
            var install = Desktop.Install ?? throw new DeckException("Claude Desktop was not found on this computer.");
            var wasRunning = _host.IsRunning(install);
            if (!_host.Close(install, TimeSpan.FromSeconds(20)))
                throw new DeckException("Claude Desktop did not close. Quit it from its tray or menu bar icon and try again.");
            try
            {
                shown = Desktop.ShowInSidebar(State, sessionIds, SpaceItems.SessionOwners(Paths, State.Spaces));
            }
            finally
            {
                if (wasRunning) _host.Launch(install);
            }
            Log(shown == sessionIds.Count
                ? $"Put {shown} conversation(s) back in Claude Desktop's list."
                : $"Put {shown} of {sessionIds.Count} conversation(s) back in Claude Desktop's list. The others could not be added: their transcript or folder is missing, or Claude Desktop has no account signed in for their space.");
            return Task.CompletedTask;
        });
        return shown;
    }

    /// <summary>Puts a project folder in another group (null: back to the suggested one).</summary>
    public Task SetProjectGroupAsync(IEnumerable<string> projectPaths, string? group) => RunAsync(() =>
    {
        foreach (var path in projectPaths)
        {
            var key = ProjectGroups.Normalize(path);
            if (string.IsNullOrWhiteSpace(group)) State.ProjectGroups.Remove(key);
            else State.ProjectGroups[key] = group.Trim();
        }
        return Task.CompletedTask;
    });

    public Task UpdateSpacesAsync(Action<SpaceSettings> change) => RunAsync(() =>
    {
        change(State.Spaces);
        return Task.CompletedTask;
    });

    /// <summary>Brings every space's data back into ~/.claude. Returns what had to stay parked.</summary>
    public async Task<List<string>> TurnOffSpacesAsync()
    {
        var leftovers = new List<string>();
        await RunAsync(() =>
        {
            WithClaudeClosed(relaunch: false, work: () =>
            {
                leftovers = Spaces.TurnOff(State);
                Desktop.RestoreHiddenSessions(State);
            });
            Log(leftovers.Count == 0
                ? "Spaces are turned off. All conversations are back together."
                : $"Spaces are turned off. {leftovers.Count} item(s) would have overwritten something and stay in {Paths.SpacesStore}.");
            return Task.CompletedTask;
        });
        return leftovers;
    }

    /// <summary>
    /// Nothing may hold ~/.claude while its folders move: closes Claude Desktop (and everything it started),
    /// refuses if a terminal Claude Code is still open, runs <paramref name="work"/>, then starts Claude Desktop
    /// again if it was open (or always, with <paramref name="relaunch"/>, as account switches do).
    /// </summary>
    private void WithClaudeClosed(bool relaunch, Action work)
    {
        Desktop.Reconcile(State);
        var install = Desktop.Install;
        var wasRunning = install is not null && _host.IsRunning(install);
        if (install is not null && !_host.Close(install, TimeSpan.FromSeconds(20)))
            throw new DeckException("Claude Desktop did not close. Quit it from its tray or menu bar icon and try again.");
        try
        {
            var others = _host.RunningClaudeCode();
            if (others.Count > 0)
                throw new DeckException("Claude Code is still running in a terminal or editor (" + string.Join(", ", others) +
                                        "). Close those sessions first, so no conversation is cut off mid-write. Nothing was changed.");
            work();
        }
        finally
        {
            if (install is not null && (wasRunning || relaunch)) _host.Launch(install);
        }
    }

    public Task AddDesktopAsync(string name, bool copySettings) => RunAsync(() =>
    {
        var p = Desktop.AddNew(State, name, copySettings);
        Log($"Added Claude Desktop account \"{p.Name}\". Sign in when Claude opens.");
        return Task.CompletedTask;
    });

    public Task RemoveAsync(string id) => RunAsync(() =>
    {
        var p = State.Find(id) ?? throw new DeckException("That account no longer exists.");
        if (p.Kind == AccountKind.Desktop) Desktop.Remove(State, id);
        else Cli(p.Kind).Remove(State, id);
        Log($"Removed {Label(p.Kind)} account \"{p.Name}\".");
        return Task.CompletedTask;
    });

    public Task RenameAsync(string id, string name) => RunAsync(() =>
    {
        var p = State.Find(id) ?? throw new DeckException("That account no longer exists.");
        name = name.Trim();
        if (name.Length == 0) throw new DeckException("The name cannot be empty.");
        if (p.Kind == AccountKind.Desktop) Desktop.Rename(State, p, name);
        else p.Name = name;
        return Task.CompletedTask;
    });

    public Task UpdateSettingsAsync(Action<Settings> change) => RunAsync(() =>
    {
        change(State.Settings);
        return Task.CompletedTask;
    });

    public static string Label(AccountKind kind) => kind switch
    {
        AccountKind.Desktop => "Claude Desktop",
        AccountKind.ClaudeCode => "Claude Code",
        _ => "Codex",
    };

    private void Scan()
    {
        Spaces.Reconcile(State, m => Log(m));
        ClaudeCode.Sync(State, m => Log(m));
        Codex.Sync(State, m => Log(m));
        Desktop.Reconcile(State, m => Log(m));
    }

    private readonly HashSet<string> _notified = new();

    private void CheckLimits()
    {
        var s = State.Settings;
        var active = State.Active(AccountKind.ClaudeCode);
        var peak = active?.Usage?.Peak;
        if (active is null || peak is null) return;

        if (s.AutoSwitchClaudeCode && peak >= s.AutoSwitchThreshold)
        {
            var best = State.Of(AccountKind.ClaudeCode)
                .Where(p => p != active && p.Usage is { Error: null, Peak: not null } u && u.Peak < s.AutoSwitchThreshold
                            // Never jump to an account of another space: its conversations are parked.
                            && (!State.Spaces.Enabled || State.Spaces.Owner(p) is null || State.Spaces.Owner(p)!.Id == State.Spaces.ActiveId)
                            && u.At > DateTimeOffset.Now.AddMinutes(-30))
                .OrderBy(p => p.Usage!.Peak)
                .FirstOrDefault();
            if (best is not null)
            {
                ClaudeCode.Switch(State, best.Id);
                Log($"Auto-switched Claude Code from \"{active.Name}\" ({peak:0}% used) to \"{best.Name}\" ({best.Usage!.Peak:0}% used).");
                Notify?.Invoke("Claude Code account switched", $"\"{active.Name}\" reached {peak:0}% of its limit. Now using \"{best.Name}\".");
                return;
            }
        }

        if (s.NotifyOnHighUsage && peak >= s.NotifyThreshold)
        {
            // One notification per account per limit window.
            var window = (active.Usage!.FiveHour >= s.NotifyThreshold ? active.Usage.FiveHourResetsAt : active.Usage.SevenDayResetsAt)?.ToString("u") ?? "";
            if (_notified.Add(active.Id + window))
                Notify?.Invoke("Claude Code limit almost reached", $"\"{active.Name}\" has used {peak:0}% of its plan limit.");
        }
    }

    private void Log(string message, bool error = false) =>
        State.Log.Add(new LogEntry { Message = message, IsError = error });

    private async Task RunAsync(Func<Task> work)
    {
        await _gate.WaitAsync();
        try
        {
            // File and process work must not block the UI thread.
            await Task.Run(work);
        }
        catch (Exception e)
        {
            Log(e is DeckException ? e.Message : $"Something went wrong: {e.Message}", error: true);
            throw;
        }
        finally
        {
            try { _store.Save(State); } catch (IOException) { }
            _gate.Release();
            Changed?.Invoke();
        }
    }
}
