using System.Windows;

namespace AccountDeck.App;

/// <summary>The hub: conversations, storage and project groups for every account and space.</summary>
public partial class HubWindow : Window
{
    public HubWindow(HubViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Icon = AppIcon.Image;
        var owner = Application.Current.MainWindow;
        if (owner is { IsVisible: true }) Owner = owner;
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Loaded += async (_, _) =>
        {
            SearchBox.Focus();
            await vm.LoadAsync();
        };
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
