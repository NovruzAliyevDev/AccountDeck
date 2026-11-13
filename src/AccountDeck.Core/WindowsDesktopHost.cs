using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.VisualBasic.FileIO;

namespace AccountDeck.Core;

/// <summary>Finds, closes and starts the real Claude Desktop on Windows.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsDesktopHost(AppPaths paths) : IDesktopHost
{
    public DesktopInstall? Detect()
    {
        // Microsoft Store build: data lives in %LOCALAPPDATA%\Claude-Data\claude_<publisher>,
        // not in %APPDATA%\Claude (which only holds a "data has moved" marker).
        var packages = Path.Combine(paths.Local, "Packages");
        var package = Directory.Exists(packages) ? Directory.GetDirectories(packages, "Claude_*").FirstOrDefault() : null;
        if (package is not null)
        {
            var family = Path.GetFileName(package);
            var publisher = family[(family.IndexOf('_') + 1)..];
            var dataRoot = Path.Combine(paths.Local, "Claude-Data");
            var data = Path.Combine(dataRoot, "claude_" + publisher);
            var legacy = Path.Combine(package, "LocalCache", "Roaming", "Claude");
            // Older Store builds kept the data inside the package folder.
            if (!Directory.Exists(dataRoot) && File.Exists(Path.Combine(legacy, "config.json"))) data = legacy;
            return new DesktopInstall(data, true, family + "!Claude", null);
        }

        // Classic installer build.
        var exe = Path.Combine(paths.Local, "AnthropicClaude", "claude.exe");
        var roaming = Path.Combine(paths.Roaming, "Claude");
        if (File.Exists(exe) || File.Exists(Path.Combine(roaming, "config.json")))
            return new DesktopInstall(roaming, false, null, File.Exists(exe) ? exe : null);

        return null;
    }

    public bool IsRunning(DesktopInstall install) => Targets(install, appOnly: true).Count > 0;

    public bool Close(DesktopInstall install, TimeSpan timeout)
    {
        var targets = Targets(install, appOnly: false);
        if (targets.Count == 0) return true;

        foreach (var p in targets)
        {
            try { if (p.MainWindowHandle != IntPtr.Zero) p.CloseMainWindow(); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        // Claude hides to the tray when its window closes, so a polite close rarely ends it.
        Thread.Sleep(1500);

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            targets = Targets(install, appOnly: false);
            if (targets.Count == 0) return true;
            foreach (var p in targets)
            {
                try { p.Kill(); }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
            Thread.Sleep(400);
        }
        return Targets(install, appOnly: false).Count == 0;
    }

    /// <summary>What <see cref="Close"/> would end right now, for the --diagnose report. Changes nothing.</summary>
    public IReadOnlyList<string> DescribeProcesses(DesktopInstall install) =>
        Targets(install, appOnly: false).Select(p => $"{p.ProcessName} ({p.Id})").ToList();

    public void Launch(DesktopInstall install)
    {
        if (install.Aumid is not null)
            Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + install.Aumid) { UseShellExecute = false });
        else if (install.ExePath is not null)
            Process.Start(new ProcessStartInfo(install.ExePath) { UseShellExecute = true });
    }

    public void DeleteFolder(string dir) =>
        FileSystem.DeleteDirectory(dir, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);

    /// <summary>
    /// Claude Desktop's own processes, anything running out of its data folder (the bundled Claude Code),
    /// and, unless <paramref name="appOnly"/>, everything those started (shells, MCP servers), because any
    /// of them can hold the data folder open. This app and its own parents are never included.
    /// </summary>
    private static List<Process> Targets(DesktopInstall install, bool appOnly)
    {
        var parents = ParentMap();
        var protectedIds = new HashSet<int>();
        for (var id = Environment.ProcessId; id != 0 && protectedIds.Add(id);)
            id = parents.GetValueOrDefault(id);

        var dataDir = install.DataDir.TrimEnd('\\') + "\\";
        var exeDir = install.ExePath is null ? null : Path.GetDirectoryName(install.ExePath) + "\\";
        var all = Process.GetProcesses();
        var hits = new HashSet<int>();

        foreach (var p in all)
        {
            var path = ImagePath(p.Id);
            if (path is null) continue;
            var isApp = string.Equals(Path.GetFileName(path), "claude.exe", StringComparison.OrdinalIgnoreCase)
                        && (path.Contains("\\WindowsApps\\Claude_", StringComparison.OrdinalIgnoreCase)
                            || (exeDir is not null && path.StartsWith(exeDir, StringComparison.OrdinalIgnoreCase)));
            if (isApp || (!appOnly && path.StartsWith(dataDir, StringComparison.OrdinalIgnoreCase))) hits.Add(p.Id);
        }

        if (!appOnly)
        {
            // Add descendants, level by level.
            for (var grew = true; grew;)
            {
                grew = false;
                foreach (var (child, parent) in parents)
                {
                    if (!hits.Contains(parent) || hits.Contains(child)) continue;
                    // Windows reuses process ids: a "child" older than its "parent" is unrelated.
                    if (Started(all, child) is { } c && Started(all, parent) is { } p && c < p) continue;
                    hits.Add(child);
                    grew = true;
                }
            }
        }

        hits.ExceptWith(protectedIds);
        return all.Where(p => hits.Contains(p.Id)).ToList();
    }

    private static DateTime? Started(Process[] all, int pid)
    {
        try { return all.FirstOrDefault(p => p.Id == pid)?.StartTime; }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    private static string? ImagePath(int pid)
    {
        var handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static Dictionary<int, int> ParentMap()
    {
        var map = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(2 /* TH32CS_SNAPPROCESS */, 0);
        if (snapshot == new IntPtr(-1)) return map;
        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snapshot, ref entry)) return map;
            do map[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
            while (Process32Next(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return map;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")]
    private static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")]
    private static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32 entry);
}
