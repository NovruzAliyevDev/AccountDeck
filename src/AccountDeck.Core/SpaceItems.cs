using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AccountDeck.Core;

public enum ItemGroup { Instructions, Skills, Agents, Commands, Settings }

/// <summary>
/// One row of the "what do the spaces share" list.
/// <paramref name="Key"/> is the item's path relative to ~/.claude with forward slashes ("rules/lessons.md",
/// "skills/test-ticket"), or "home/.claude.json" for the file next to ~/.claude.
/// <paramref name="SectionText"/> is set for a section of CLAUDE.md that becomes its own rule file on setup.
/// </summary>
public sealed record SpaceItem(string Key, ItemGroup Group, string Title, string Help, string Text, string? SectionText = null)
{
    /// <summary>Settings-like items can also be "each space keeps its own copy".</summary>
    public bool CanBeSeparate => Group == ItemGroup.Settings;
}

/// <summary>One conversation of a project: its title (if it was given one), the first thing typed, and when.</summary>
public sealed record ConversationInfo(string SessionId, string? Title, string? FirstPrompt, DateTimeOffset? Started, DateTimeOffset LastUsed, long Bytes = 0);

/// <summary>A folder under ~/.claude/projects: one project path, with its conversations and memory.</summary>
public sealed record ProjectInfo(string DirName, string? ProjectPath, int Conversations, DateTimeOffset? LastUsed);

/// <summary>Reads what is in ~/.claude so the user can choose, row by row, what each space gets.</summary>
public static partial class SpaceItems
{
    public const string HomePrefix = "home/";
    public const string ClaudeMd = "CLAUDE.md";
    public const string ClaudeJson = HomePrefix + ".claude.json";

    /// <summary>Never shared: these are what "keep spaces separate" means.</summary>
    public static readonly string[] AlwaysSeparate = ["projects", "file-history", "history.jsonl", "todos", "plans", "paste-cache"];

    /// <summary>Settings-like items, offered as one row each.</summary>
    private static readonly (string Key, string Title, string Help)[] SettingItems =
    [
        ("settings.json", "Settings and permissions", "Allowed commands, hooks and model choice"),
        ("plugins", "Plugins", "Installed plugins and their data"),
        (ClaudeJson, "MCP servers and trusted folders", "Your MCP server list and which folders you trusted"),
    ];

    public static string LivePath(AppPaths paths, string key) => key.StartsWith(HomePrefix, StringComparison.Ordinal)
        ? Path.Combine(paths.UserProfile, Native(key[HomePrefix.Length..]))
        : Path.Combine(paths.ClaudeDir, Native(key));

    public static string ParkedPath(AppPaths paths, string spaceId, string key) => Path.Combine(paths.SpacesStore, spaceId, Native(key));

    private static string Native(string key) => key.Replace('/', Path.DirectorySeparatorChar);

    public static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>
    /// Every row to show. Before spaces are set up, each "##" section of CLAUDE.md is offered as its own rule
    /// (it is cut out into rules/ on setup). Items parked in another space are listed too.
    /// </summary>
    public static List<SpaceItem> List(AppPaths paths, AppState state)
    {
        var items = new List<SpaceItem>();
        var seen = new HashSet<string>();
        void Add(SpaceItem item) { if (seen.Add(item.Key)) items.Add(item); }

        var roots = new List<string> { paths.ClaudeDir };
        roots.AddRange(state.Spaces.List.Select(s => Path.Combine(paths.SpacesStore, s.Id)));

        if (!state.Spaces.Enabled)
        {
            var (preamble, sections) = SplitClaudeMd(ReadText(Path.Combine(paths.ClaudeDir, ClaudeMd)));
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(Path.Combine(paths.ClaudeDir, "rules")))
                foreach (var f in Directory.GetFiles(Path.Combine(paths.ClaudeDir, "rules"))) taken.Add(Path.GetFileName(f));
            if (HasContent(preamble))
                Add(new SpaceItem(ClaudeMd, ItemGroup.Instructions, "General", "The start of CLAUDE.md, before its first section", preamble));
            foreach (var (heading, text) in sections)
            {
                var file = UniqueName(Slug(heading), taken);
                Add(new SpaceItem("rules/" + file, ItemGroup.Instructions, heading, "Section of CLAUDE.md", text, text));
            }
        }
        else
        {
            foreach (var root in roots)
                if (File.Exists(Path.Combine(root, ClaudeMd)))
                    Add(new SpaceItem(ClaudeMd, ItemGroup.Instructions, "General", "CLAUDE.md", ReadText(Path.Combine(root, ClaudeMd))));
        }

        foreach (var root in roots)
        {
            foreach (var f in Files(Path.Combine(root, "rules"), "*.md"))
            {
                var text = ReadText(f);
                Add(new SpaceItem("rules/" + Path.GetFileName(f), ItemGroup.Instructions, FirstHeading(text) ?? Path.GetFileNameWithoutExtension(f), "Rule file", text));
            }
            foreach (var d in Dirs(Path.Combine(root, "skills")))
            {
                var skill = ReadText(Path.Combine(d, "SKILL.md"));
                if (skill.Length == 0) continue; // plugin-synced folders and the like are not the user's own skills
                var (name, description) = Frontmatter(skill);
                Add(new SpaceItem("skills/" + Path.GetFileName(d), ItemGroup.Skills, name ?? Path.GetFileName(d), description ?? "Skill", skill));
            }
            foreach (var f in Files(Path.Combine(root, "agents"), "*.md"))
            {
                var text = ReadText(f);
                var (name, description) = Frontmatter(text);
                Add(new SpaceItem("agents/" + Path.GetFileName(f), ItemGroup.Agents, name ?? Path.GetFileNameWithoutExtension(f), description ?? "Agent", text));
            }
            foreach (var f in Files(Path.Combine(root, "commands"), "*.md"))
            {
                var text = ReadText(f);
                var (_, description) = Frontmatter(text);
                Add(new SpaceItem("commands/" + Path.GetFileName(f), ItemGroup.Commands, "/" + Path.GetFileNameWithoutExtension(f), description ?? "Slash command", text));
            }
        }

        foreach (var (key, title, help) in SettingItems)
        {
            var present = Exists(LivePath(paths, key)) || state.Spaces.List.Any(s => Exists(ParkedPath(paths, s.Id, key)));
            if (present) Add(new SpaceItem(key, ItemGroup.Settings, title, help, ""));
        }
        return items;
    }

    /// <summary>
    /// A first guess for each row: settings are kept separate, and anything mentioning one of the
    /// <paramref name="workWords"/> goes to <paramref name="workSpaceId"/>; the rest is shared.
    /// </summary>
    public static ItemChoice Suggest(SpaceItem item, string workSpaceId, IEnumerable<string> workWords)
    {
        if (item.CanBeSeparate) return ItemChoice.Separate;
        var haystack = item.Title + "\n" + item.Text;
        return workWords.Any(w => w.Length > 0 && haystack.Contains(w, StringComparison.OrdinalIgnoreCase))
            ? ItemChoice.Only(workSpaceId)
            : ItemChoice.Shared;
    }

    /// <summary>
    /// Names found in the user's instructions and skills (company, product, tool and ticket names such as
    /// "Tayqa", "Jira", "ServicePortal", "TSC"), with the items that mention each, most used first, so the user
    /// can tick them instead of remembering them. A word counts when it is written with a capital letter in the
    /// middle of a sentence, or is a hyphenated identifier like "tayqasale-db". Words in every item are left out.
    /// </summary>
    public static List<(string Word, List<SpaceItem> Items)> FindNames(IEnumerable<SpaceItem> items, int max = 24)
    {
        var list = items.Where(i => i.Group != ItemGroup.Settings).ToList();
        var found = new Dictionary<string, (string Word, HashSet<SpaceItem> Items)>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in list)
        {
            var text = Regex.Replace(item.Title + ".\n" + item.Help + ".\n" + item.Text, "^---\\s*$[\\s\\S]*?^---\\s*$", "", RegexOptions.Multiline);
            foreach (Match m in NameLike().Matches(text))
            {
                var word = m.Groups["w"].Value;
                // A capital right after a sentence end or at the start of a line is just grammar.
                if (char.IsUpper(word[0]) && !word.Contains('-') && StartsSentence(text, m.Index)) continue;
                var key = word.Contains('-') ? word.ToLowerInvariant() : word;
                if (Common.Contains(key)) continue;
                if (!found.TryGetValue(key, out var entry)) found[key] = entry = (word, new HashSet<SpaceItem>());
                entry.Items.Add(item);
            }
        }
        return found.Values
            .Where(v => v.Items.Count < list.Count || list.Count == 1)
            .OrderByDescending(v => v.Items.Count).ThenBy(v => v.Word, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .Select(v => (v.Word, v.Items.ToList()))
            .ToList();
    }

    /// <summary>Capitalised words (Tayqa, ServicePortal, TST) and hyphenated identifiers (tayqasale-db); ticket keys give their prefix (TSC-6864 → TSC).</summary>
    [GeneratedRegex(@"(?<![\w/\\.@-])(?<w>[A-Z][A-Za-z0-9]{2,}|[a-z][a-z0-9]+(?:-[a-z0-9]+)+)(?![\w@])")]
    private static partial Regex NameLike();

    private static bool StartsSentence(string text, int index)
    {
        var i = index - 1;
        while (i >= 0 && (text[i] == ' ' || text[i] == '\t' || text[i] == '*' || text[i] == '`' || text[i] == '"' || text[i] == '(' || text[i] == '#')) i--;
        if (i < 0 || text[i] == '\n' || text[i] == '.' || text[i] == '!' || text[i] == '?' || text[i] == ':') return true;
        // List bullets: "- Word", "1. Word"
        return text[i] == '-' && (i == 0 || text[i - 1] == '\n' || text[i - 1] == ' ');
    }

    /// <summary>Capitalised words that are not names: product words every Claude setup mentions, and English that is often capitalised.</summary>
    private static readonly HashSet<string> Common = new(StringComparer.Ordinal)
    {
        "Claude", "Code", "Desktop", "CLAUDE", "MCP", "SQL", "API", "JSON", "README", "TODO", "URL", "PR", "IDE", "CLI", "UI",
        "The", "This", "That", "These", "Those", "When", "Then", "Never", "Always", "Use", "Do", "Don", "Not", "Before", "After",
        "Read", "Write", "Check", "Also", "And", "For", "With", "Only", "Each", "Every", "Any", "All", "Some", "Your", "You",
        "Windows", "Linux", "Mac", "macOS", "Git", "GitHub", "Markdown", "Yes", "No", "OK", "First", "Second", "Next", "Last",
        "auto-memory", "follow-up", "e-mail", "step-by-step", "read-only", "up-to-date", "per-project", "built-in", "set-up",
    };

    public static List<ProjectInfo> Projects(AppPaths paths)
    {
        var root = Path.Combine(paths.ClaudeDir, "projects");
        var list = new List<ProjectInfo>();
        foreach (var dir in Dirs(root))
        {
            var sessions = Directory.GetFiles(dir, "*.jsonl");
            DateTimeOffset? last = sessions.Length == 0 ? null : sessions.Max(f => new DateTimeOffset(File.GetLastWriteTimeUtc(f), TimeSpan.Zero));
            list.Add(new ProjectInfo(Path.GetFileName(dir), ProjectPathOf(sessions), sessions.Length, last));
        }
        return list.OrderByDescending(p => p.LastUsed).ToList();
    }

    /// <summary>
    /// The conversations of one project folder, newest first, so the user can see what a project is before
    /// choosing its space. The title is the one shown in Claude ("customTitle"); otherwise the first prompt.
    /// </summary>
    public static List<ConversationInfo> Conversations(AppPaths paths, string dirName) =>
        Conversations(Path.Combine(paths.ClaudeDir, "projects", dirName));

    /// <summary>
    /// The folder holding a space's projects, file history and prompt history: ~/.claude for the space in use,
    /// its parking folder for the others.
    /// </summary>
    public static string SpaceRoot(AppPaths paths, SpaceSettings s, string spaceId) =>
        spaceId == s.ActiveId ? paths.ClaudeDir : Path.Combine(paths.SpacesStore, spaceId);

    /// <summary>Every conversation of every space, newest first, with the project folder it belongs to.</summary>
    public static List<(Space Space, ProjectInfo Project, ConversationInfo Conversation)> AllConversations(AppPaths paths, SpaceSettings s)
    {
        var all = new List<(Space, ProjectInfo, ConversationInfo)>();
        foreach (var (space, root) in Roots(paths, s))
        {
            foreach (var dir in Dirs(Path.Combine(root, "projects")))
            {
                var sessions = Directory.GetFiles(dir, "*.jsonl");
                if (sessions.Length == 0) continue;
                var project = new ProjectInfo(Path.GetFileName(dir), ProjectPathOf(sessions), sessions.Length, null);
                foreach (var c in Conversations(dir))
                    all.Add((space, project, c with { Bytes = c.Bytes + FolderSize(Path.Combine(root, "file-history", c.SessionId)) }));
            }
        }
        return all.OrderByDescending(x => x.Item3.LastUsed).ToList();
    }

    /// <summary>Stands for ~/.claude as a whole while spaces are not set up.</summary>
    public static readonly Space Everything = new() { Id = "", Name = "This PC" };

    /// <summary>Each space with the folder its data is in; without spaces, just ~/.claude.</summary>
    public static IEnumerable<(Space Space, string Root)> Roots(AppPaths paths, SpaceSettings s) =>
        s.Enabled ? s.List.Select(x => (x, SpaceRoot(paths, s, x.Id))) : [(Everything, paths.ClaudeDir)];

    /// <summary>Total size of the files in a folder (0 if it does not exist).</summary>
    public static long FolderSize(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        try
        {
            return new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 })
                .Sum(f => f.Length);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    public static long PathSize(string path) => File.Exists(path) ? new FileInfo(path).Length : FolderSize(path);

    public static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.0} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.00} GB",
    };

    private static List<ConversationInfo> Conversations(string dir)
    {
        var list = new List<ConversationInfo>();
        if (!Directory.Exists(dir)) return list;
        foreach (var file in Directory.GetFiles(dir, "*.jsonl"))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            // The sidebar title lives next to the transcript; renames inside the transcript are the fallback.
            var shown = FileUtil.ReadJsonObject(Path.Combine(dir, id, "custom-title.json"))?["customTitle"]?.GetValue<string>();
            string? title = null;
            string? first = null;
            DateTimeOffset? started = null;
            try
            {
                using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
                while (reader.ReadLine() is { } line)
                {
                    if (shown is null && line.Contains("\"customTitle\"", StringComparison.Ordinal))
                    {
                        using var t = TryParse(line);
                        if (t is not null && t.RootElement.TryGetProperty("customTitle", out var ct) && ct.GetString() is { Length: > 0 } named)
                            title = named;
                    }
                    if (first is not null && shown is not null) break;
                    if (first is not null || !line.Contains("\"type\":\"user\"", StringComparison.Ordinal)) continue;
                    using var doc = TryParse(line);
                    if (doc is null) continue;
                    var root = doc.RootElement;
                    if (root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True) continue;
                    if (root.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True) continue;
                    if (PromptText(root) is not { } text) continue;
                    first = text.Length > 120 ? text[..117] + "…" : text;
                    if (root.TryGetProperty("timestamp", out var ts) && DateTimeOffset.TryParse(ts.GetString(), out var when)) started = when;
                }
            }
            catch (IOException) { }
            var bytes = new FileInfo(file).Length + FolderSize(Path.Combine(dir, id));
            list.Add(new ConversationInfo(id, shown ?? title, first, started, new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero), bytes));
        }
        return list.OrderByDescending(c => c.LastUsed).ToList();
    }

    private static JsonDocument? TryParse(string line)
    {
        try { return JsonDocument.Parse(line); }
        catch (JsonException) { return null; }
    }

    /// <summary>What the user typed, without tool results, command echoes and system notes.</summary>
    private static string? PromptText(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content)) return null;
        var text = content.ValueKind == JsonValueKind.String ? content.GetString()
            : content.ValueKind == JsonValueKind.Array
                ? string.Join(" ", content.EnumerateArray()
                    .Where(b => b.TryGetProperty("type", out var t) && t.GetString() == "text" && b.TryGetProperty("text", out _))
                    .Select(b => b.GetProperty("text").GetString()))
                : null;
        text = Regex.Replace(text ?? "", @"\s+", " ").Trim();
        if (text.Length == 0 || text.StartsWith('<') || text.StartsWith("Caveat:", StringComparison.Ordinal) || text.StartsWith("[Request interrupted", StringComparison.Ordinal))
            return null;
        return text;
    }

    /// <summary>The working folder a project's conversations were started in ("cwd" in the transcript).</summary>
    private static string? ProjectPathOf(string[] sessions)
    {
        foreach (var file in sessions.OrderByDescending(File.GetLastWriteTimeUtc))
        {
            try
            {
                using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
                for (var i = 0; i < 50 && reader.ReadLine() is { } line; i++)
                {
                    if (!line.Contains("\"cwd\"", StringComparison.Ordinal)) continue;
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("cwd", out var cwd) && cwd.GetString() is { Length: > 0 } path) return path;
                }
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return null;
    }

    /// <summary>The folder name Claude Code uses under projects/ for a working folder: every character that is not a letter or digit becomes "-".</summary>
    public static string ProjectDirName(string projectPath) => Regex.Replace(projectPath, "[^A-Za-z0-9]", "-");

    /// <summary>Picks a space for a project from path rules such as "D:\GameDev" → Personal; the longest matching prefix wins.</summary>
    public static string SuggestProject(ProjectInfo project, IReadOnlyList<(string Prefix, string SpaceId)> rules, string fallbackSpaceId)
    {
        var path = Normalize(project.ProjectPath ?? "");
        var best = rules
            .Where(r => r.Prefix.Length > 0)
            .Where(r => path.Length > 0
                ? path.StartsWith(Normalize(r.Prefix), StringComparison.OrdinalIgnoreCase)
                : project.DirName.StartsWith(ProjectDirName(r.Prefix), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Prefix.Length)
            .FirstOrDefault();
        return best.SpaceId ?? fallbackSpaceId;
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/') + "/";

    // ---- CLAUDE.md sections ----

    /// <summary>Cuts markdown at its "## " headings (outside code fences). Returns the text before the first one and each section with its heading.</summary>
    public static (string Preamble, List<(string Heading, string Text)> Sections) SplitClaudeMd(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var preamble = new StringBuilder();
        var sections = new List<(string Heading, StringBuilder Text)>();
        var inFence = false;
        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) inFence = !inFence;
            if (!inFence && line.StartsWith("## ", StringComparison.Ordinal))
                sections.Add((line[3..].Trim(), new StringBuilder()));
            (sections.Count == 0 ? preamble : sections[^1].Text).Append(line).Append('\n');
        }
        return (preamble.ToString().TrimEnd() + "\n", sections.Select(s => (s.Heading, s.Text.ToString().TrimEnd() + "\n")).ToList());
    }

    /// <summary>True when there is more than a title line and blank lines.</summary>
    private static bool HasContent(string preamble) =>
        preamble.Split('\n').Any(l => l.Trim().Length > 0 && !l.StartsWith("# ", StringComparison.Ordinal) && !l.TrimStart().StartsWith("<!--", StringComparison.Ordinal));

    public static string Slug(string heading)
    {
        var slug = Regex.Replace(heading.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (slug.Length > 60) slug = slug[..60].TrimEnd('-');
        return slug.Length == 0 ? "section" : slug;
    }

    private static string UniqueName(string slug, HashSet<string> taken)
    {
        var name = slug + ".md";
        for (var n = 2; !taken.Add(name); n++) name = $"{slug}-{n}.md";
        return name;
    }

    private static string? FirstHeading(string text) =>
        text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith('#'))?.TrimStart('#').Trim();

    private static (string? Name, string? Description) Frontmatter(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---") return (null, null);
        string? name = null, description = null;
        for (var i = 1; i < lines.Length && lines[i].Trim() != "---"; i++)
        {
            var colon = lines[i].IndexOf(':');
            if (colon <= 0 || char.IsWhiteSpace(lines[i][0])) continue;
            var key = lines[i][..colon].Trim();
            var value = lines[i][(colon + 1)..].Trim().Trim('"', '\'');
            if (key == "name") name = value;
            else if (key == "description") description = value.Length > 160 ? value[..157] + "…" : value;
        }
        return (string.IsNullOrEmpty(name) ? null : name, string.IsNullOrEmpty(description) ? null : description);
    }

    private static string ReadText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
        catch (IOException) { return ""; }
    }

    private static IEnumerable<string> Files(string dir, string pattern) =>
        Directory.Exists(dir) ? Directory.GetFiles(dir, pattern).OrderBy(f => f, StringComparer.OrdinalIgnoreCase) : [];

    private static IEnumerable<string> Dirs(string dir) =>
        Directory.Exists(dir) ? Directory.GetDirectories(dir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase) : [];
}
