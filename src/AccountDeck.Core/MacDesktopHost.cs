using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AccountDeck.Core;

/// <summary>
/// Finds, closes and starts the real Claude Desktop on macOS.
/// The app lives in /Applications/Claude.app (or ~/Applications) and keeps its data in
/// ~/Library/Application Support/Claude, which is <see cref="AppPaths.Local"/> on macOS.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacDesktopHost(AppPaths paths) : IDesktopHost
{
    private const string DefaultBundleId = "com.anthropic.claudefordesktop";

    public DesktopInstall? Detect()
    {
        var data = Path.Combine(paths.Local, "Claude");
        var app = new[]
        {
            "/Applications/Claude.app",
            Path.Combine(paths.UserProfile, "Applications", "Claude.app"),
        }.FirstOrDefault(Directory.Exists);
        if (app is null && !File.Exists(Path.Combine(data, "config.json"))) return null;
        // Aumid carries the bundle identifier on macOS: it is what "open -b" and AppleScript address.
        return new DesktopInstall(data, false, app is null ? DefaultBundleId : BundleId(app), app);
    }

    public bool IsRunning(DesktopInstall install) => Targets(install, appOnly: true).Count > 0;

    public bool Close(DesktopInstall install, TimeSpan timeout)
    {
        if (Targets(install, appOnly: false).Count == 0) return true;

        // Ask politely first, so Claude can save its window state.
        if (Targets(install, appOnly: true).Count > 0)
        {
            Run("/usr/bin/osascript", "-e", $"tell application id \"{install.Aumid ?? DefaultBundleId}\" to quit");
            var politeDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Min(6, timeout.TotalSeconds / 2));
            while (DateTime.UtcNow < politeDeadline && Targets(install, appOnly: true).Count > 0) Thread.Sleep(300);
        }

        var deadline = DateTime.UtcNow + timeout;
        var signal = SIGTERM;
        while (DateTime.UtcNow < deadline)
        {
            var targets = Targets(install, appOnly: false);
            if (targets.Count == 0) return true;
            foreach (var p in targets) kill(p.Pid, signal);
            Thread.Sleep(600);
            signal = SIGKILL; // whatever ignored the first request is ended on the next pass
        }
        return Targets(install, appOnly: false).Count == 0;
    }

    /// <summary>What <see cref="Close"/> would end right now, for the --diagnose report. Changes nothing.</summary>
    public IReadOnlyList<string> DescribeProcesses(DesktopInstall install) =>
        Targets(install, appOnly: false).Select(p => $"{Path.GetFileName(p.Path)} ({p.Pid})").ToList();

    public void Launch(DesktopInstall install)
    {
        if (install.ExePath is not null) Run("/usr/bin/open", install.ExePath);
        else Run("/usr/bin/open", "-b", install.Aumid ?? DefaultBundleId);
    }

    /// <summary>Moves the folder to the Trash, so a removed account can still be recovered from there.</summary>
    public void DeleteFolder(string dir)
    {
        var trash = Path.Combine(paths.UserProfile, ".Trash");
        Directory.CreateDirectory(trash);
        var name = "AccountDeck " + Path.GetFileName(dir.TrimEnd('/'));
        var target = Path.Combine(trash, name);
        if (Directory.Exists(target) || File.Exists(target)) target += " " + DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss");
        FileUtil.MoveDirectory(dir, target, attempts: 4);
    }

    private sealed record ProcessInfo(int Pid, int ParentPid, string Path);

    /// <summary>
    /// Claude Desktop's own processes (anything inside Claude.app), anything running out of its data folder
    /// (the bundled Claude Code), and, unless <paramref name="appOnly"/>, everything those started (shells,
    /// MCP servers), because any of them can hold the data folder open. This app and its own parents are never included.
    /// </summary>
    private static List<ProcessInfo> Targets(DesktopInstall install, bool appOnly)
    {
        var all = ListProcesses();
        var parents = all.ToDictionary(p => p.Pid, p => p.ParentPid);
        var protectedIds = new HashSet<int>();
        for (var id = Environment.ProcessId; id > 1 && protectedIds.Add(id);)
            id = parents.GetValueOrDefault(id);

        var dataDir = install.DataDir.TrimEnd('/') + "/";
        var appDir = install.ExePath is null ? null : install.ExePath.TrimEnd('/') + "/";
        var hits = new HashSet<int>();
        foreach (var p in all)
        {
            var isApp = appDir is not null && p.Path.StartsWith(appDir, StringComparison.Ordinal);
            if (isApp || (!appOnly && p.Path.StartsWith(dataDir, StringComparison.Ordinal))) hits.Add(p.Pid);
        }

        if (!appOnly)
        {
            for (var grew = true; grew;)
            {
                grew = false;
                foreach (var p in all)
                {
                    if (hits.Contains(p.Pid) || !hits.Contains(p.ParentPid)) continue;
                    hits.Add(p.Pid);
                    grew = true;
                }
            }
        }

        // Never this app, its parents, or anything this app started itself (ps, osascript).
        var own = new HashSet<int> { Environment.ProcessId };
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var p in all)
                if (own.Contains(p.ParentPid)) grew |= own.Add(p.Pid);
        }
        hits.ExceptWith(protectedIds);
        hits.ExceptWith(own);
        return all.Where(p => hits.Contains(p.Pid)).ToList();
    }

    private static List<ProcessInfo> ListProcesses()
    {
        var (_, output) = Run("/bin/ps", "-axww", "-o", "pid=,ppid=,comm=");
        var list = new List<ProcessInfo>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3 && int.TryParse(parts[0], out var pid) && int.TryParse(parts[1], out var ppid))
                list.Add(new ProcessInfo(pid, ppid, parts[2].Trim()));
        }
        return list;
    }

    private static string BundleId(string app)
    {
        var (code, output) = Run("/usr/libexec/PlistBuddy", "-c", "Print :CFBundleIdentifier", Path.Combine(app, "Contents", "Info.plist"));
        var id = output.Trim();
        return code == 0 && id.Length > 0 ? id : DefaultBundleId;
    }

    private static (int Code, string Output) Run(string file, params string[] args)
    {
        var info = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) info.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(info)!;
            var output = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, output);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (-1, "");
        }
    }

    private const int SIGTERM = 15, SIGKILL = 9;

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);
}
