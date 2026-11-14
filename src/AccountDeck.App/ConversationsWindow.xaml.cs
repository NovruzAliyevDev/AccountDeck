using System.Windows;

namespace AccountDeck.App;

/// <summary>Lists every space's conversations and moves one to another space.</summary>
public partial class ConversationsWindow : Window
{
    public ConversationsWindow(ConversationsViewModel vm)
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
