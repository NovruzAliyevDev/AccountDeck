using System.ComponentModel;
using System.Windows;

namespace AccountDeck.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _hintShown;

    /// <summary>Raised the first time the window hides to the tray instead of closing.</summary>
    public event Action? HiddenToTray;

    public MainWindow(MainViewModel vm, Action quit)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Icon = AppIcon.Image;
        Closing += (_, e) => OnClosing(e, quit);
    }

    private void OnClosing(CancelEventArgs e, Action quit)
    {
        if (!_vm.CloseToTray)
        {
            quit();
            return;
        }
        e.Cancel = true;
        Hide();
        if (_hintShown) return;
        _hintShown = true;
        HiddenToTray?.Invoke();
    }
}
