using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AccountDeck.Mac;

public sealed record DialogAnswer(string Text, bool Checked);

/// <summary>The one dialog the app uses: a question with an optional text box and check box.</summary>
public static class Dialog
{
    /// <summary>Returns the window dialogs belong to, showing it first if it is hidden. Set by <see cref="App"/>.</summary>
    public static Func<Window?> Owner { get; set; } = () => null;

    /// <summary>Returns null when the user cancels.</summary>
    public static async Task<DialogAnswer?> Ask(string title, string message, string okText,
        string? input = null, string? check = null, bool checkDefault = false, bool showCancel = true)
    {
        var owner = Owner();
        var accepted = false;

        var heading = new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        var body = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 0) };
        var box = new TextBox { Text = input ?? "", Margin = new(0, 12, 0, 0), IsVisible = input is not null };
        var tick = new CheckBox { Content = check, IsChecked = checkDefault, Margin = new(0, 12, 0, 0), IsVisible = check is not null };
        var cancel = new Button { Content = "Cancel", MinWidth = 88, IsCancel = true, IsVisible = showCancel, HorizontalContentAlignment = HorizontalAlignment.Center };
        var ok = new Button { Content = okText, MinWidth = 88, IsDefault = true, Margin = new(8, 0, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Classes.Add("accent");

        var window = new Window
        {
            Title = "Account Deck",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new(24, 20, 24, 20),
                Children =
                {
                    heading, body, box, tick,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Margin = new(0, 22, 0, 0),
                        Children = { cancel, ok },
                    },
                },
            },
        };
        ok.Click += (_, _) => { accepted = true; window.Close(); };
        cancel.Click += (_, _) => window.Close();
        if (input is not null)
            window.Opened += (_, _) => { box.Focus(); box.SelectAll(); };

        if (owner is not null) await window.ShowDialog(owner);
        else
        {
            var closed = new TaskCompletionSource();
            window.Closed += (_, _) => closed.TrySetResult();
            window.Show();
            await closed.Task;
        }

        return accepted ? new DialogAnswer((box.Text ?? "").Trim(), tick.IsChecked == true) : null;
    }

    public static Task Error(string title, string message) => Ask(title, message, "OK", showCancel: false);
}
