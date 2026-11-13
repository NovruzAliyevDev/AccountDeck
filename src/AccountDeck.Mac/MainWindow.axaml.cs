using Avalonia.Controls;

namespace AccountDeck.Mac;

public partial class MainWindow : Window
{
    private readonly MainViewModel? _vm;
    private bool _hintShown;

    /// <summary>Raised the first time the window hides to the menu bar instead of quitting.</summary>
    public event Action? HiddenToTray;

    /// <summary>Set once the app is quitting, so closing the window is no longer turned into hiding it.</summary>
    public bool Quitting { get; set; }

    public MainWindow() => InitializeComponent(); // for the designer

    public MainWindow(MainViewModel vm, Action quit) : this()
    {
        _vm = vm;
        DataContext = vm;
        Icon = AppIcon.Window(64);
        Closing += (_, e) =>
        {
            if (Quitting || e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown) return;
            if (!_vm.CloseToTray)
            {
                e.Cancel = true;
                quit();
                return;
            }
            e.Cancel = true;
            Hide();
            if (_hintShown) return;
            _hintShown = true;
            HiddenToTray?.Invoke();
        };
    }
}
