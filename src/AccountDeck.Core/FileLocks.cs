using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AccountDeck.Core;

/// <summary>Names the programs that keep a folder from being moved, so an error can say who to close.</summary>
public static class FileLocks
{
    /// <summary>Files asked about at most; a folder with more is only partly checked.</summary>
    private const int MaxFiles = 5000;

    /// <summary>Programs holding files in <paramref name="path"/> or running from inside it, as "name (pid)". Empty when unknown.</summary>
    public static IReadOnlyList<string> Holders(string path)
    {
        if (!OperatingSystem.IsWindows()) return [];
        try
        {
            var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = Directory.Exists(path)
                ? Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }).Take(MaxFiles).ToArray()
                : File.Exists(path) ? [path] : [];
            for (var i = 0; i < files.Length; i += 500)
                foreach (var who in RestartManager(files[i..Math.Min(i + 500, files.Length)])) found.Add(who);

            var inside = path.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.MainModule?.FileName is { } exe && exe.StartsWith(inside, StringComparison.OrdinalIgnoreCase))
                        found.Add($"{p.ProcessName} ({p.Id})");
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            }
            return found.ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return [];
        }
    }

    /// <summary>The text an error adds after the folder name: who holds it, or what else can.</summary>
    public static string Describe(string path)
    {
        var holders = Holders(path);
        return holders.Count > 0
            ? " It is held by " + string.Join(", ", holders) + "."
            : " No program has a file in it open, so a terminal, editor or Explorer window may be opened inside that folder.";
    }

    [SupportedOSPlatform("windows")]
    private static List<string> RestartManager(string[] files)
    {
        var result = new List<string>();
        if (RmStartSession(out var session, 0, Guid.NewGuid().ToString("N")) != 0) return result;
        try
        {
            if (RmRegisterResources(session, (uint)files.Length, files, 0, null, 0, null) != 0) return result;
            uint needed = 0, count = 0, reasons = 0;
            var rc = RmGetList(session, out needed, ref count, null, ref reasons);
            if (rc != ErrorMoreData || needed == 0) return result;
            var info = new RmProcessInfo[needed];
            count = needed;
            if (RmGetList(session, out needed, ref count, info, ref reasons) != 0) return result;
            for (var i = 0; i < count; i++) result.Add($"{info[i].AppName} ({info[i].Process.ProcessId})");
        }
        finally
        {
            RmEndSession(session);
        }
        return result;
    }

    private const int ErrorMoreData = 234;

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int ProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME StartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint session, int flags, string key);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint session);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint session, uint files, string[] fileNames, uint apps, RmUniqueProcess[]? processes, uint services, string[]? serviceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] RmProcessInfo[]? info, ref uint rebootReasons);
}
