using System.Diagnostics;
using System.Security;
using AccountDeck.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace AccountDeck.Mac;

/// <summary>The app icon is drawn in code, so the repository carries no binary assets.</summary>
public static class AppIcon
{
    private static readonly Color Tile = Color.FromRgb(0x2F, 0x8F, 0x6B);

    public static Bitmap Render(int size)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var g = bitmap.CreateDrawingContext())
        {
            double s = size;
            // macOS icons leave a margin around the tile; the Dock and Finder expect it.
            var inset = s * 0.09;
            var t = s - inset * 2;
            g.DrawRectangle(new SolidColorBrush(Tile), null, new RoundedRect(new Rect(inset, inset, t, t), t * 0.22));
            Card(g, Color.FromArgb(150, 255, 255, 255), inset + t * 0.34, inset + t * 0.20, t);   // the account behind
            Card(g, Colors.White, inset + t * 0.20, inset + t * 0.34, t);                          // the account in front
        }
        return bitmap;
    }

    private static void Card(DrawingContext g, Color color, double x, double y, double t) =>
        g.DrawRectangle(new SolidColorBrush(color), null, new RoundedRect(new Rect(x, y, t * 0.46, t * 0.46), t * 0.10));

    public static WindowIcon Window(int size)
    {
        using var stream = new MemoryStream();
        Render(size).Save(stream);
        stream.Position = 0;
        return new WindowIcon(stream);
    }

    /// <summary>"AccountDeck --export-icon file.png": used by build-mac.sh to make the .icns for the bundle.</summary>
    public static void Export(string path, int size = 1024) => Render(size).Save(path);
}

/// <summary>
/// "Start at login" is a per-user LaunchAgent in ~/Library/LaunchAgents; no administrator rights needed.
/// macOS lists it under System Settings > General > Login Items.
/// </summary>
public static class Autostart
{
    private const string Label = "io.github.novruzaliyevdev.accountdeck";
    private static string PlistPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", Label + ".plist");

    public static bool IsEnabled => File.Exists(PlistPath);

    public static void Set(bool enabled)
    {
        if (!enabled)
        {
            File.Delete(PlistPath);
            return;
        }
        var exe = Environment.ProcessPath ?? throw new DeckException("Could not find where Account Deck is running from.");
        // Inside an .app bundle, start it through LaunchServices so it gets its Dock icon and single-instance handling.
        var marker = ".app/Contents/MacOS/";
        var at = exe.IndexOf(marker, StringComparison.Ordinal);
        string[] args = at >= 0 ? ["/usr/bin/open", "-a", exe[..(at + 4)], "--args", "--tray"] : [exe, "--tray"];

        var items = string.Concat(args.Select(a => $"\n        <string>{SecurityElement.Escape(a)}</string>"));
        var plist = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key>
                <string>{Label}</string>
                <key>ProgramArguments</key>
                <array>{items}
                </array>
                <key>RunAtLoad</key>
                <true/>
            </dict>
            </plist>
            """;
        FileUtil.WriteAtomic(PlistPath, plist);
    }
}

/// <summary>macOS notifications, through AppleScript so no signing or entitlements are needed.</summary>
public static class Notifier
{
    public static void Show(string title, string text)
    {
        try
        {
            // Title and text go in as arguments, never spliced into the script.
            var info = new ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false, RedirectStandardError = true };
            foreach (var a in new[] { "-e", "on run argv", "-e", "display notification (item 2 of argv) with title (item 1 of argv)", "-e", "end run", title, text })
                info.ArgumentList.Add(a);
            Process.Start(info)?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // A missed notification is not worth interrupting the user for.
        }
    }
}

/// <summary>Opens Terminal running a tool's own login command, the way the Windows app opens cmd.exe.</summary>
public static class LoginTerminal
{
    public static void Open(string label, string command)
    {
        // A .command file opens in Terminal without asking for permission to control it.
        var file = Path.Combine(Path.GetTempPath(), "AccountDeck-sign-in-" + command.Split(' ')[0] + ".command");
        // -il: a login, interactive shell loads the user's PATH, so claude/codex installed via npm, Homebrew or nvm are found.
        File.WriteAllText(file, $"""
            #!/bin/zsh -il
            clear
            echo "Account Deck: signing in to {label}. Use the account you want to add."
            echo
            {command}
            echo
            echo "Done. You can close this window and go back to Account Deck."
            """);
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Process.Start(new ProcessStartInfo("/usr/bin/open", [file]) { UseShellExecute = false })?.Dispose();
    }
}

/// <summary>
/// "AccountDeck --diagnose": writes what the app can see to a text file and opens it.
/// Read-only: nothing is saved, moved or closed, and no sign-in data is written to the report.
/// </summary>
public static class Diagnosis
{
    public static int Run(bool open)
    {
        var paths = AppPaths.Default();
        var host = new MacDesktopHost(paths);
        var install = host.Detect();
        var lines = new List<string> { "Account Deck diagnosis (macOS), " + DateTime.Now.ToString("g"), "" };
        if (install is null) lines.Add("Claude Desktop: not found");
        else
        {
            var processes = host.DescribeProcesses(install);
            lines.Add("Claude Desktop: " + (install.ExePath ?? "app not found, data folder only"));
            lines.Add("  Bundle id: " + install.Aumid);
            lines.Add("  Data folder: " + install.DataDir + (Directory.Exists(install.DataDir) ? "" : "  (missing)"));
            lines.Add("  Running: " + (host.IsRunning(install) ? "yes" : "no"));
            lines.Add($"  A switch would close {processes.Count} process(es): " + string.Join(", ", processes));
        }
        lines.Add("Account Deck data: " + paths.AppDataDir);
        // The protector is never used here: reading the live sign-in needs no decryption.
        var code = new ClaudeCodeSwitcher(paths, new AesGcmProtector(() => new byte[32]), new MacClaudeCredentialStore(paths));
        lines.Add("Claude Code: " + (!code.IsInstalled ? "not set up" : code.ReadLive() is { } c ? "signed in as " + (c.Email ?? "unknown") : "signed out"));
        lines.Add("  Tokens: " + code.CredentialLocation);
        var codex = new CodexSwitcher(paths, new AesGcmProtector(() => new byte[32]));
        lines.Add("Codex: " + (!codex.IsInstalled ? "not set up" : codex.ReadLive() is { } x ? "signed in as " + (x.Email ?? x.Plan ?? "unknown") : "signed out"));
        lines.Add("  Sign-in file: " + paths.CodexAuthFile);

        var file = Path.Combine(Path.GetTempPath(), "AccountDeck-diagnosis.txt");
        File.WriteAllLines(file, lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
        if (open) Process.Start(new ProcessStartInfo("/usr/bin/open", [file]) { UseShellExecute = false })?.Dispose();
        return 0;
    }
}
