namespace AccountDeck.Core;

/// <summary>Every location the app touches. Tests point these at temporary folders.</summary>
public sealed class AppPaths(string localAppData, string roamingAppData, string userProfile, string? codexHome = null)
{
    public string Local { get; } = localAppData;
    public string Roaming { get; } = roamingAppData;
    public string UserProfile { get; } = userProfile;

    public string AppDataDir => Path.Combine(Local, "AccountDeck");
    public string StateFile => Path.Combine(AppDataDir, "state.json");
    public string DesktopStore => Path.Combine(AppDataDir, "desktop-profiles");

    public string ClaudeDir => Path.Combine(UserProfile, ".claude");
    public string ClaudeCredentialsFile => Path.Combine(ClaudeDir, ".credentials.json");
    public string ClaudeConfigFile => Path.Combine(UserProfile, ".claude.json");

    public string CodexDir { get; } = codexHome ?? Path.Combine(userProfile, ".codex");
    public string CodexAuthFile => Path.Combine(CodexDir, "auth.json");

    public static AppPaths Default() => new(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } h ? h : null);
}
