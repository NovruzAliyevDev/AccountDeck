using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using AccountDeck.Core;

namespace AccountDeck.App;

/// <summary>A button on a conversation row: bring it to the space in use, or send it to another space.</summary>
public sealed record MoveTarget(string Label, ICommand Command);

public sealed class ConversationRowVM : Bindable
{
    public required string SessionId { get; init; }
    public required string SpaceName { get; init; }
    public required string Group { get; init; }
    public required string Project { get; init; }
    public required DateTimeOffset LastUsed { get; init; }
    public required long Bytes { get; init; }
    public required string Text { get; init; }
    public required string Detail { get; init; }
    public List<MoveTarget> Targets { get; init; } = new();
    public bool ShowSpace { get; init; }
    public bool IsChecked { get; set { Set(ref field, value); Changed?.Invoke(); } }
    public Action? Changed { get; set; }

    public string Date => LastUsed.ToLocalTime().ToString("dd MMM yyyy HH:mm");
    public string Size => SpaceItems.FormatBytes(Bytes);
    public bool HasDetail => Detail.Length > 0;

    public bool Matches(string search) =>
        search.Length == 0 || $"{Text} {Detail} {Project} {SpaceName} {Group}".Contains(search, StringComparison.OrdinalIgnoreCase);
}

/// <summary>One line of the storage tab.</summary>
public sealed record SizeRowVM(string Name, string Detail, long Bytes, double Share, bool IsHeading = false)
{
    public string Size => SpaceItems.FormatBytes(Bytes);
    public double BarWidth => Math.Max(2, Share * 220);
    public bool IsItem => !IsHeading;
}

/// <summary>A project folder on the groups tab; editing its group moves it.</summary>
public sealed class FolderVM(string path, string space, int conversations, long bytes, string group, bool changed, Func<string, string?, Task> setGroup) : Bindable
{
    public string Path => path;
    public string Detail => $"{space} · {conversations} conversation(s) · {SpaceItems.FormatBytes(bytes)}";
    public bool IsChanged => changed;
    public List<string> AllGroups { get; init; } = new();
    public string Group
    {
        get => group;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value.Trim() == group) return;
            group = value.Trim();
            _ = setGroup(path, group);
        }
    }
    public ICommand Reset => new RelayCommand(() => _ = setGroup(path, null));
}

public sealed class GroupVM(string name, List<FolderVM> folders, long bytes, int conversations, Func<GroupVM, string, Task> rename) : Bindable
{
    public string Name => name;
    public string NewName { get; set => Set(ref field, value); } = name;
    public List<FolderVM> Folders => folders;
    public string Detail => $"{folders.Count} folder(s) · {conversations} conversation(s) · {SpaceItems.FormatBytes(bytes)}";
    public ICommand Rename => new RelayCommand(() => { if (NewName.Trim().Length > 0 && NewName.Trim() != name) _ = rename(this, NewName.Trim()); });
}

/// <summary>
/// The hub: every conversation sorted into spaces and groups, with size, move and delete; what takes room on disk;
/// and which project folders make up each group.
/// </summary>
public sealed class HubViewModel(DeckService service) : Bindable
{
    public const string All = "All";
    private List<ConversationRowVM> _all = new();
    private bool _loading;

    // ---------------------------------------------------------------- conversations tab
    public ObservableCollection<ConversationRowVM> Rows { get; } = new();
    public string Search { get; set { Set(ref field, value); Filter(); } } = "";
    public List<string> SpaceFilters { get; private set { Set(ref field, value); } } = [All];
    public string SpaceFilter { get; set { Set(ref field, value ?? All); Filter(); } } = All;
    public List<string> GroupFilters { get; private set { Set(ref field, value); } } = [All];
    public string GroupFilter { get; set { Set(ref field, value ?? All); Filter(); } } = All;
    public List<string> Sorts { get; } = ["Newest first", "Largest first"];
    public string Sort { get; set { Set(ref field, value); Filter(); } } = "Newest first";
    public string Summary { get; private set => Set(ref field, value); } = "Loading…";
    public string SelectionText { get; private set => Set(ref field, value); } = "";
    public bool HasSelection { get; private set => Set(ref field, value); }
    public bool SpacesOn => service.State.Spaces.Enabled;

    // ---------------------------------------------------------------- storage and groups tabs
    public List<SizeRowVM> Storage { get; private set { Set(ref field, value); } } = new();
    public List<GroupVM> Groups { get; private set { Set(ref field, value); } } = new();

    public bool IsBusy { get; private set { Set(ref field, value); Raise(nameof(IsIdle)); } }
    public bool IsIdle => !IsBusy;
    public string BusyText { get; private set => Set(ref field, value); } = "";

    public ICommand SelectShownCommand => new RelayCommand(() => { foreach (var r in Rows) r.IsChecked = true; });
    public ICommand ClearCommand => new RelayCommand(() => { foreach (var r in _all) r.IsChecked = false; });
    public ICommand DeleteCommand => new RelayCommand(() => _ = DeleteAsync());

    public async Task LoadAsync()
    {
        IsBusy = true;
        BusyText = "Reading conversations…";
        try
        {
            var state = service.State;
            var paths = service.Paths;
            var data = await Task.Run(() => Read(paths, state));
            _loading = true;
            _all = data.Rows.Select(Row).ToList();
            SpaceFilters = [All, .. data.Rows.Select(r => r.Space.Name).Distinct()];
            GroupFilters = [All, .. data.Groups.Values.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
            if (!SpaceFilters.Contains(SpaceFilter)) SpaceFilter = All;
            if (!GroupFilters.Contains(GroupFilter)) GroupFilter = All;
            Storage = data.Storage;
            Groups = BuildGroups(data, state);
            _loading = false;
            Filter();
        }
        finally { IsBusy = false; }

        ConversationRowVM Row((Space Space, ProjectInfo Project, ConversationInfo Conversation, string Group) x)
        {
            var s = service.State.Spaces;
            var here = !s.Enabled || x.Space.Id == s.ActiveId;
            var targets = !s.Enabled ? new List<MoveTarget>()
                : here ? s.List.Where(o => o.Id != x.Space.Id).Select(o => Target("Send to " + o.Name, o.Id)).ToList()
                : s.Active is { } active ? [Target("Bring to " + active.Name, active.Id)] : new List<MoveTarget>();
            var c = x.Conversation;
            return new ConversationRowVM
            {
                SessionId = c.SessionId,
                SpaceName = x.Space.Name + (s.Enabled && here ? " (in use)" : ""),
                ShowSpace = s.Enabled,
                Group = x.Group,
                Project = x.Project.ProjectPath ?? x.Project.DirName,
                LastUsed = c.LastUsed,
                Bytes = c.Bytes,
                Text = c.Title ?? c.FirstPrompt ?? "(no typed prompt, only commands such as /login)",
                Detail = c.Title is not null && c.FirstPrompt is not null ? "First prompt: " + c.FirstPrompt : "",
                Targets = targets,
                Changed = Selection,
            };

            MoveTarget Target(string label, string spaceId) => new(label, new RelayCommand(() => _ = MoveAsync(x.Conversation, x.Project, spaceId)));
        }
    }

    private sealed record HubData(
        List<(Space Space, ProjectInfo Project, ConversationInfo Conversation, string Group)> Rows,
        Dictionary<string, string> Groups,
        List<SizeRowVM> Storage);

    private static string Key(ProjectInfo p) => p.ProjectPath ?? p.DirName;

    private static HubData Read(AppPaths paths, AppState state)
    {
        var raw = SpaceItems.AllConversations(paths, state.Spaces);
        var groups = ProjectGroups.Resolve(paths, state, raw.Select(x => Key(x.Project)));
        var rows = raw.Select(x => (x.Space, x.Project, x.Conversation, groups[Key(x.Project)])).ToList();

        // Storage: per space (with its groups), then what every space shares, then Claude's scratch folder.
        var storage = new List<SizeRowVM>();
        var spaces = SpaceItems.Roots(paths, state.Spaces).Select(r =>
        {
            var conv = rows.Where(x => x.Space == r.Space).ToList();
            var projects = SpaceItems.FolderSize(Path.Combine(r.Root, "projects"));
            var fileHistory = SpaceItems.FolderSize(Path.Combine(r.Root, "file-history"));
            var other = SpaceItems.PathSize(Path.Combine(r.Root, "history.jsonl")) + SpaceItems.FolderSize(Path.Combine(r.Root, "todos"))
                        + SpaceItems.FolderSize(Path.Combine(r.Root, "plans")) + SpaceItems.FolderSize(Path.Combine(r.Root, "paste-cache"));
            return (r.Space, Conv: conv, Projects: projects, FileHistory: fileHistory, Other: other, Total: projects + fileHistory + other);
        }).ToList();
        var shared = new[] { "plugins", "skills", "cache", "shell-snapshots", "backups", "agents", "commands", "rules", "telemetry" }
            .Select(n => (Name: n, Bytes: SpaceItems.FolderSize(Path.Combine(paths.ClaudeDir, n)))).Where(x => x.Bytes > 0).OrderByDescending(x => x.Bytes).ToList();
        var scratchDir = Path.Combine(Path.GetTempPath(), "claude");
        var scratch = SpaceItems.FolderSize(scratchDir);
        var max = Math.Max(1, spaces.Select(s => s.Total).Append(shared.Sum(x => x.Bytes)).Append(scratch).Max());

        foreach (var s in spaces)
        {
            storage.Add(new SizeRowVM(s.Space.Name, $"{s.Conv.Count} conversation(s)", s.Total, s.Total / (double)max, IsHeading: true));
            foreach (var g in s.Conv.GroupBy(x => x.Item4, StringComparer.OrdinalIgnoreCase)
                         .Select(g => (Name: g.Key, Bytes: g.Sum(x => x.Conversation.Bytes), Count: g.Count())).OrderByDescending(g => g.Bytes))
                storage.Add(new SizeRowVM(g.Name, $"{g.Count} conversation(s)", g.Bytes, g.Bytes / (double)max));
            var memory = Math.Max(0, s.Projects + s.FileHistory - s.Conv.Sum(x => x.Conversation.Bytes));
            if (memory > 0) storage.Add(new SizeRowVM("Project memory and other files", "memory notes, folders without a conversation", memory, memory / (double)max));
            if (s.Other > 0) storage.Add(new SizeRowVM("Prompt history, to-dos, plans", "", s.Other, s.Other / (double)max));
        }
        if (shared.Count > 0)
        {
            var total = shared.Sum(x => x.Bytes);
            storage.Add(new SizeRowVM(state.Spaces.Enabled ? "Shared by every space" : "Everything else in ~/.claude", "", total, total / (double)max, IsHeading: true));
            foreach (var (name, bytes) in shared) storage.Add(new SizeRowVM(name, "", bytes, bytes / (double)max));
        }
        if (scratch > 0)
        {
            storage.Add(new SizeRowVM("Claude's scratch folder", scratchDir, scratch, scratch / (double)max, IsHeading: true));
            storage.Add(new SizeRowVM("Temporary files of past sessions", "Safe to clear once those sessions are finished", scratch, scratch / (double)max));
        }
        return new HubData(rows, groups, storage);
    }

    private List<GroupVM> BuildGroups(HubData data, AppState state)
    {
        var names = data.Groups.Values.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var folders = data.Rows.GroupBy(x => Key(x.Project), StringComparer.OrdinalIgnoreCase).Select(f =>
        {
            var first = f.First();
            return new FolderVM(f.Key, first.Space.Name, f.Count(), f.Sum(x => x.Conversation.Bytes), first.Item4,
                state.ProjectGroups.ContainsKey(ProjectGroups.Normalize(f.Key)), SetGroupAsync) { AllGroups = names };
        }).ToList();
        return folders.GroupBy(f => f.Group, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var rows = data.Rows.Where(x => string.Equals(x.Item4, g.Key, StringComparison.OrdinalIgnoreCase)).ToList();
                return new GroupVM(g.Key, g.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList(),
                    rows.Sum(x => x.Conversation.Bytes), rows.Count, RenameAsync);
            })
            .OrderByDescending(g => g.Folders.Count).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task SetGroupAsync(string path, string? group)
    {
        await service.SetProjectGroupAsync([path], group);
        await LoadAsync();
    }

    private async Task RenameAsync(GroupVM group, string name)
    {
        await service.SetProjectGroupAsync(group.Folders.Select(f => f.Path), name);
        await LoadAsync();
    }

    // ---------------------------------------------------------------- actions

    private async Task MoveAsync(ConversationInfo c, ProjectInfo project, string toSpaceId)
    {
        if (IsBusy) return;
        var to = service.State.Spaces.Find(toSpaceId);
        if (to is null) return;
        var bringingHere = toSpaceId == service.State.Spaces.ActiveId;
        var answer = Dialog.Ask($"Move this conversation to \"{to.Name}\"?",
            $"\"{Short(c.Title ?? c.FirstPrompt ?? c.SessionId)}\"\n{project.ProjectPath ?? project.DirName}\n\n" +
            "Its transcript, file history, to-dos and prompt history move, and Claude Desktop lists it under that space's account. " +
            "The project's memory stays where it is.\n\n" +
            (bringingHere ? "You can continue it right away." : $"You can continue it after switching to \"{to.Name}\".") +
            "\n\nClaude Desktop will close while it moves. Close any Claude Code terminals first.", "Move");
        if (answer is null) return;
        await Busy("Moving…", () => service.MoveConversationAsync(c.SessionId, toSpaceId));
    }

    private async Task DeleteAsync()
    {
        if (IsBusy) return;
        var chosen = _all.Where(r => r.IsChecked).ToList();
        if (chosen.Count == 0) return;
        var bytes = chosen.Sum(r => r.Bytes);
        var list = string.Join("\n", chosen.Take(8).Select(r => "• " + Short(r.Text)));
        if (chosen.Count > 8) list += $"\n• … and {chosen.Count - 8} more";
        var answer = Dialog.Ask($"Delete {chosen.Count} conversation(s)?",
            $"{list}\n\nThis frees about {SpaceItems.FormatBytes(bytes)}. Each conversation's transcript, file history, to-dos, prompt history and " +
            "Claude Desktop sidebar entry go to the Recycle Bin together, so you can still restore them from there.\n\n" +
            "Claude Desktop will close while this happens. Close any Claude Code terminals first.", "Delete");
        if (answer is null) return;
        await Busy("Deleting…", () => service.DeleteConversationsAsync(chosen.Select(r => r.SessionId).ToList()));
    }

    private async Task Busy(string text, Func<Task> work)
    {
        IsBusy = true;
        BusyText = text;
        try { await work(); }
        catch (Exception e) { Dialog.Error("That did not work", e is DeckException ? e.Message : "Something unexpected happened: " + e.Message); }
        finally { IsBusy = false; }
        await LoadAsync();
    }

    private void Filter()
    {
        if (_loading) return;
        var search = Search.Trim();
        var shown = _all.Where(r => r.Matches(search)
                                    && (SpaceFilter == All || r.SpaceName.StartsWith(SpaceFilter, StringComparison.Ordinal))
                                    && (GroupFilter == All || string.Equals(r.Group, GroupFilter, StringComparison.OrdinalIgnoreCase)));
        shown = Sort == "Largest first" ? shown.OrderByDescending(r => r.Bytes) : shown.OrderByDescending(r => r.LastUsed);
        Rows.Clear();
        foreach (var row in shown) Rows.Add(row);
        var total = SpaceItems.FormatBytes(_all.Sum(r => r.Bytes));
        Summary = _all.Count == 0 ? "No conversations found."
            : Rows.Count == _all.Count ? $"{_all.Count} conversation(s), {total}"
            : $"{Rows.Count} of {_all.Count} conversation(s), {SpaceItems.FormatBytes(Rows.Sum(r => r.Bytes))}";
        Selection();
    }

    private void Selection()
    {
        var chosen = _all.Where(r => r.IsChecked).ToList();
        HasSelection = chosen.Count > 0;
        SelectionText = chosen.Count == 0 ? "Tick conversations to delete several at once."
            : $"{chosen.Count} selected · {SpaceItems.FormatBytes(chosen.Sum(r => r.Bytes))}";
    }

    private static string Short(string text) => text.Length <= 90 ? text : text[..87] + "…";
}
