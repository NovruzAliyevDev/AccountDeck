using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AccountDeck.Core;
using Xunit;

namespace AccountDeck.Tests;

/// <summary>A throwaway copy of the folders the app works on.</summary>
public sealed class Sandbox : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "adeck-" + Guid.NewGuid().ToString("N")[..8]);
    public AppPaths Paths { get; }
    public FakeHost Host { get; }
    public string Live => Host.Install!.DataDir;

    public Sandbox()
    {
        FileUtil.RetryDelayOverrideMs = 1;
        Paths = new AppPaths(Path.Combine(Root, "Local"), Path.Combine(Root, "Roaming"), Path.Combine(Root, "Home"));
        Host = new FakeHost(new DesktopInstall(Path.Combine(Paths.Local, "Claude-Data", "claude_test"), true, "Claude_test!Claude", null));
        Directory.CreateDirectory(Paths.Local);
    }

    public void SeedLive(string who, string? accountUuid = null)
    {
        Directory.CreateDirectory(Path.Combine(Live, "Network"));
        File.WriteAllText(Path.Combine(Live, "Network", "Cookies"), who);
        File.WriteAllText(Path.Combine(Live, "claude_desktop_config.json"), "{\"preferences\":{\"from\":\"" + who + "\"}}");
        if (accountUuid is not null)
            File.WriteAllText(Path.Combine(Live, "config.json"), "{\"lastKnownAccountUuid\":\"" + accountUuid + "\"}");
    }

    public string LiveCookies => File.ReadAllText(Path.Combine(Live, "Network", "Cookies"));

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch (IOException) { }
    }
}

public sealed class FakeHost(DesktopInstall? install) : IDesktopHost
{
    public DesktopInstall? Install { get; set; } = install;
    public int Closed, Launched;
    public bool CloseSucceeds = true;
    public DesktopInstall? Detect() => Install;
    public bool IsRunning(DesktopInstall i) => false;
    public bool Close(DesktopInstall i, TimeSpan timeout) { Closed++; return CloseSucceeds; }
    public void Launch(DesktopInstall i) => Launched++;
    public void DeleteFolder(string dir) => Directory.Delete(dir, true);
}

public sealed class PlainProtector : ISecretProtector
{
    public string Protect(string plain) => Convert.ToBase64String(Encoding.UTF8.GetBytes(plain));
    public string Unprotect(string cipher) => Encoding.UTF8.GetString(Convert.FromBase64String(cipher));
}

public class DesktopTests
{
    [Fact]
    public void First_run_saves_the_current_sign_in_as_a_profile()
    {
        using var box = new Sandbox();
        box.SeedLive("alice", "uuid-a");
        var state = new AppState();
        var desktop = new DesktopSwitcher(box.Paths, box.Host);

        desktop.Reconcile(state);

        var p = Assert.Single(state.Profiles);
        Assert.Equal("Account 1", p.Name);
        Assert.Equal("uuid-a", p.AccountKey);
        Assert.True(state.IsActive(p));
        Assert.True(File.Exists(Path.Combine(box.Live, DesktopSwitcher.MarkerName)));
        Assert.Equal("alice", box.LiveCookies); // nothing moved
    }

    [Fact]
    public void Adding_an_account_parks_the_current_one_and_starts_clean()
    {
        using var box = new Sandbox();
        box.SeedLive("alice");
        var state = new AppState();
        var desktop = new DesktopSwitcher(box.Paths, box.Host);

        var bob = desktop.AddNew(state, "Bob", copySettings: true);

        Assert.Equal(2, state.Profiles.Count);
        Assert.True(state.IsActive(bob));
        Assert.False(Directory.Exists(Path.Combine(box.Live, "Network"))); // clean: no cookies carried over
        Assert.Contains("alice", File.ReadAllText(Path.Combine(box.Live, "claude_desktop_config.json"))); // settings copied
        var alice = state.Profiles.Single(p => p != bob);
        Assert.Equal("alice", File.ReadAllText(Path.Combine(box.Paths.DesktopStore, alice.Id, "Network", "Cookies")));
        Assert.Equal(1, box.Host.Closed);
        Assert.Equal(1, box.Host.Launched);
    }

    [Fact]
    public void Switching_swaps_the_whole_data_folder_both_ways()
    {
        using var box = new Sandbox();
        box.SeedLive("alice");
        var state = new AppState();
        var desktop = new DesktopSwitcher(box.Paths, box.Host);
        var bob = desktop.AddNew(state, "Bob", copySettings: false);
        box.SeedLive("bob");
        var alice = state.Profiles.Single(p => p != bob);

        desktop.Switch(state, alice.Id);
        Assert.Equal("alice", box.LiveCookies);
        Assert.True(state.IsActive(alice));

        desktop.Switch(state, bob.Id);
        Assert.Equal("bob", box.LiveCookies);
        Assert.True(state.IsActive(bob));
        Assert.Single(Directory.GetDirectories(box.Paths.DesktopStore));
    }

    [Fact]
    public void A_failed_switch_puts_the_current_account_back()
    {
        using var box = new Sandbox();
        box.SeedLive("alice");
        var state = new AppState();
        var desktop = new DesktopSwitcher(box.Paths, box.Host);
        var bob = desktop.AddNew(state, "Bob", copySettings: false);
        box.SeedLive("bob");
        var alice = state.Profiles.Single(p => p != bob);

        // Something keeps a file open inside the account we want to switch to.
        using (File.Open(Path.Combine(box.Paths.DesktopStore, alice.Id, "Network", "Cookies"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Throws<DeckException>(() => desktop.Switch(state, alice.Id));
        }

        Assert.Equal("bob", box.LiveCookies);
        Assert.True(state.IsActive(bob));
        Assert.True(Directory.Exists(Path.Combine(box.Paths.DesktopStore, alice.Id)));
    }

    [Fact]
    public void Nothing_moves_when_Claude_will_not_close()
    {
        using var box = new Sandbox();
        box.SeedLive("alice");
        var state = new AppState();
        var desktop = new DesktopSwitcher(box.Paths, box.Host);
        box.Host.CloseSucceeds = false;

        Assert.Throws<DeckException>(() => desktop.AddNew(state, "Bob", copySettings: false));

        Assert.Equal("alice", box.LiveCookies);
        Assert.Single(state.Profiles);
    }

    [Fact]
    public void An_interrupted_switch_is_repaired_on_next_start()
    {
        using var box = new Sandbox();
        box.SeedLive("alice");
        var state = new AppState();
        var desktop = new DesktopSwitcher(box.Paths, box.Host);
        desktop.Reconcile(state);
        var alice = state.Profiles.Single();
        // Simulate a crash right after the live folder was parked.
        Directory.Move(box.Live, Path.Combine(Directory.CreateDirectory(box.Paths.DesktopStore).FullName, alice.Id));

        desktop.Reconcile(state);

        Assert.Equal("alice", box.LiveCookies);
        Assert.True(state.IsActive(alice));
    }

    [Fact]
    public void Lost_state_is_rebuilt_from_the_folders()
    {
        using var box = new Sandbox();
        box.SeedLive("alice");
        var state = new AppState();
        var desktop = new DesktopSwitcher(box.Paths, box.Host);
        desktop.AddNew(state, "Bob", copySettings: false);

        var fresh = new AppState();
        new DesktopSwitcher(box.Paths, box.Host).Reconcile(fresh);

        Assert.Equal(2, fresh.Profiles.Count);
        Assert.Equal("Bob", fresh.Active(AccountKind.Desktop)!.Name);
        Assert.Contains(fresh.Profiles, p => p.Name == "Account 1");
    }

    [Fact]
    public void The_active_account_cannot_be_removed_but_a_parked_one_can()
    {
        using var box = new Sandbox();
        box.SeedLive("alice");
        var state = new AppState();
        var desktop = new DesktopSwitcher(box.Paths, box.Host);
        var bob = desktop.AddNew(state, "Bob", copySettings: false);
        var alice = state.Profiles.Single(p => p != bob);

        Assert.Throws<DeckException>(() => desktop.Remove(state, bob.Id));
        desktop.Remove(state, alice.Id);

        Assert.Single(state.Profiles);
        Assert.False(Directory.Exists(Path.Combine(box.Paths.DesktopStore, alice.Id)));
    }

    [Fact]
    public void Usage_recorded_by_Claude_Desktop_is_shown()
    {
        using var box = new Sandbox();
        box.SeedLive("alice");
        File.WriteAllText(Path.Combine(box.Live, "plan-usage-history.json"),
            "{\"version\":2,\"samples\":[{\"t\":1791874051774,\"org\":\"o\",\"u\":{\"fh\":2,\"sd\":9}},{\"t\":1792397821880,\"org\":\"o\",\"u\":{\"fh\":30,\"sd\":35.5}}]}");
        var state = new AppState();

        new DesktopSwitcher(box.Paths, box.Host).Reconcile(state);

        var usage = state.Profiles.Single().Usage!;
        Assert.Equal(30, usage.FiveHour);
        Assert.Equal(35.5, usage.SevenDay);
    }
}

public class ClaudeCodeTests
{
    private static void SignIn(Sandbox box, string who, long expiresAt = 4102444800000)
    {
        Directory.CreateDirectory(box.Paths.ClaudeDir);
        var credentials = FileUtil.ReadJsonObject(box.Paths.ClaudeCredentialsFile) ?? new JsonObject { ["mcpOAuth"] = new JsonObject { ["server"] = "keep-me" } };
        credentials["claudeAiOauth"] = new JsonObject
        {
            ["accessToken"] = "access-" + who, ["refreshToken"] = "refresh-" + who, ["expiresAt"] = expiresAt,
            ["scopes"] = new JsonArray("user:inference"), ["subscriptionType"] = "max",
        };
        File.WriteAllText(box.Paths.ClaudeCredentialsFile, credentials.ToJsonString());
        var config = FileUtil.ReadJsonObject(box.Paths.ClaudeConfigFile) ?? new JsonObject { ["numStartups"] = 7, ["projects"] = new JsonObject { ["C:/x"] = new JsonObject() } };
        config["oauthAccount"] = new JsonObject { ["accountUuid"] = "uuid-" + who, ["emailAddress"] = who + "@example.com" };
        File.WriteAllText(box.Paths.ClaudeConfigFile, config.ToJsonString());
    }

    [Fact]
    public void New_sign_ins_are_saved_automatically_and_switching_restores_them()
    {
        using var box = new Sandbox();
        var state = new AppState();
        var code = new ClaudeCodeSwitcher(box.Paths, new PlainProtector());

        Assert.Null(code.Sync(state)); // nobody signed in yet
        SignIn(box, "alice");
        var alice = code.Sync(state)!;
        SignIn(box, "bob");
        var bob = code.Sync(state)!;

        Assert.Equal(2, state.Profiles.Count);
        Assert.Equal("alice@example.com", alice.Name);
        Assert.Equal("max", alice.Plan);
        Assert.True(state.IsActive(bob));

        code.Switch(state, alice.Id);

        var credentials = FileUtil.ReadJsonObject(box.Paths.ClaudeCredentialsFile)!;
        var config = FileUtil.ReadJsonObject(box.Paths.ClaudeConfigFile)!;
        Assert.Equal("refresh-alice", credentials["claudeAiOauth"]!["refreshToken"]!.ToString());
        Assert.Equal("keep-me", credentials["mcpOAuth"]!["server"]!.ToString()); // unrelated data untouched
        Assert.Equal("uuid-alice", config["oauthAccount"]!["accountUuid"]!.ToString());
        Assert.Equal(7, config["numStartups"]!.GetValue<int>());
        Assert.NotNull(config["projects"]!["C:/x"]);
        Assert.True(state.IsActive(alice));
        Assert.Equal(2, state.Profiles.Count);
    }

    [Fact]
    public async Task Usage_is_fetched_and_an_expired_sign_in_is_renewed_and_written_back()
    {
        using var box = new Sandbox();
        var state = new AppState();
        var code = new ClaudeCodeSwitcher(box.Paths, new PlainProtector());
        SignIn(box, "alice", expiresAt: 1000); // long expired
        var requests = new List<string>();
        var api = new ClaudeApi(new HttpClient(new StubHandler(async request =>
        {
            requests.Add(request.RequestUri!.AbsolutePath + " " + request.Headers.Authorization?.Parameter
                         + (request.Content is null ? "" : " " + await request.Content.ReadAsStringAsync()));
            return request.RequestUri.AbsolutePath.EndsWith("/token")
                ? "{\"access_token\":\"access-new\",\"refresh_token\":\"refresh-new\",\"expires_in\":3600}"
                : "{\"five_hour\":{\"utilization\":42.5,\"resets_at\":\"2030-01-01T10:00:00Z\"},\"seven_day\":{\"utilization\":12,\"resets_at\":null}}";
        })));

        await code.RefreshUsageAsync(state, api, renew: true);

        var p = state.Profiles.Single();
        Assert.Null(p.Usage!.Error);
        Assert.Equal(42.5, p.Usage.FiveHour);
        Assert.Equal(12, p.Usage.SevenDay);
        Assert.NotNull(p.Usage.FiveHourResetsAt);
        Assert.Contains("refresh-alice", requests[0]);
        Assert.Equal("/api/oauth/usage access-new", requests[1]);
        var live = FileUtil.ReadJsonObject(box.Paths.ClaudeCredentialsFile)!["claudeAiOauth"]!;
        Assert.Equal("refresh-new", live["refreshToken"]!.ToString());
        Assert.True(live["expiresAt"]!.GetValue<long>() > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.Equal("keep-me", FileUtil.ReadJsonObject(box.Paths.ClaudeCredentialsFile)!["mcpOAuth"]!["server"]!.ToString());
    }

    [Fact]
    public async Task Without_renewal_an_expired_sign_in_is_reported_and_no_request_is_made()
    {
        using var box = new Sandbox();
        var state = new AppState();
        var code = new ClaudeCodeSwitcher(box.Paths, new PlainProtector());
        SignIn(box, "alice", expiresAt: 1000);
        var calls = 0;
        var api = new ClaudeApi(new HttpClient(new StubHandler(_ => { calls++; return Task.FromResult("{}"); })));

        await code.RefreshUsageAsync(state, api, renew: false);

        Assert.Equal(0, calls);
        Assert.NotNull(state.Profiles.Single().Usage!.Error);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<string>> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            new(HttpStatusCode.OK) { Content = new StringContent(await respond(request)) };
    }
}

public class CodexTests
{
    private static string Jwt(string json) =>
        "e30." + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".sig";

    private static void SignIn(Sandbox box, string who)
    {
        Directory.CreateDirectory(box.Paths.CodexDir);
        var claims = "{\"email\":\"" + who + "@example.com\",\"https://api.openai.com/auth\":{\"chatgpt_account_id\":\"acct-" + who + "\",\"chatgpt_plan_type\":\"plus\"}}";
        File.WriteAllText(box.Paths.CodexAuthFile, "{\"OPENAI_API_KEY\":null,\"tokens\":{\"id_token\":\"" + Jwt(claims) + "\",\"refresh_token\":\"r-" + who + "\"}}");
    }

    [Fact]
    public void Codex_sign_ins_are_recognised_and_swapped_as_a_whole_file()
    {
        using var box = new Sandbox();
        var state = new AppState();
        var codex = new CodexSwitcher(box.Paths, new PlainProtector());
        SignIn(box, "alice");
        var alice = codex.Sync(state)!;
        var aliceFile = File.ReadAllText(box.Paths.CodexAuthFile);
        SignIn(box, "bob");
        codex.Sync(state);

        Assert.Equal("alice@example.com", alice.Email);
        Assert.Equal("plus", alice.Plan);
        Assert.Equal(2, state.Profiles.Count);

        codex.Switch(state, alice.Id);

        Assert.Equal(aliceFile, File.ReadAllText(box.Paths.CodexAuthFile));
        Assert.True(state.IsActive(alice));
    }

    [Fact]
    public void An_API_key_login_is_recognised()
    {
        using var box = new Sandbox();
        Directory.CreateDirectory(box.Paths.CodexDir);
        File.WriteAllText(box.Paths.CodexAuthFile, "{\"OPENAI_API_KEY\":\"sk-test\"}");
        var state = new AppState();

        var p = new CodexSwitcher(box.Paths, new PlainProtector()).Sync(state)!;

        Assert.Equal("API key", p.Plan);
    }
}

public class StateTests
{
    [Fact]
    public void State_survives_a_save_and_load_and_secrets_are_encrypted()
    {
        using var box = new Sandbox();
        var store = new StateStore(box.Paths);
        var dpapi = new DpapiProtector();
        var state = new AppState();
        state.Profiles.Add(new Profile { Kind = AccountKind.ClaudeCode, Name = "A", Secret = dpapi.Protect("top-secret") });
        state.ActiveIds[AccountKind.ClaudeCode] = state.Profiles[0].Id;
        state.Settings.AutoSwitchClaudeCode = true;

        store.Save(state);
        var loaded = store.Load();

        Assert.DoesNotContain("top-secret", File.ReadAllText(box.Paths.StateFile));
        Assert.Equal("top-secret", dpapi.Unprotect(loaded.Profiles.Single().Secret!));
        Assert.True(loaded.IsActive(loaded.Profiles[0]));
        Assert.True(loaded.Settings.AutoSwitchClaudeCode);
    }
}
