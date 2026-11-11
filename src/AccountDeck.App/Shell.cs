using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace AccountDeck.App;

/// <summary>The app icon is drawn in code, so the repository carries no binary assets.</summary>
public static class AppIcon
{
    public static Icon Create(int size)
    {
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = size;
            using var tile = new SolidBrush(System.Drawing.Color.FromArgb(0x2F, 0x8F, 0x6B));
            using var back = new SolidBrush(System.Drawing.Color.FromArgb(150, 255, 255, 255));
            using var front = new SolidBrush(System.Drawing.Color.White);
            Rounded(g, tile, 0, 0, s, s, s * 0.22f);
            Rounded(g, back, s * 0.34f, s * 0.20f, s * 0.46f, s * 0.46f, s * 0.10f);   // the account behind
            Rounded(g, front, s * 0.20f, s * 0.34f, s * 0.46f, s * 0.46f, s * 0.10f);  // the account in front
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }

    public static ImageSource Image { get; } = Imaging.CreateBitmapSourceFromHIcon(
        Create(64).Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

    private static void Rounded(Graphics g, System.Drawing.Brush brush, float x, float y, float w, float h, float r)
    {
        using var path = new GraphicsPath();
        var d = r * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }
}

/// <summary>"Start with Windows" is a per-user Run entry; no administrator rights needed.</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "AccountDeck";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(Name) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(Name, $"\"{Environment.ProcessPath}\" --tray");
        else key.DeleteValue(Name, false);
    }
}

/// <summary>The icon next to the clock: quick switching without opening the window.</summary>
public sealed class Tray : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly MainViewModel _vm;
    private readonly Action _show, _quit;

    public Tray(MainViewModel vm, Action show, Action quit)
    {
        _vm = vm;
        _show = show;
        _quit = quit;
        _icon = new Forms.NotifyIcon
        {
            Icon = AppIcon.Create(32),
            Text = vm.IsDemo ? "Account Deck (demo)" : "Account Deck",
            Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip(),
        };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) show(); };
        _icon.BalloonTipClicked += (_, _) => show();
        vm.Rebuilt += BuildMenu;
        BuildMenu();
    }

    public void Balloon(string title, string text) => _icon.ShowBalloonTip(6000, title, text, Forms.ToolTipIcon.Info);

    private void BuildMenu()
    {
        var items = _icon.ContextMenuStrip!.Items;
        items.Clear();
        foreach (var (section, profiles) in _vm.TrayItems)
        {
            items.Add(new Forms.ToolStripMenuItem(section) { Enabled = false });
            foreach (var p in profiles)
            {
                var id = p.Id;
                var item = new Forms.ToolStripMenuItem(p.Name) { Checked = p.IsActive };
                item.Click += (_, _) => _vm.SwitchTo(id);
                items.Add(item);
            }
            items.Add(new Forms.ToolStripSeparator());
        }
        items.Add("Open Account Deck", null, (_, _) => _show());
        items.Add("Refresh", null, (_, _) => _vm.RefreshCommand.Execute(null));
        items.Add("Quit", null, (_, _) => _quit());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
