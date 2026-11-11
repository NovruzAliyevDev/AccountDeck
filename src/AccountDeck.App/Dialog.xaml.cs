using System.Windows;

namespace AccountDeck.App;

public sealed record DialogAnswer(string Text, bool Checked);

/// <summary>The one dialog the app uses: a question with an optional text box and check box.</summary>
public partial class Dialog : Window
{
    private Dialog() => InitializeComponent();

    /// <summary>Returns null when the user cancels.</summary>
    public static DialogAnswer? Ask(string title, string message, string okText,
        string? input = null, string? check = null, bool checkDefault = false, bool showCancel = true)
    {
        var dialog = new Dialog { Title = "Account Deck", Icon = AppIcon.Image };
        var owner = Application.Current.MainWindow;
        if (owner is { IsVisible: true } && owner != dialog) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        dialog.Heading.Text = title;
        dialog.Message.Text = message;
        dialog.Ok.Content = okText;
        if (!showCancel) dialog.Cancel.Visibility = Visibility.Collapsed;
        if (input is not null)
        {
            dialog.Input.Text = input;
            dialog.Input.Visibility = Visibility.Visible;
            dialog.Loaded += (_, _) => { dialog.Input.Focus(); dialog.Input.SelectAll(); };
        }
        if (check is not null)
        {
            dialog.Check.Content = check;
            dialog.Check.IsChecked = checkDefault;
            dialog.Check.Visibility = Visibility.Visible;
        }

        return dialog.ShowDialog() == true
            ? new DialogAnswer(dialog.Input.Text.Trim(), dialog.Check.IsChecked == true)
            : null;
    }

    public static void Error(string title, string message) => Ask(title, message, "OK", showCancel: false);

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
