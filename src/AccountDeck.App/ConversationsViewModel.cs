using System.Collections.ObjectModel;
using System.Windows.Input;
using AccountDeck.Core;

namespace AccountDeck.App;

/// <summary>A button on a conversation row: bring it to the space in use, or send it to another space.</summary>
public sealed record MoveTarget(string Label, string SpaceId, ICommand Command);

public sealed class ConversationRowVM
{
    public required string SessionId { get; init; }
    public required string SpaceName { get; init; }
    public required bool IsHere { get; init; }
    public required string Project { get; init; }
    public required string Date { get; init; }
    public required string Text { get; init; }
    public required string Detail { get; init; }
    public List<MoveTarget> Targets { get; init; } = new();
    public bool HasDetail => Detail.Length > 0;
    public string SpaceLabel => IsHere ? SpaceName + " (in use)" : SpaceName;

    public bool Matches(string search) =>
        search.Length == 0 || $"{Text} {Detail} {Project} {SpaceName}".Contains(search, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Every conversation of every space, so one can be moved to the space where it should be continued.</summary>
public sealed class ConversationsViewModel(DeckService service) : Bindable
{
    private List<ConversationRowVM> _all = new();

    public ObservableCollection<ConversationRowVM> Rows { get; } = new();
    public string Search { get; set { Set(ref field, value); Filter(); } } = "";
    public string Summary { get; private set => Set(ref field, value); } = "Loading…";
    public bool IsBusy { get; private set { Set(ref field, value); Raise(nameof(IsIdle)); } }
    public bool IsIdle => !IsBusy;

    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var s = service.State.Spaces;
            var list = await Task.Run(() => SpaceItems.AllConversations(service.Paths, s));
            _all = list.Select(x => Row(s, x.Space, x.Project, x.Conversation)).ToList();
            Filter();
        }
        finally { IsBusy = false; }
    }

    private ConversationRowVM Row(SpaceSettings s, Space space, ProjectInfo project, ConversationInfo c)
    {
        var here = space.Id == s.ActiveId;
        var targets = here
            ? s.List.Where(o => o.Id != space.Id).Select(o => Target("Send to " + o.Name, o.Id)).ToList()
            : s.Active is { } active ? [Target("Bring to " + active.Name, active.Id)] : new List<MoveTarget>();
        return new ConversationRowVM
        {
            SessionId = c.SessionId,
            SpaceName = space.Name,
            IsHere = here,
            Project = project.ProjectPath ?? project.DirName,
            Date = c.LastUsed.ToLocalTime().ToString("dd MMM yyyy HH:mm"),
            Text = c.Title ?? c.FirstPrompt ?? "(no typed prompt, only commands such as /login)",
            Detail = c.Title is not null && c.FirstPrompt is not null ? "First prompt: " + c.FirstPrompt : "",
            Targets = targets,
        };

        MoveTarget Target(string label, string spaceId) => new(label, spaceId, new RelayCommand(() => _ = MoveAsync(c, project, spaceId)));
    }

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

        IsBusy = true;
        try
        {
            await service.MoveConversationAsync(c.SessionId, toSpaceId);
        }
        catch (Exception e)
        {
            Dialog.Error("That did not work", e is DeckException ? e.Message : "Something unexpected happened: " + e.Message);
        }
        finally { IsBusy = false; }
        await LoadAsync();
    }

    private void Filter()
    {
        var search = Search.Trim();
        Rows.Clear();
        foreach (var row in _all.Where(r => r.Matches(search))) Rows.Add(row);
        var spaces = _all.Select(r => r.SpaceName).Distinct().Count();
        Summary = _all.Count == 0 ? "No conversations found."
            : Rows.Count == _all.Count ? $"{_all.Count} conversation(s) in {spaces} space(s), newest first."
            : $"{Rows.Count} of {_all.Count} conversation(s) match.";
    }

    private static string Short(string text) => text.Length <= 90 ? text : text[..87] + "…";
}
