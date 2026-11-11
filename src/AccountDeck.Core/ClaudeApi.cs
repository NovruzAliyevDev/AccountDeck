using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace AccountDeck.Core;

/// <summary>
/// The two Anthropic endpoints Claude Code itself uses for a subscription sign-in:
/// plan usage, and renewing an expired access token. Tokens are only ever sent to Anthropic.
/// </summary>
public sealed class ClaudeApi(HttpClient http)
{
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private const string TokenUrl = "https://platform.claude.com/v1/oauth/token";
    private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";

    public async Task<UsageSnapshot> GetUsageAsync(string accessToken, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return new UsageSnapshot { Error = (int)response.StatusCode == 401 ? "Signed out. Log in to this account again." : $"Usage is unavailable ({(int)response.StatusCode})." };

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonObject;
        return new UsageSnapshot
        {
            FiveHour = Number(body?["five_hour"]?["utilization"]),
            FiveHourResetsAt = Time(body?["five_hour"]?["resets_at"]),
            SevenDay = Number(body?["seven_day"]?["utilization"]),
            SevenDayResetsAt = Time(body?["seven_day"]?["resets_at"]),
        };
    }

    /// <summary>Renews the tokens inside <paramref name="oauth"/> in place. False means the sign-in is no longer valid.</summary>
    public async Task<bool> RenewAsync(JsonObject oauth, CancellationToken ct = default)
    {
        var refreshToken = oauth["refreshToken"]?.ToString();
        if (string.IsNullOrEmpty(refreshToken)) return false;

        var payload = new JsonObject
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = ClientId,
        };
        if (oauth["scopes"] is JsonArray scopes && scopes.Count > 0)
            payload["scope"] = string.Join(' ', scopes.Select(s => s?.ToString()));

        using var response = await http.PostAsJsonAsync(TokenUrl, payload, ct);
        if (!response.IsSuccessStatusCode) return false;
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonObject;
        var access = body?["access_token"]?.ToString();
        if (string.IsNullOrEmpty(access)) return false;

        var now = DateTimeOffset.UtcNow;
        oauth["accessToken"] = access;
        if (body!["refresh_token"]?.ToString() is { Length: > 0 } rotated) oauth["refreshToken"] = rotated;
        oauth["expiresAt"] = now.AddSeconds(Number(body["expires_in"]) ?? 3600).ToUnixTimeMilliseconds();
        if (Number(body["refresh_token_expires_in"]) is { } refreshLife)
            oauth["refreshTokenExpiresAt"] = now.AddSeconds(refreshLife).ToUnixTimeMilliseconds();
        return true;
    }

    private static double? Number(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    private static DateTimeOffset? Time(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s)
        && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t.ToLocalTime() : null;
}
