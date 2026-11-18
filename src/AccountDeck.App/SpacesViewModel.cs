using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using AccountDeck.Core;

namespace AccountDeck.App;

public abstract class Bindable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected void Set<T>(ref T storage, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value)) return;
        storage = value;
        Raise(name);
    }
}

public sealed record Option(string Label, string? Id);

public sealed class SpaceRowVM : Bindable
{
    public required Space Space { get; init; }
    public string Name { get => Space.Name; set { Space.Name = value; Raise(); } }
    public List<Option> DesktopChoices { get; init; } = new();
    public List<Option> CodeChoices { get; init; } = new();
    public Option? Desktop { get; set { Set(ref field, value); Space.DesktopProfileId = value?.Id; } }
    public Option? Code { get; set { Set(ref field, value); Space.ClaudeCodeProfileId = value?.Id; } }
    public bool IsLive { get; set => Set(ref field, value); }
}

public sealed record ChoiceOption(string Label, ItemChoice Choice)
{
    public bool Same(ItemChoice c) => c.Mode == Choice.Mode && c.SpaceId == Choice.SpaceId;
}

public sealed class ItemRowVM : Bindable
{
    public required SpaceItem Item { get; init; }
    public string Title => Item.Title;
    public string Help => Item.Help;
    public List<ChoiceOption> Options { get; set { Set(ref field, value); } } = new();
    public ChoiceOption? Selected { get; set => Set(ref field, value); }
}

public sealed class NameVM : Bindable
{
    public required string Word { get; init; }
    public required int Count { get; init; }
    public required string Where { get; init; }
    public string Label => $"{Word}  ·  {Count}";
    public bool IsChecked { get; set => Set(ref field, value); }
}

public sealed class ItemGroupVM
{
    public required string Title { get; init; }
    public required List<ItemRowVM> Rows { get; init; }
}

public sealed class ProjectRowVM : Bindable
{
    public required ProjectInfo Project { get; init; }
    public string Path => Project.ProjectPath ?? Project.DirName;
    public string Detail => $"{Project.Conversations} conversation(s)" + (Project.LastUsed is { } d ? ", last used " + d.ToLocalTime().ToString("dd MMM yyyy") : "");
    public List<Option> Options
    {
        get;
        set
        {
            Set(ref field, value);
            foreach (var c in Conversations ?? []) c.SetSpaces(value);
        }
    } = new();
    public Option? Selected { get; set => Set(ref field, value); }

    /// <summary>Conversations given to another space than this project's, by session id.</summary>
    public IEnumerable<(string SessionId, string SpaceId)> Exceptions =>
        (Conversations ?? []).Where(c => c.Selected?.Id is { } id && id != Selected?.Id).Select(c => (c.SessionId, c.Selected!.Id!));

    public required Func<List<ConversationInfo>> Load { get; init; }
    public string ExpandText => $"Show the {Project.Conversations} conversation(s)";
    /// <summary>Conversations are read only when the row is opened: transcripts can be large.</summary>
    public bool IsExpanded
    {
        get;
        set
        {
            Set(ref field, value);
            if (value && Conversations is null)
            {
                Conversations = Load().Select(c => new ConversationVM(c.SessionId,
                    (c.Started ?? c.LastUsed).ToLocalTime().ToString("dd MMM yyyy"),
                    c.Title ?? c.FirstPrompt ?? "(no typed prompt, only commands such as /login)",
                    c.Title is not null && c.FirstPrompt is not null ? "First prompt: " + c.FirstPrompt : "")).ToList();
                foreach (var c in Conversations) c.SetSpaces(Options);
                Raise(nameof(Conversations));
            }
        }
    }
    public List<ConversationVM>? Conversations { get; private set; }
}

/// <summary>One conversation of a project; it goes with the project unless another space is picked for it.</summary>
public sealed class ConversationVM(string sessionId, string date, string text, string detail) : Bindable
{
    public const string WithProject = "With the project";
    public string SessionId => sessionId;
    public string Date => date;
    public string Text => text;
    public string Detail => detail;
    public bool HasDetail => detail.Length > 0;
    public List<Option> Options { get; private set { Set(ref field, value); } } = new();
    public Option? Selected { get; set => Set(ref field, value); }

    public void SetSpaces(List<Option> spaces)
    {
        var keep = Selected?.Id;
        Options = [new Option(WithProject, null), .. spaces];
        Selected = Options.FirstOrDefault(o => o.Id == keep) ?? Options[0];
    }
}

public sealed class RuleRowVM : Bindable
{
    public string Prefix { get; set => Set(ref field, value); } = "";
    public List<Option> Options { get; set { Set(ref field, value); } } = new();
    public Option? Selected { get; set => Set(ref field, value); }
}

/// <summary>
/// The spaces guide. In setup mode it has four steps (spaces, what is shared, projects, confirm).
/// In edit mode, after setup, it shows the first two steps and applies each changed row.
/// </summary>
public sealed class SpacesViewModel : Bindable
{
    private readonly DeckService _service;
    private readonly Dictionary<string, ItemChoice> _initial = new();

    public bool IsSetup { get; }
    public ObservableCollection<SpaceRowVM> SpaceRows { get; } = new();
    public ObservableCollection<ItemGroupVM> Groups { get; } = new();
    public ObservableCollection<ProjectRowVM> Projects { get; } = new();
    public ObservableCollection<RuleRowVM> Rules { get; } = new();
    /// <summary>Names found in the user's instructions, to tick instead of typing from memory.</summary>
    public ObservableCollection<NameVM> Names { get; } = new();
    public bool HasNames => Names.Count > 0;
    public bool NoNames => !HasNames;
    public List<Option> SpaceOptions => SpaceRows.Select(r => new Option(r.Name, r.Space.Id)).ToList();
    public Option? WorkSpace { get; set => Set(ref field, value); }

    public int Step { get; private set { Set(ref field, value); RaiseStep(); } } = 1;
    public int LastStep => IsSetup ? 4 : 2;
    public bool IsStep1 => Step == 1;
    public bool IsStep2 => Step == 2;
    public bool IsStep3 => Step == 3;
    public bool IsStep4 => Step == 4;
    public bool CanBack => Step > 1;
    public string StepText => $"Step {Step} of {LastStep}";
    public string NextText => Step < LastStep ? "Next" : IsSetup ? "Start" : "Save";
    public string Heading => Step switch
    {
        1 => IsSetup ? "Your spaces" : "Spaces and accounts",
        2 => "What both spaces share",
        3 => "Sort your projects",
        _ => "Check and start",
    };
    public string Intro => Step switch
    {
        1 => IsSetup
            ? "A space is one side of your work, such as Work or Personal. Each space has its own conversations, file history and prompt history, and its own accounts. Pick the accounts for each space and which one you are using right now."
            : "Rename a space or change which accounts it uses.",
        2 => "Choose, item by item, which instructions both spaces use and which belong to one side only. Click an item to read it. Conversations, file history, project memory and prompt history are always kept separate.",
        3 => "Every project you have used with Claude is listed with its folder. Choose the space it belongs to. Rules like \"D:\\GameDev → Personal\" fill the list for you. If one conversation belongs elsewhere, open the project's conversations and pick its own space.",
        _ => "",
    };

    public ItemRowVM? Preview { get; set { Set(ref field, value); Raise(nameof(PreviewText)); Raise(nameof(PreviewTitle)); } }
    public string PreviewTitle => Preview?.Title ?? "Click an item to see its text";
    public string PreviewText => Preview is null ? "" : Preview.Item.Text.Length > 0 ? Preview.Item.Text : Preview.Help;
    public string Summary { get; private set => Set(ref field, value); } = "";

    public ICommand AddSpaceCommand { get; }
    public ICommand AddRuleCommand { get; }
    public ICommand ApplyRulesCommand { get; }

    public SpacesViewModel(DeckService service)
    {
        _service = service;
        var state = service.State;
        IsSetup = !state.Spaces.Enabled;
        AddSpaceCommand = new RelayCommand(() => AddSpace(new Space { Name = $"Space {SpaceRows.Count + 1}" }));
        AddRuleCommand = new RelayCommand(() => Rules.Add(new RuleRowVM { Options = SpaceOptions, Selected = SpaceOptions.LastOrDefault() }));
        ApplyRulesCommand = new RelayCommand(ApplyRules);

        if (IsSetup)
        {
            string[] names = ["Work", "Personal"];
            var desktop = PickByName(state.Of(AccountKind.Desktop).ToList(), names);
            var code = PickByName(state.Of(AccountKind.ClaudeCode).ToList(), names);
            for (var i = 0; i < names.Length; i++) AddSpace(new Space { Name = names[i] }, desktop[i], code[i]);

            // The space in use is the one whose Claude Desktop (or else Claude Code) account is signed in now.
            var live = SpaceRows.FirstOrDefault(r => r.Desktop?.Id is { } d && d == state.Active(AccountKind.Desktop)?.Id)
                       ?? SpaceRows.FirstOrDefault(r => r.Code?.Id is { } c && c == state.Active(AccountKind.ClaudeCode)?.Id)
                       ?? SpaceRows[0];
            live.IsLive = true;

            foreach (var (word, items) in SpaceItems.FindNames(SpaceItems.List(service.Paths, state)))
                Names.Add(new NameVM
                {
                    Word = word,
                    Count = items.Count,
                    Where = "Found in: " + string.Join(", ", items.Select(i => i.Title)),
                });
        }
        else
        {
            foreach (var s in state.Spaces.List)
            {
                var copy = new Space { Id = s.Id, Name = s.Name };
                AddSpace(copy, state.Find(s.DesktopProfileId ?? ""), state.Find(s.ClaudeCodeProfileId ?? "")).IsLive = s.Id == state.Spaces.ActiveId;
            }
        }
        WorkSpace = SpaceOptions.FirstOrDefault();
    }

    /// <summary>
    /// One account per space name: first the accounts whose name or e-mail contains the space name
    /// ("Work", "work@…"), then the rest in order. Null where there are fewer accounts than spaces.
    /// </summary>
    private static Profile?[] PickByName(List<Profile> accounts, string[] names)
    {
        var picked = new Profile?[names.Length];
        var free = new List<Profile>(accounts);
        for (var i = 0; i < names.Length; i++)
        {
            picked[i] = free.FirstOrDefault(p => (p.Name + " " + p.Email).Contains(names[i], StringComparison.OrdinalIgnoreCase));
            if (picked[i] is not null) free.Remove(picked[i]!);
        }
        for (var i = 0; i < names.Length; i++)
        {
            if (picked[i] is not null || free.Count == 0) continue;
            picked[i] = free[0];
            free.RemoveAt(0);
        }
        return picked;
    }

    private SpaceRowVM AddSpace(Space space, Profile? desktop = null, Profile? code = null)
    {
        var state = _service.State;
        List<Option> Choices(AccountKind kind) =>
            new[] { new Option("No account", null) }.Concat(state.Of(kind).Select(p => new Option(p.Name, p.Id))).ToList();
        var row = new SpaceRowVM { Space = space, DesktopChoices = Choices(AccountKind.Desktop), CodeChoices = Choices(AccountKind.ClaudeCode) };
        row.Desktop = row.DesktopChoices.FirstOrDefault(o => o.Id == desktop?.Id) ?? row.DesktopChoices[0];
        row.Code = row.CodeChoices.FirstOrDefault(o => o.Id == code?.Id) ?? row.CodeChoices[0];
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SpaceRowVM.IsLive) && row.IsLive)
                foreach (var other in SpaceRows.Where(r => r != row)) other.IsLive = false;
            if (e.PropertyName == nameof(SpaceRowVM.Name)) Raise(nameof(SpaceOptions));
        };
        SpaceRows.Add(row);
        Raise(nameof(SpaceOptions));
        return row;
    }

    /// <summary>Moves on; returns true when the guide is finished.</summary>
    public bool Next()
    {
        switch (Step)
        {
            case 1:
                if (SpaceRows.Any(r => string.IsNullOrWhiteSpace(r.Name)))
                    throw new DeckException("Every space needs a name.");
                if (SpaceRows.Select(r => r.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != SpaceRows.Count)
                    throw new DeckException("Two spaces have the same name.");
                var linked = SpaceRows.SelectMany(r => new[] { r.Desktop?.Id, r.Code?.Id }).Where(id => id is not null).ToList();
                if (linked.Count != linked.Distinct().Count())
                    throw new DeckException("An account can belong to one space only.");
                if (IsSetup && !SpaceRows.Any(r => r.IsLive))
                    throw new DeckException("Choose the space you are using right now.");
                BuildItemRows();
                break;
            case 2 when IsSetup:
                BuildProjectRows();
                break;
            case 3:
                BuildSummary();
                break;
        }
        if (Step == LastStep) return true;
        Step++;
        return false;
    }

    public void Back() { if (Step > 1) Step--; }

    private void BuildItemRows()
    {
        var state = _service.State;
        var keepWhatWasChosen = Groups.SelectMany(g => g.Rows).ToDictionary(r => r.Item.Key, r => r.Selected?.Choice);
        var items = SpaceItems.List(_service.Paths, state);
        var words = Names.Where(n => n.IsChecked).Select(n => n.Word).ToList();
        var workId = WorkSpace?.Id ?? SpaceRows[0].Space.Id;

        Groups.Clear();
        foreach (var group in items.GroupBy(i => i.Group))
        {
            var rows = new List<ItemRowVM>();
            foreach (var item in group)
            {
                var options = new List<ChoiceOption> { new("Both spaces", ItemChoice.Shared) };
                if (item.CanBeSeparate) options.Add(new ChoiceOption("Each its own copy", ItemChoice.Separate));
                options.AddRange(SpaceRows.Select(r => new ChoiceOption(r.Name + " only", ItemChoice.Only(r.Space.Id))));

                var current = keepWhatWasChosen.GetValueOrDefault(item.Key)
                              ?? (IsSetup ? SpaceItems.Suggest(item, workId, words) : state.Spaces.ChoiceFor(item.Key));
                if (!IsSetup) _initial.TryAdd(item.Key, state.Spaces.ChoiceFor(item.Key));
                rows.Add(new ItemRowVM { Item = item, Options = options, Selected = options.FirstOrDefault(o => o.Same(current)) ?? options[0] });
            }
            Groups.Add(new ItemGroupVM
            {
                Title = group.Key switch
                {
                    ItemGroup.Instructions => "Your instructions",
                    ItemGroup.Skills => "Your skills",
                    ItemGroup.Agents => "Your agents",
                    ItemGroup.Commands => "Your slash commands",
                    _ => "Settings",
                },
                Rows = rows,
            });
        }
        Preview = null;
    }

    private void BuildProjectRows()
    {
        if (Projects.Count > 0) { foreach (var p in Projects) p.Options = SpaceOptions; return; }
        var live = SpaceRows.First(r => r.IsLive).Space.Id;
        foreach (var project in SpaceItems.Projects(_service.Paths))
        {
            var options = SpaceOptions;
            Projects.Add(new ProjectRowVM
            {
                Project = project, Options = options, Selected = options.First(o => o.Id == live),
                Load = () => SpaceItems.Conversations(_service.Paths, project.DirName),
            });
        }
    }

    private void ApplyRules()
    {
        var live = SpaceRows.First(r => r.IsLive).Space.Id;
        var rules = Rules.Where(r => r.Selected?.Id is not null && r.Prefix.Trim().Length > 0)
            .Select(r => (r.Prefix.Trim(), r.Selected!.Id!)).ToList();
        foreach (var row in Projects)
        {
            var id = SpaceItems.SuggestProject(row.Project, rules, live);
            row.Selected = row.Options.FirstOrDefault(o => o.Id == id);
        }
    }

    private void BuildSummary()
    {
        var lines = new List<string>();
        foreach (var space in SpaceRows)
        {
            var exceptions = Projects.SelectMany(p => p.Exceptions).ToList();
            var leaving = Projects.Where(p => p.Selected?.Id == space.Space.Id).Sum(p => p.Exceptions.Count());
            var conversations = Projects.Where(p => p.Selected?.Id == space.Space.Id).Sum(p => p.Project.Conversations)
                                - leaving + exceptions.Count(e => e.SpaceId == space.Space.Id);
            var own = Rows.Count(r => r.Selected?.Choice.Mode == ItemMode.OnlyIn && r.Selected.Choice.SpaceId == space.Space.Id);
            lines.Add($"{space.Name}{(space.IsLive ? " (in use now)" : "")}: {conversations} conversation(s), {own} item(s) of its own" +
                      $", Claude Desktop: {space.Desktop?.Label ?? "none"}, Claude Code: {space.Code?.Label ?? "none"}.");
        }
        var shared = Rows.Where(r => r.Selected?.Choice.Mode == ItemMode.Shared).Select(r => r.Title).ToList();
        lines.Add("");
        lines.Add("Shared by every space: " + (shared.Count == 0 ? "nothing" : string.Join(", ", shared)) + ".");
        lines.Add("");
        lines.Add("Claude Desktop will close while the files are sorted. Close any Claude Code terminals first. " +
                  "CLAUDE.md and your prompt history are copied to a backup folder before anything changes.");
        Summary = string.Join("\n", lines);
    }

    private IEnumerable<ItemRowVM> Rows => Groups.SelectMany(g => g.Rows);

    public SpaceSetupPlan BuildPlan() => new()
    {
        Spaces = SpaceRows.Select(r => r.Space).ToList(),
        LiveSpaceId = SpaceRows.First(r => r.IsLive).Space.Id,
        Items = Rows.Select(r => r.Item).ToList(),
        Choices = Rows.ToDictionary(r => r.Item.Key, r => r.Selected?.Choice ?? ItemChoice.Shared),
        ProjectOwners = Projects.Where(p => p.Selected?.Id is not null).ToDictionary(p => p.Project.DirName, p => p.Selected!.Id!),
        ConversationOwners = Projects.SelectMany(p => p.Exceptions).ToDictionary(e => e.SessionId, e => e.SpaceId),
    };

    /// <summary>Edit mode: the rows whose choice changed, and the new space names and accounts.</summary>
    public async Task SaveEditsAsync()
    {
        await _service.UpdateSpacesAsync(s =>
        {
            foreach (var row in SpaceRows)
            {
                var space = s.Find(row.Space.Id);
                if (space is null) continue;
                space.Name = row.Name.Trim();
                space.DesktopProfileId = row.Space.DesktopProfileId;
                space.ClaudeCodeProfileId = row.Space.ClaudeCodeProfileId;
            }
        });
        var changes = new Dictionary<string, ItemChoice>();
        foreach (var row in Rows)
        {
            var next = row.Selected?.Choice ?? ItemChoice.Shared;
            var before = _initial.GetValueOrDefault(row.Item.Key) ?? ItemChoice.Shared;
            if (next.Mode != before.Mode || next.SpaceId != before.SpaceId) changes[row.Item.Key] = next;
        }
        await _service.ChangeSpaceItemsAsync(changes);
    }

    private void RaiseStep()
    {
        foreach (var n in new[] { nameof(IsStep1), nameof(IsStep2), nameof(IsStep3), nameof(IsStep4), nameof(CanBack), nameof(StepText), nameof(NextText), nameof(Heading), nameof(Intro) })
            Raise(n);
    }
}
