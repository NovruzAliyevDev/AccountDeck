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

    public AppPaths Paths { get; }
    public AppState State { get; private set; }
    public DesktopSwitcher Desktop { get; }
    public ClaudeCodeSwitcher ClaudeCode { get; }
    public CodexSwitcher Codex { get; }

    /// <summary>Raised (on any thread) after the state changed.</summary>
    public event Action? Changed;
    /// <summary>Raised when something worth a notification happened: title, text.</summary>
    public event Action<string, string>? Notify;

    /// <param name="claudeCredentials">Where Claude Code keeps its tokens; null means ~/.claude/.credentials.json (Windows, Linux, tests).</param>
    public DeckService(AppPaths paths, IDesktopHost host, ISecretProtector protector, HttpClient http, ICredentialStore? claudeCredentials = null)
    {
        Paths = paths;
        _store = new StateStore(paths);
        _api = new ClaudeApi(http);
        State = _store.Load();
        Desktop = new DesktopSwitcher(paths, host);
        ClaudeCode = new ClaudeCodeSwitcher(paths, protector, claudeCredentials);
        Codex = new CodexSwitcher(paths, protector);
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
        if (p.Kind == AccountKind.Desktop) Desktop.Switch(State, id);
        else Cli(p.Kind).Switch(State, id);
        Log($"Switched {Label(p.Kind)} to \"{p.Name}\".");
        return Task.CompletedTask;
    });

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
