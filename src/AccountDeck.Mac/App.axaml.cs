using System.Security.Cryptography;
using AccountDeck.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace AccountDeck.Mac;

public partial class App : Application
{
    private IClassicDesktopStyleApplicationLifetime? _lifetime;
    private FileStream? _instanceLock;
    private MainWindow? _window;
    private MainViewModel? _vm;
    private TrayIcon? _tray;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime lifetime) return;
        _lifetime = lifetime;
        var args = lifetime.Args ?? [];

        var export = Array.IndexOf(args, "--export-icon");
        if (export >= 0 && export + 1 < args.Length)
        {
            AppIcon.Export(args[export + 1]);
            Dispatcher.UIThread.Post(() => lifetime.Shutdown());
            return;
        }

        var demo = args.Contains("--demo");
        var startHidden = args.Contains("--tray");

        DeckService service;
        if (demo)
        {
            // A throwaway key: the demo must not create anything in the real Keychain.
            var key = RandomNumberGenerator.GetBytes(32);
            var protector = new AesGcmProtector(() => key);
            var (paths, host, http) = Demo.Create(protector);
            service = new DeckService(paths, host, protector, http);
        }
        else
        {
            var paths = AppPaths.Default();
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("AccountDeck/1.0");
            service = new DeckService(paths, new MacDesktopHost(paths), MacKeychainProtector.Create(), http, new MacClaudeCredentialStore(paths));
        }

        // One running copy only. Inside the .app bundle macOS already ensures this; the lock covers direct starts.
        if (!TakeInstanceLock(service.Paths.AppDataDir))
        {
            Dispatcher.UIThread.Post(() => lifetime.Shutdown());
            return;
        }

        _vm = new MainViewModel(service, demo);
        _window = new MainWindow(_vm, Quit);
        Dialog.Owner = () => { ShowWindow(); return _window; };
        BuildTray();
        _vm.Rebuilt += RebuildTrayMenu;
        service.Notify += (title, text) => Dispatcher.UIThread.Post(() => Notifier.Show(title, text));
        _window.HiddenToTray += () => Notifier.Show("Account Deck is still running",
            "Find it in the menu bar. Click the icon to switch accounts or quit.");

        lifetime.ShutdownRequested += (_, _) => { if (_window is not null) _window.Quitting = true; };
        // Clicking the Dock icon while the window is hidden brings it back.
        if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
            activatable.Activated += (_, e) => { if (e.Kind == ActivationKind.Reopen) ShowWindow(); };

        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            e.Handled = true;
            _ = Dialog.Error("Something went wrong", e.Exception.Message);
        };

        if (!startHidden) _window.Show();
        _vm.Start();
    }

    private bool TakeInstanceLock(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            // FileShare.None takes an exclusive advisory lock on macOS, released automatically if the app dies.
            _instanceLock = new FileStream(Path.Combine(dir, ".instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private void BuildTray()
    {
        _tray = new TrayIcon
        {
            Icon = AppIcon.Window(44),
            ToolTipText = _vm!.IsDemo ? "Account Deck (demo)" : "Account Deck",
            Menu = new NativeMenu(),
            IsVisible = true,
        };
        TrayIcon.SetIcons(this, [_tray]);
        RebuildTrayMenu();
    }

    private void RebuildTrayMenu()
    {
        if (_tray?.Menu is not { } menu || _vm is null) return;
        menu.Items.Clear();
        foreach (var (section, profiles) in _vm.TrayItems)
        {
            menu.Items.Add(new NativeMenuItem(section) { IsEnabled = false });
            foreach (var p in profiles)
            {
                var id = p.Id;
                var item = new NativeMenuItem(p.Name) { ToggleType = NativeMenuItemToggleType.CheckBox, IsChecked = p.IsActive };
                item.Click += (_, _) => _vm.SwitchTo(id);
                menu.Items.Add(item);
            }
            menu.Items.Add(new NativeMenuItemSeparator());
        }
        var open = new NativeMenuItem("Open Account Deck");
        open.Click += (_, _) => ShowWindow();
        var refresh = new NativeMenuItem("Refresh");
        refresh.Click += (_, _) => _vm.RefreshCommand.Execute(null);
        var quit = new NativeMenuItem("Quit Account Deck");
        quit.Click += (_, _) => Quit();
        menu.Items.Add(open);
        menu.Items.Add(refresh);
        menu.Items.Add(quit);
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
        if (_window is not null) _window.Quitting = true;
        if (_tray is not null) _tray.IsVisible = false;
        _instanceLock?.Dispose();
        _lifetime?.Shutdown();
    }
}
