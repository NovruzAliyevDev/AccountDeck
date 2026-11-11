using System.Net.Http;
using System.Windows;
using AccountDeck.Core;

namespace AccountDeck.App;

public partial class App : Application
{
    private Mutex? _mutex;
    private EventWaitHandle? _showSignal;
    private Tray? _tray;
    private MainWindow? _window;
    private MainViewModel? _vm;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--uninstall"))
        {
            Installer.Uninstall();
            Shutdown();
            return;
        }
        if (Installer.IsSetupFile)
        {
            Installer.Install();
            Shutdown();
            return;
        }
        if (e.Args.Contains("--diagnose"))
        {
            Diagnose(open: !e.Args.Contains("--no-open"));
            Shutdown();
            return;
        }
        var demo = e.Args.Contains("--demo");
        var startHidden = e.Args.Contains("--tray");

        // One running copy only: a second start just brings the first one to the front.
        var instance = demo ? "AccountDeck.Demo" : "AccountDeck";
        _mutex = new Mutex(true, instance + ".Mutex", out var first);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, instance + ".Show");
        if (!first)
        {
            _showSignal.Set();
            Shutdown();
            return;
        }
        new Thread(() => { while (_showSignal.WaitOne()) Dispatcher.BeginInvoke(ShowWindow); }) { IsBackground = true }.Start();

        DispatcherUnhandledException += (_, args) =>
        {
            Dialog.Error("Something went wrong", args.Exception.Message);
            args.Handled = true;
        };

        DeckService service;
        if (demo)
        {
            var (paths, host, http) = Demo.Create();
            service = new DeckService(paths, host, new DpapiProtector(), http);
        }
        else
        {
            var paths = AppPaths.Default();
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("AccountDeck/1.0");
            service = new DeckService(paths, new WindowsDesktopHost(paths), new DpapiProtector(), http);
        }

        _vm = new MainViewModel(service, demo);
        _window = new MainWindow(_vm, Quit);
        _tray = new Tray(_vm, ShowWindow, Quit);
        service.Notify += (title, text) => Dispatcher.BeginInvoke(() => _tray.Balloon(title, text));
        _window.HiddenToTray += () => _tray.Balloon("Account Deck is still running", "Find it next to the clock. Right-click the icon to switch accounts or quit.");

        if (!startHidden) _window.Show();
        _vm.Start();
    }

    /// <summary>
    /// "AccountDeck.exe --diagnose": writes what the app can see to a text file and opens it.
    /// Read-only: nothing is saved, moved or closed.
    /// </summary>
    private static void Diagnose(bool open)
    {
        var paths = AppPaths.Default();
        var host = new WindowsDesktopHost(paths);
        var install = host.Detect();
        var lines = new List<string> { "Account Deck diagnosis, " + DateTime.Now.ToString("g"), "" };
        if (install is null) lines.Add("Claude Desktop: not found");
        else
        {
            var processes = host.DescribeProcesses(install);
            lines.Add("Claude Desktop: " + (install.IsStore ? "Microsoft Store version" : "classic installer version"));
            lines.Add("  Data folder: " + install.DataDir + (System.IO.Directory.Exists(install.DataDir) ? "" : "  (missing)"));
            lines.Add("  Starts with: " + (install.Aumid ?? install.ExePath ?? "unknown"));
            lines.Add("  Running: " + (host.IsRunning(install) ? "yes" : "no"));
            lines.Add($"  A switch would close {processes.Count} process(es): " + string.Join(", ", processes));
        }
        var code = new ClaudeCodeSwitcher(paths, new DpapiProtector());
        lines.Add("Claude Code: " + (!code.IsInstalled ? "not set up" : code.ReadLive() is { } c ? "signed in as " + (c.Email ?? "unknown") : "signed out"));
        var codex = new CodexSwitcher(paths, new DpapiProtector());
        lines.Add("Codex: " + (!codex.IsInstalled ? "not set up" : codex.ReadLive() is { } x ? "signed in as " + (x.Email ?? x.Plan ?? "unknown") : "signed out"));

        var file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AccountDeck-diagnosis.txt");
        System.IO.File.WriteAllLines(file, lines);
        if (open) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = true });
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void Quit()
    {
        _tray?.Dispose();
        Shutdown();
    }
}
