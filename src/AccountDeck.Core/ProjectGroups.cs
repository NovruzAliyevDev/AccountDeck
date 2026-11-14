using System.Text.RegularExpressions;

namespace AccountDeck.Core;

/// <summary>
/// Claude Code knows folders, not products: one product is many project folders (repositories, sub-folders,
/// worktrees). A group gathers them. Groups are suggested from git and from which folders Claude was used in;
/// the user's changes are kept in <see cref="AppState.ProjectGroups"/>. Nothing on disk changes.
/// </summary>
public static partial class ProjectGroups
{
    /// <summary>The group of every project folder: the user's choice if there is one, otherwise the suggestion.</summary>
    public static Dictionary<string, string> Resolve(AppPaths paths, AppState state, IEnumerable<string> projectPaths)
    {
        var used = projectPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in used)
            result[path] = state.ProjectGroups.TryGetValue(Normalize(path), out var g) ? g : Suggest(paths, used, path);
        return result;
    }

    /// <summary>
    /// The repository the folder belongs to (a worktree belongs to the repository it was made from), then the
    /// nearest folder above that repository that Claude was also used in. Without one, the repository itself.
    /// </summary>
    public static string Suggest(AppPaths paths, IReadOnlyCollection<string> usedPaths, string path)
    {
        path = Normalize(path);
        if (QueueName(path) is { } queue) return queue;
        var repo = RepoOf(path);
        var used = new HashSet<string>(usedPaths.Select(Normalize), StringComparer.OrdinalIgnoreCase);
        for (var dir = Parent(repo); dir is not null && !IsRoot(paths, dir); dir = Parent(dir))
            if (used.Contains(dir)) return NameOf(paths, dir);
        return NameOf(paths, repo);
    }

    public static string Normalize(string path) => path.Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);

    /// <summary>The top folder of the git repository the path is in; for a worktree, the repository it came from.</summary>
    public static string RepoOf(string path)
    {
        for (var dir = path; dir is not null; dir = Parent(dir))
        {
            var git = Path.Combine(dir, ".git");
            if (Directory.Exists(git)) return dir;
            if (File.Exists(git))
            {
                // A worktree: ".git" is a file saying "gitdir: <repo>/.git/worktrees/<name>".
                var line = ReadFirstLine(git);
                var target = line?.StartsWith("gitdir:", StringComparison.Ordinal) == true ? Normalize(line[7..].Trim()) : null;
                var marker = Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar;
                var at = target?.IndexOf(marker, StringComparison.OrdinalIgnoreCase) ?? -1;
                return at > 0 ? target![..at] : dir;
            }
        }
        return ByName(path) ?? path;
    }

    /// <summary>Worktree folders that may be gone already, matched by the names the tools give them.</summary>
    private static string? ByName(string path)
    {
        var m = DesktopWorktree().Match(path);
        if (m.Success) return m.Groups[1].Value;
        m = WtFolder().Match(path);
        if (m.Success) return Path.Combine(m.Groups[1].Value, m.Groups[2].Value);
        return null;
    }

    private static string? QueueName(string path)
    {
        var m = QueueWorktree().Match(path);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string NameOf(AppPaths paths, string dir) =>
        string.Equals(dir, Normalize(paths.UserProfile), StringComparison.OrdinalIgnoreCase) ? "Home folder" : Path.GetFileName(dir) is { Length: > 0 } n ? n : dir;

    /// <summary>Folders that hold unrelated things and are never a group of their own.</summary>
    private static bool IsRoot(AppPaths paths, string dir)
    {
        var home = Normalize(paths.UserProfile);
        return Parent(dir) is null
               || string.Equals(dir, home, StringComparison.OrdinalIgnoreCase)
               || string.Equals(Parent(dir), home, StringComparison.OrdinalIgnoreCase); // Desktop, Documents, source…
    }

    private static string? Parent(string dir) => Path.GetDirectoryName(dir) is { Length: > 0 } p && p != dir ? p : null;

    private static string? ReadFirstLine(string file)
    {
        try { return File.ReadLines(file).FirstOrDefault(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    [GeneratedRegex(@"^(.+?)[\\/]\.claude[\\/]worktrees[\\/][^\\/]+", RegexOptions.IgnoreCase)]
    private static partial Regex DesktopWorktree();

    [GeneratedRegex(@"^(.+?)[\\/]wt[\\/]([^\\/-]+)-[^\\/]+", RegexOptions.IgnoreCase)]
    private static partial Regex WtFolder();

    [GeneratedRegex(@"[\\/]\.claude[\\/]queues[\\/]([^\\/]+?)-\d{8}-\d{4}-wt-\d+", RegexOptions.IgnoreCase)]
    private static partial Regex QueueWorktree();
}
