using System.Text.Json.Serialization;

namespace AccountDeck.Core;

[JsonConverter(typeof(JsonStringEnumConverter<AccountKind>))]
public enum AccountKind { Desktop, ClaudeCode, Codex }

/// <summary>How much of the plan limits an account has used, as percentages.</summary>
public sealed class UsageSnapshot
{
    public double? FiveHour { get; set; }
    public double? SevenDay { get; set; }
    public DateTimeOffset? FiveHourResetsAt { get; set; }
    public DateTimeOffset? SevenDayResetsAt { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.Now;
    public string? Error { get; set; }

    [JsonIgnore]
    public double? Peak => FiveHour is null && SevenDay is null ? null : Math.Max(FiveHour ?? 0, SevenDay ?? 0);
}

public sealed class Profile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public AccountKind Kind { get; set; }
    public string Name { get; set; } = "";
    public string? Email { get; set; }
    /// <summary>Stable identity of the signed-in account (account UUID), when it can be read.</summary>
    public string? AccountKey { get; set; }
    public string? Plan { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? LastUsedAt { get; set; }
    /// <summary>Encrypted sign-in data (Claude Code and Codex only). Desktop profiles are whole folders.</summary>
    public string? Secret { get; set; }
    public UsageSnapshot? Usage { get; set; }
}

public sealed class Settings
{
    public bool CloseToTray { get; set; } = true;
    /// <summary>
    /// On: Claude Desktop accounts share one data folder (sessions, settings, files) and only the
    /// sign-in is swapped. Off: every account gets its own complete copy of the data folder.
    /// </summary>
    public bool ShareDesktopSessions { get; set; } = true;
    public int RefreshMinutes { get; set; } = 5;
    public bool KeepSignInsFresh { get; set; } = true;
    public bool NotifyOnHighUsage { get; set; } = true;
    public int NotifyThreshold { get; set; } = 90;
    public bool AutoSwitchClaudeCode { get; set; }
    public int AutoSwitchThreshold { get; set; } = 95;
}

/// <summary>
/// A side such as "Work" or "Personal": a Claude Desktop account, a Claude Code account and its own
/// private part of ~/.claude (conversations, file history, prompt history, and whatever the user marked as its own).
/// </summary>
public sealed class Space
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "";
    public string? DesktopProfileId { get; set; }
    public string? ClaudeCodeProfileId { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<ItemMode>))]
public enum ItemMode
{
    /// <summary>One copy, always in place, seen by every space.</summary>
    Shared,
    /// <summary>Every space has its own copy.</summary>
    Separate,
    /// <summary>Only the space named in <see cref="ItemChoice.SpaceId"/> has it.</summary>
    OnlyIn,
}

/// <summary>What happens to one item of ~/.claude (a rule file, a skill, settings.json, ...) when spaces switch.</summary>
public sealed class ItemChoice
{
    public ItemMode Mode { get; set; }
    public string? SpaceId { get; set; }

    public static ItemChoice Shared => new() { Mode = ItemMode.Shared };
    public static ItemChoice Separate => new() { Mode = ItemMode.Separate };
    public static ItemChoice Only(string spaceId) => new() { Mode = ItemMode.OnlyIn, SpaceId = spaceId };

    /// <summary>Whether the item is swapped in and out together with this space.</summary>
    public bool BelongsTo(string spaceId) => Mode == ItemMode.Separate || (Mode == ItemMode.OnlyIn && SpaceId == spaceId);
}

public sealed class SpaceSettings
{
    public bool Enabled { get; set; }
    public List<Space> List { get; set; } = new();
    public string? ActiveId { get; set; }
    /// <summary>Item key (see <see cref="SpaceItems"/>) to choice. Items not listed are shared.</summary>
    public Dictionary<string, ItemChoice> Items { get; set; } = new();

    public Space? Find(string? id) => List.FirstOrDefault(s => s.Id == id);
    public Space? Active => Find(ActiveId);
    public ItemChoice ChoiceFor(string key) => Items.TryGetValue(key, out var c) ? c : ItemChoice.Shared;

    /// <summary>The space a saved account is linked to, if any.</summary>
    public Space? Owner(Profile p) => p.Kind switch
    {
        AccountKind.Desktop => List.FirstOrDefault(s => s.DesktopProfileId == p.Id),
        AccountKind.ClaudeCode => List.FirstOrDefault(s => s.ClaudeCodeProfileId == p.Id),
        _ => null,
    };
}

public sealed class LogEntry
{
    public DateTimeOffset At { get; set; } = DateTimeOffset.Now;
    public string Message { get; set; } = "";
    public bool IsError { get; set; }
}

public sealed class AppState
{
    public List<Profile> Profiles { get; set; } = new();
    public Dictionary<AccountKind, string> ActiveIds { get; set; } = new();
    public Settings Settings { get; set; } = new();
    public SpaceSettings Spaces { get; set; } = new();
    /// <summary>Project folder to group name, only where the user changed the suggested group.</summary>
    public Dictionary<string, string> ProjectGroups { get; set => field = new(value, StringComparer.OrdinalIgnoreCase); } = new(StringComparer.OrdinalIgnoreCase);
    public List<LogEntry> Log { get; set; } = new();

    public IEnumerable<Profile> Of(AccountKind kind) => Profiles.Where(p => p.Kind == kind);
    public Profile? Find(string id) => Profiles.FirstOrDefault(p => p.Id == id);
    public Profile? Active(AccountKind kind) =>
        ActiveIds.TryGetValue(kind, out var id) ? Profiles.FirstOrDefault(p => p.Id == id && p.Kind == kind) : null;
    public bool IsActive(Profile p) => ActiveIds.TryGetValue(p.Kind, out var id) && id == p.Id;

    public string NextName(AccountKind kind)
    {
        for (var n = Of(kind).Count() + 1; ; n++)
        {
            var name = $"Account {n}";
            if (!Of(kind).Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))) return name;
        }
    }
}

/// <summary>A failure whose message is safe to show to the user as-is.</summary>
public sealed class DeckException(string message, Exception? inner = null) : Exception(message, inner);
