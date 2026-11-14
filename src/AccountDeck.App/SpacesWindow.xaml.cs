using System.Windows;
using System.Windows.Input;
using AccountDeck.Core;

namespace AccountDeck.App;

/// <summary>The spaces guide. DialogResult is true when the user finished it (Start or Save).</summary>
public partial class SpacesWindow : Window
{
    private readonly SpacesViewModel _vm;

    public SpacesWindow(SpacesViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Icon = AppIcon.Image;
        var owner = Application.Current.MainWindow;
        if (owner is { IsVisible: true }) Owner = owner;
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_vm.Next()) DialogResult = true;
        }
        catch (DeckException ex)
        {
            Dialog.Error("Please check this", ex.Message);
        }
    }

    private void OnBack(object sender, RoutedEventArgs e) => _vm.Back();

    private void OnRowClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ItemRowVM row }) _vm.Preview = row;
    }
}
