using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace AccountDeck.App;

/// <summary>
/// The release is one self-contained file, AccountDeck-Setup.exe, and it is the app itself.
/// Started under a name containing "Setup" it installs: copies itself into the user's Programs
/// folder, adds a Start menu shortcut and an entry in Windows' "Installed apps", then opens the app.
/// No administrator rights and no other downloads are needed.
/// </summary>
public static class Installer
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\AccountDeck";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static string Target => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "AccountDeck");
    private static string TargetExe => Path.Combine(Target, "AccountDeck.exe");
    private static string Shortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Account Deck.lnk");

    public static string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    public static bool IsSetupFile =>
        Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "").Contains("Setup", StringComparison.OrdinalIgnoreCase);

    public static void Install()
    {
        var updating = File.Exists(TargetExe);
        var answer = Dialog.Ask(
            updating ? $"Update Account Deck to {Version}?" : $"Install Account Deck {Version}?",
            "Account Deck switches between your Claude Desktop, Claude Code and Codex accounts and shows how much of each plan limit is used.\n\n" +
            "It is installed for your Windows user only and appears in the Start menu." +
            (updating ? "\n\nYour saved accounts and settings are kept." : ""),
            updating ? "Update" : "Install");
        if (answer is null) return;

        try
        {
            StopInstalledCopies();
            if (Directory.Exists(Target)) Directory.Delete(Target, true);
            Directory.CreateDirectory(Target);
            File.Copy(Environment.ProcessPath!, TargetExe, true);

            CreateShortcut();
            using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                key.SetValue("DisplayName", "Account Deck");
                key.SetValue("DisplayVersion", Version);
                key.SetValue("Publisher", "Account Deck");
                key.SetValue("DisplayIcon", TargetExe);
                key.SetValue("InstallLocation", Target);
                key.SetValue("UninstallString", $"\"{TargetExe}\" --uninstall");
                key.SetValue("EstimatedSize", (int)(new FileInfo(TargetExe).Length / 1024), RegistryValueKind.DWord);
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
            // Keep "start with Windows" pointing at the installed copy.
            using (var run = Registry.CurrentUser.OpenSubKey(RunKey, true))
                if (run?.GetValue("AccountDeck") is string) run.SetValue("AccountDeck", $"\"{TargetExe}\" --tray");

            Process.Start(new ProcessStartInfo(TargetExe) { UseShellExecute = true });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Dialog.Error("Account Deck could not be installed", e.Message);
        }
    }

    public static void Uninstall()
    {
        var answer = Dialog.Ask("Remove Account Deck?",
            "The program, its Start menu shortcut and its \"start with Windows\" entry are removed.\n\n" +
            "Your saved accounts are kept, so nothing you are signed in to is lost. Claude Desktop, Claude Code and Codex stay on the account they use now.",
            "Remove");
        if (answer is null) return;

        StopInstalledCopies();
        if (File.Exists(Shortcut)) File.Delete(Shortcut);
        using (var run = Registry.CurrentUser.OpenSubKey(RunKey, true)) run?.DeleteValue("AccountDeck", false);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);

        // A running program cannot delete its own file, so a hidden command does it a moment after we exit.
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 2 /nobreak >nul & rmdir /s /q \"{Target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(),
        });
    }

    private static void StopInstalledCopies()
    {
        foreach (var process in Process.GetProcessesByName("AccountDeck"))
        {
            if (process.Id == Environment.ProcessId) continue;
            try
            {
                if (process.MainModule?.FileName?.StartsWith(Target, StringComparison.OrdinalIgnoreCase) != true) continue;
                process.Kill();
                process.WaitForExit(5000);
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }

    private static void CreateShortcut()
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null) return;
        dynamic shell = Activator.CreateInstance(shellType)!;
        var link = shell.CreateShortcut(Shortcut);
        link.TargetPath = TargetExe;
        link.WorkingDirectory = Target;
        link.Description = "Switch and monitor Claude Desktop, Claude Code and Codex accounts";
        link.Save();
    }
}
