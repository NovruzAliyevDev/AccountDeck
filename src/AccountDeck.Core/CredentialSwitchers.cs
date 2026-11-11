using System.Text;
using System.Text.Json.Nodes;

namespace AccountDeck.Core;

/// <summary>The sign-in currently on disk for a command-line tool.</summary>
public sealed record LiveLogin(string Key, string? Email, string? Plan, string Secret);

/// <summary>
/// Switches accounts for a tool that keeps its sign-in in a file (Claude Code, Codex).
/// Each profile stores an encrypted copy of that sign-in; switching writes the chosen copy back.
/// </summary>
public abstract class CredentialSwitcher(AccountKind kind, ISecretProtector protector)
{
    public AccountKind Kind => kind;
    protected ISecretProtector Protector => protector;

    /// <summary>True when the tool has been used on this computer.</summary>
    public abstract bool IsInstalled { get; }
    public abstract LiveLogin? ReadLive();
    protected abstract void WriteLive(string secret);

    /// <summary>
    /// Records whatever is signed in right now: a new account becomes a new profile,
    /// a known one gets its saved sign-in refreshed. Returns the active profile, if any.
    /// </summary>
    public Profile? Sync(AppState state, Action<string>? log = null)
    {
        var live = ReadLive();
        if (live is null)
        {
            state.ActiveIds.Remove(kind);
            return null;
        }

        var profile = state.Of(kind).FirstOrDefault(p => p.AccountKey == live.Key);
        if (profile is null)
        {
            profile = new Profile { Kind = kind, AccountKey = live.Key, Name = live.Email ?? state.NextName(kind) };
            state.Profiles.Add(profile);
            log?.Invoke($"Saved {Label} account \"{profile.Name}\".");
        }
        profile.Email = live.Email ?? profile.Email;
        profile.Plan = live.Plan ?? profile.Plan;
        profile.Secret = protector.Protect(live.Secret);
        state.ActiveIds[kind] = profile.Id;
        return profile;
    }

    public void Switch(AppState state, string targetId)
    {
        Sync(state); // keep the freshest copy of the account we are leaving
        var target = state.Find(targetId) ?? throw new DeckException("That account no longer exists.");
        if (target.Secret is null) throw new DeckException($"No saved sign-in for \"{target.Name}\".");
        WriteLive(protector.Unprotect(target.Secret));
        state.ActiveIds[kind] = target.Id;
        target.LastUsedAt = DateTimeOffset.Now;
    }

    public void Remove(AppState state, string id)
    {
        var p = state.Find(id) ?? throw new DeckException("That account no longer exists.");
        if (state.IsActive(p))
            throw new DeckException($"This is the account {Label} is using right now. Switch to another account first, then remove it.");
        state.Profiles.Remove(p);
    }

    public string Label => kind == AccountKind.ClaudeCode ? "Claude Code" : "Codex";
}

/// <summary>
/// Claude Code keeps tokens in ~/.claude/.credentials.json ("claudeAiOauth") and the account
/// description in ~/.claude.json ("oauthAccount"). Everything else in those files is left untouched.
/// </summary>
public sealed class ClaudeCodeSwitcher(AppPaths paths, ISecretProtector protector) : CredentialSwitcher(AccountKind.ClaudeCode, protector)
{
    public override bool IsInstalled => Directory.Exists(paths.ClaudeDir) || File.Exists(paths.ClaudeConfigFile);

    public override LiveLogin? ReadLive()
    {
        if (FileUtil.ReadJsonObject(paths.ClaudeCredentialsFile)?["claudeAiOauth"] is not JsonObject oauth) return null;
        if (string.IsNullOrEmpty(oauth["refreshToken"]?.ToString()) && string.IsNullOrEmpty(oauth["accessToken"]?.ToString())) return null;
        var account = FileUtil.ReadJsonObject(paths.ClaudeConfigFile)?["oauthAccount"] as JsonObject;
        var secret = new JsonObject { ["oauth"] = oauth.DeepClone(), ["account"] = account?.DeepClone() };
        return new LiveLogin(
            account?["accountUuid"]?.ToString() ?? "unknown",
            account?["emailAddress"]?.ToString(),
            oauth["subscriptionType"]?.ToString(),
            secret.ToJsonString());
    }

    protected override void WriteLive(string secret)
    {
        var saved = JsonNode.Parse(secret)!.AsObject();

        var credentials = FileUtil.ReadJsonObject(paths.ClaudeCredentialsFile) ?? new JsonObject();
        credentials["claudeAiOauth"] = saved["oauth"]!.DeepClone();
        FileUtil.WriteAtomic(paths.ClaudeCredentialsFile, credentials.ToJsonString(FileUtil.Pretty));

        if (saved["account"] is JsonObject account)
        {
            if (File.Exists(paths.ClaudeConfigFile) && FileUtil.ReadJsonObject(paths.ClaudeConfigFile) is null)
                throw new DeckException("Claude Code's settings file (.claude.json) could not be read, so it was left untouched.");
            var config = FileUtil.ReadJsonObject(paths.ClaudeConfigFile) ?? new JsonObject();
            config["oauthAccount"] = account.DeepClone();
            FileUtil.WriteAtomic(paths.ClaudeConfigFile, config.ToJsonString(FileUtil.Pretty));
        }
    }

    /// <summary>
    /// Asks Anthropic how much of the plan each saved account has used. An expired sign-in is renewed
    /// first when <paramref name="renew"/> is set; the renewed tokens are saved straight away
    /// (and written back to Claude Code for the active account) so nothing is ever signed out.
    /// </summary>
    public async Task RefreshUsageAsync(AppState state, ClaudeApi api, bool renew, CancellationToken ct = default)
    {
        Sync(state);
        foreach (var p in state.Of(AccountKind.ClaudeCode).ToList())
        {
            if (p.Secret is null) continue;
            try
            {
                var saved = JsonNode.Parse(Protector.Unprotect(p.Secret))!.AsObject();
                var oauth = saved["oauth"]!.AsObject();
                var expires = oauth["expiresAt"] is JsonValue v && v.TryGetValue<long>(out var ms) ? ms : 0;
                if (DateTimeOffset.FromUnixTimeMilliseconds(expires) < DateTimeOffset.UtcNow.AddMinutes(1))
                {
                    if (!renew)
                    {
                        p.Usage = new UsageSnapshot { Error = "Sign-in needs renewing. Turn on \"Keep sign-ins fresh\" or open Claude Code once." };
                        continue;
                    }
                    var before = oauth["refreshToken"]?.ToString();
                    if (!await api.RenewAsync(oauth, ct))
                    {
                        p.Usage = new UsageSnapshot { Error = "Signed out. Log in to this account again." };
                        continue;
                    }
                    p.Secret = Protector.Protect(saved.ToJsonString());
                    // Only write back if Claude Code has not changed its sign-in in the meantime.
                    if (state.IsActive(p) && ReadLive() is { } live && live.Key == p.AccountKey
                        && JsonNode.Parse(live.Secret)?["oauth"]?["refreshToken"]?.ToString() == before)
                        WriteLive(saved.ToJsonString());
                }
                p.Usage = await api.GetUsageAsync(oauth["accessToken"]!.ToString(), ct);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException
                                          or InvalidOperationException or System.Security.Cryptography.CryptographicException)
            {
                p.Usage = new UsageSnapshot { Error = e is HttpRequestException or TaskCanceledException ? "Could not reach Anthropic." : "Could not read usage." };
            }
        }
    }
}

/// <summary>Codex keeps its whole sign-in in one file (~/.codex/auth.json), which is swapped as a unit.</summary>
public sealed class CodexSwitcher(AppPaths paths, ISecretProtector protector) : CredentialSwitcher(AccountKind.Codex, protector)
{
    public override bool IsInstalled => Directory.Exists(paths.CodexDir);

    public override LiveLogin? ReadLive()
    {
        if (!File.Exists(paths.CodexAuthFile)) return null;
        string raw;
        try { raw = File.ReadAllText(paths.CodexAuthFile); }
        catch (IOException) { return null; }
        JsonObject? auth;
        try { auth = JsonNode.Parse(raw) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
        if (auth is null) return null;

        var tokens = auth["tokens"] as JsonObject;
        var claims = DecodeJwt(tokens?["id_token"]?.ToString());
        var openai = claims?["https://api.openai.com/auth"] as JsonObject;
        var email = claims?["email"]?.ToString();
        var accountId = openai?["chatgpt_account_id"]?.ToString() ?? tokens?["account_id"]?.ToString();
        var apiKey = auth["OPENAI_API_KEY"]?.ToString();

        if (tokens is not null && (email is not null || accountId is not null))
            return new LiveLogin($"{email}|{accountId}", email, openai?["chatgpt_plan_type"]?.ToString(), raw);
        if (!string.IsNullOrEmpty(apiKey))
            return new LiveLogin("apikey:" + FileUtil.Sha256Short(apiKey), null, "API key", raw);
        return null;
    }

    protected override void WriteLive(string secret) => FileUtil.WriteAtomic(paths.CodexAuthFile, secret);

    private static JsonObject? DecodeJwt(string? jwt)
    {
        var parts = jwt?.Split('.');
        if (parts is not { Length: >= 2 }) return null;
        try
        {
            var body = parts[1].Replace('-', '+').Replace('_', '/');
            body = body.PadRight(body.Length + (4 - body.Length % 4) % 4, '=');
            return JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(body))) as JsonObject;
        }
        catch (Exception e) when (e is FormatException or System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
