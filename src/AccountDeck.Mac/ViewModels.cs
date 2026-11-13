using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using AccountDeck.Core;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;

namespace AccountDeck.Mac;

public sealed class RelayCommand(Action run) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => run();
}

public sealed record UsageBar(string Label, double Percent, string Text, IBrush Brush);

public sealed class ProfileVM
{
    public required string Id { get; init; }
    public required AccountKind Kind { get; init; }
    public required string Name { get; init; }
    public string Detail { get; init; } = "";
    public bool HasDetail => Detail.Length > 0;
    public bool IsActive { get; init; }
    public bool CanSwitch => !IsActive;
    public List<UsageBar> Bars { get; init; } = new();
    public string Note { get; init; } = "";
    public bool HasNote => Note.Length > 0;
    public required ICommand Switch { get; init; }
    public required ICommand Rename { get; init; }
    public required ICommand Remove { get; init; }

    public string Signature => $"{Id}|{Name}|{Detail}|{IsActive}|{Note}|{string.Join(",", Bars.Select(b => b.Text))}";
}

public sealed class SectionVM
{
    public required AccountKind Kind { get; init; }
    public required string Title { get; init; }
    public string Status { get; init; } = "";
    public string Hint { get; init; } = "";
    public bool HasHint => Hint.Length > 0;
    public bool CanAdd { get; init; }
    public required ICommand Add { get; init; }
    public List<ProfileVM> Profiles { get; init; } = new();

    public string Signature => $"{Title}|{Status}|{Hint}|{CanAdd}|{string.Join(";", Profiles.Select(p => p.Signature))}";
}

public sealed record LogVM(string Time, string Message, bool IsError);

/// <summary>The macOS port of the Windows app's view model: same behaviour, macOS wording and system calls.</summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private static readonly IBrush Calm = Solid("#2F8F6B"), Warn = Solid("#C98A00"), Critical = Solid("#D13438");

    private readonly DeckService _service;
    private readonly DispatcherTimer _scanTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly DispatcherTimer _usageTimer = new();
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<FileSystemWatcher> _watchers = new();
    private bool _desktopRunning;
    private string _signature = "";

    public MainViewModel(DeckService service, bool isDemo)
    {
        _service = service;
        IsDemo = isDemo;
        RefreshCommand = new RelayCommand(() => _ = Run("Checking accounts…", _service.RefreshUsageAsync));
        OpenDataFolderCommand = new RelayCommand(() =>
        {
            var dir = _service.Paths.AppDataDir;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("/usr/bin/open", [dir]) { UseShellExecute = false })?.Dispose();
        });
        _service.Changed += () => Dispatcher.UIThread.Post(Rebuild);
    }

    public bool IsDemo { get; }
    public ObservableCollection<SectionVM> Sections { get; } = new();
    public ObservableCollection<LogVM> Log { get; } = new();
    public ICommand RefreshCommand { get; }
    public ICommand OpenDataFolderCommand { get; }
    public int[] RefreshChoices { get; } = [1, 5, 15, 30];
    public int[] ThresholdChoices { get; } = [70, 80, 90, 95, 100];

    /// <summary>Raised after the account list changed (the menu bar icon listens).</summary>
    public event Action? Rebuilt;

    public bool IsBusy { get; private set => Set(ref field, value); }
    public string BusyText { get; private set => Set(ref field, value); } = "";
    public string LastChecked { get; private set => Set(ref field, value); } = "";

    private Settings S => _service.State.Settings;

    public bool StartAtLogin
    {
        get => Autostart.IsEnabled;
        set
        {
            try { Autostart.Set(value); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or DeckException)
            {
                _ = Dialog.Error("That did not work", "Could not change the login item: " + e.Message);
            }
            Raise();
        }
    }
    public bool ShareDesktopSessions { get => S.ShareDesktopSessions; set => Change(s => s.ShareDesktopSessions = value); }
    public bool CloseToTray { get => S.CloseToTray; set => Change(s => s.CloseToTray = value); }
    public bool KeepSignInsFresh { get => S.KeepSignInsFresh; set => Change(s => s.KeepSignInsFresh = value); }
    public bool NotifyOnHighUsage { get => S.NotifyOnHighUsage; set => Change(s => s.NotifyOnHighUsage = value); }
    public int NotifyThreshold { get => S.NotifyThreshold; set => Change(s => s.NotifyThreshold = value); }
    public bool AutoSwitchClaudeCode { get => S.AutoSwitchClaudeCode; set => Change(s => s.AutoSwitchClaudeCode = value); }
    public int AutoSwitchThreshold { get => S.AutoSwitchThreshold; set => Change(s => s.AutoSwitchThreshold = value); }
    public int RefreshMinutes
    {
        get => S.RefreshMinutes;
        set { Change(s => s.RefreshMinutes = value); _usageTimer.Interval = TimeSpan.FromMinutes(Math.Max(1, value)); }
    }

    public void Start()
    {
        _scanTimer.Tick += async (_, _) => await Quietly(_service.ReloadAsync);
        _usageTimer.Interval = TimeSpan.FromMinutes(Math.Max(1, S.RefreshMinutes));
        _usageTimer.Tick += async (_, _) => await Quietly(_service.RefreshUsageAsync);
        _debounce.Tick += async (_, _) => { _debounce.Stop(); await Quietly(_service.ReloadAsync); };
        _scanTimer.Start();
        _usageTimer.Start();

        // Pick up a new sign-in the moment a tool writes it. On macOS Claude Code's tokens are in the Keychain,
        // which cannot be watched, but every login also rewrites ~/.claude.json, so that file is watched too.
        Watch(_service.Paths.ClaudeDir, ".credentials.json");
        Watch(_service.Paths.UserProfile, ".claude.json");
        Watch(_service.Paths.CodexDir, "auth.json");

        _ = Quietly(_service.RefreshUsageAsync);
    }

    public IEnumerable<(string Section, IEnumerable<ProfileVM> Profiles)> TrayItems =>
        Sections.Where(s => s.Profiles.Count > 0).Select(s => (s.Title, s.Profiles.AsEnumerable()));

    private void Watch(string dir, string file)
    {
        if (!Directory.Exists(dir)) return;
        var watcher = new FileSystemWatcher(dir, file) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
        void Poke() => Dispatcher.UIThread.Post(() => { _debounce.Stop(); _debounce.Start(); });
        watcher.Changed += (_, _) => Poke();
        watcher.Created += (_, _) => Poke();
        watcher.Renamed += (_, _) => Poke(); // atomic writes arrive as a rename
        watcher.EnableRaisingEvents = true;
        _watchers.Add(watcher);
    }

    private async Task Run(string busyText, Func<Task> work)
    {
        if (IsBusy) return;
        IsBusy = true;
        BusyText = busyText;
        try { await work(); }
        catch (Exception e) { await Dialog.Error("That did not work", e is DeckException ? e.Message : "Something unexpected happened: " + e.Message); }
        finally { IsBusy = false; }
    }

    /// <summary>Background refreshes never interrupt the user; failures land in the Activity tab.</summary>
    private async Task Quietly(Func<Task> work)
    {
        if (IsBusy) return;
        try { await work(); }
        catch (Exception) { /* already logged by the service */ }
    }

    private void Change(Action<Settings> change, [CallerMemberName] string? name = null)
    {
        change(S);
        _ = Quietly(() => _service.UpdateSettingsAsync(_ => { }));
        Raise(name);
    }

    private async void Rebuild()
    {
        var state = _service.State;
        _desktopRunning = await Task.Run(() => _service.Desktop.IsRunning);
        var sections = new List<SectionVM> { DesktopSection(state), CliSection(state, _service.ClaudeCode), CliSection(state, _service.Codex) };

        var signature = string.Join("#", sections.Select(s => s.Signature));
        if (signature != _signature)
        {
            _signature = signature;
            Sections.Clear();
            foreach (var s in sections) Sections.Add(s);
            Rebuilt?.Invoke();
        }

        if (Log.Count != state.Log.Count || (Log.Count > 0 && Log[0].Message != state.Log[^1].Message))
        {
            Log.Clear();
            foreach (var entry in state.Log.AsEnumerable().Reverse())
                Log.Add(new LogVM(entry.At.ToLocalTime().ToString("dd MMM HH:mm"), entry.Message, entry.IsError));
        }
        LastChecked = "Checked " + DateTime.Now.ToString("HH:mm");
    }

    private SectionVM DesktopSection(AppState state)
    {
        var install = _service.Desktop.Install;
        var profiles = state.Of(AccountKind.Desktop).Select(p => Profile(state, p)).ToList();
        return new SectionVM
        {
            Kind = AccountKind.Desktop,
            Title = "Claude Desktop",
            Status = install is null ? "Not installed" : _desktopRunning ? "Running" : "Not running",
            Hint = install is null ? "Claude Desktop was not found on this Mac."
                : profiles.Count == 0 ? "Open Claude Desktop and sign in once. Your account will then appear here."
                : profiles.Count == 1 ? S.ShareDesktopSessions
                    ? "Add a second account to switch between them. Your sessions, settings and files are shared by all accounts; only the sign-in changes."
                    : "Add a second account to switch between them. Each account keeps its own sessions, settings and sign-in."
                : "",
            CanAdd = install is not null && profiles.Count > 0,
            Add = new RelayCommand(AddDesktop),
            Profiles = profiles,
        };
    }

    private SectionVM CliSection(AppState state, CredentialSwitcher tool)
    {
        var profiles = state.Of(tool.Kind).Select(p => Profile(state, p)).ToList();
        var active = state.Active(tool.Kind);
        return new SectionVM
        {
            Kind = tool.Kind,
            Title = tool.Label,
            Status = !tool.IsInstalled ? "Not set up" : active is null ? "Signed out" : "Signed in",
            Hint = !tool.IsInstalled ? $"{tool.Label} has not been used on this Mac yet. Install it and sign in, and the account will appear here."
                : profiles.Count == 0 ? $"Nobody is signed in to {tool.Label}. Choose Add account to sign in."
                : profiles.Count == 1 ? "Choose Add account to sign in with another account. Both stay saved here."
                : "",
            CanAdd = tool.IsInstalled,
            Add = new RelayCommand(() => AddCli(tool)),
            Profiles = profiles,
        };
    }

    private ProfileVM Profile(AppState state, Profile p)
    {
        var bars = new List<UsageBar>();
        var note = "";
        var u = p.Usage;
        if (u?.Error is not null) note = u.Error;
        else if (u is not null)
        {
            if (u.FiveHour is { } five) bars.Add(Bar("5-hour limit", five, u.FiveHourResetsAt, "HH:mm"));
            if (u.SevenDay is { } seven) bars.Add(Bar("Weekly limit", seven, u.SevenDayResetsAt, "ddd HH:mm"));
            if (p.Kind == AccountKind.Desktop) note = "Recorded by Claude Desktop, " + u.At.ToString("dd MMM HH:mm");
        }
        else if (p.Kind == AccountKind.Codex) note = "Codex does not report usage.";

        var detail = string.Join(" · ", new[] { p.Email == p.Name ? null : p.Email, Plan(p.Plan) }.Where(x => !string.IsNullOrEmpty(x)));
        return new ProfileVM
        {
            Id = p.Id,
            Kind = p.Kind,
            Name = p.Name,
            Detail = detail,
            IsActive = state.IsActive(p),
            Bars = bars,
            Note = note,
            Switch = new RelayCommand(() => SwitchTo(p.Id)),
            Rename = new RelayCommand(() => Rename(p)),
            Remove = new RelayCommand(() => Remove(p)),
        };
    }

    private static UsageBar Bar(string label, double percent, DateTimeOffset? resets, string format)
    {
        var text = $"{percent:0}%" + (resets is { } r && r > DateTimeOffset.Now ? $" · resets {r.ToLocalTime().ToString(format)}" : "");
        return new UsageBar(label, Math.Clamp(percent, 0, 100), text, percent >= 90 ? Critical : percent >= 70 ? Warn : Calm);
    }

    private static string? Plan(string? plan) =>
        string.IsNullOrEmpty(plan) ? null : plan.Length <= 4 && plan != "API key" ? char.ToUpper(plan[0]) + plan[1..] + " plan" : plan;

    public async void SwitchTo(string id)
    {
        var p = _service.State.Find(id);
        if (p is null || _service.State.IsActive(p)) return;
        if (p.Kind == AccountKind.Desktop)
        {
            var answer = await Dialog.Ask("Switch Claude Desktop account",
                $"Claude Desktop will quit and reopen as \"{p.Name}\".\n\nAnything it is working on right now will be stopped.", "Switch");
            if (answer is null) return;
        }
        await Run($"Switching to {p.Name}…", () => _service.SwitchAsync(id));
    }

    private async void AddDesktop()
    {
        var shared = S.ShareDesktopSessions;
        var answer = await Dialog.Ask("Add a Claude Desktop account",
            "Your current sign-in is saved first. Claude Desktop then restarts signed out, and you log in with the other account.\n\n" +
            (shared ? "Your sessions, settings and files stay in place and will be available in both accounts." : "The new account starts with its own empty set of sessions and settings.") +
            "\n\nGive the new account a name:",
            "Add account", input: _service.State.NextName(AccountKind.Desktop),
            check: shared ? null : "Copy my app preferences to the new account", checkDefault: true);
        if (answer is null) return;
        await Run("Preparing a clean Claude Desktop…", () => _service.AddDesktopAsync(answer.Text, answer.Checked));
    }

    private async void AddCli(CredentialSwitcher tool)
    {
        var command = tool.Kind == AccountKind.ClaudeCode ? "claude auth login" : "codex login";
        var answer = await Dialog.Ask($"Add a {tool.Label} account",
            $"A Terminal window will open and run \"{command}\". Sign in there with the account you want to add.\n\n" +
            "The account you are using now stays saved, and the new one appears here as soon as you finish signing in.", "Open sign-in");
        if (answer is null) return;
        await Run("Saving the current account…", async () =>
        {
            await _service.ReloadAsync();
            if (IsDemo) throw new DeckException("Signing in is turned off in demo mode.");
            try
            {
                LoginTerminal.Open(tool.Label, command);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                throw new DeckException("Could not open a Terminal window.", e);
            }
        });
    }

    private async void Rename(Profile p)
    {
        var answer = await Dialog.Ask("Rename account", "Name shown in this app:", "Rename", input: p.Name);
        if (answer is null) return;
        await Run("Renaming…", () => _service.RenameAsync(p.Id, answer.Text));
    }

    private async void Remove(Profile p)
    {
        var what = p.Kind == AccountKind.Desktop && S.ShareDesktopSessions
            ? "Only the saved sign-in is removed. Sessions and settings are shared and stay in place. Your account itself is not affected."
            : p.Kind == AccountKind.Desktop
            ? "Its chat history on this Mac, settings and sign-in will be moved to the Trash. Your account itself and anything stored online are not affected."
            : "Only the saved sign-in on this Mac is removed. Your account itself is not affected.";
        if (await Dialog.Ask($"Remove \"{p.Name}\"?", what, "Remove") is null) return;
        await Run("Removing…", () => _service.RemoveAsync(p.Id));
    }

    private static IBrush Solid(string hex) => new ImmutableSolidColorBrush(Color.Parse(hex));

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private void Set<T>(ref T storage, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value)) return;
        storage = value;
        Raise(name);
    }
}
