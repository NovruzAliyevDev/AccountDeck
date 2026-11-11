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
    public int RefreshMinutes { get; set; } = 5;
    public bool KeepSignInsFresh { get; set; } = true;
    public bool NotifyOnHighUsage { get; set; } = true;
    public int NotifyThreshold { get; set; } = 90;
    public bool AutoSwitchClaudeCode { get; set; }
    public int AutoSwitchThreshold { get; set; } = 95;
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
