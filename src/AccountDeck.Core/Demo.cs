using System.Net;
using System.Text.Json.Nodes;

namespace AccountDeck.Core;

/// <summary>
/// "AccountDeck --demo": the full app on sample accounts in a temporary folder.
/// It never looks at real sign-ins, never closes the real Claude Desktop and never goes online.
/// </summary>
public static class Demo
{
    public static (AppPaths, IDesktopHost, HttpClient) Create(ISecretProtector protector)
    {
        var root = Path.Combine(Path.GetTempPath(), "AccountDeck-demo");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var paths = new AppPaths(Path.Combine(root, "Local"), Path.Combine(root, "Roaming"), Path.Combine(root, "Home"));
        var host = new DemoHost(new DesktopInstall(Path.Combine(paths.Local, "Claude-Data", "claude_demo"), true, null, null));
        var state = new AppState { Settings = { ShareDesktopSessions = false } };

        // Two Claude Code sign-ins, saved the same way a real one would be.
        var code = new ClaudeCodeSwitcher(paths, protector);
        foreach (var who in new[] { "personal", "work" })
        {
            Directory.CreateDirectory(paths.ClaudeDir);
            File.WriteAllText(paths.ClaudeCredentialsFile, new JsonObject
            {
                ["claudeAiOauth"] = new JsonObject
                {
                    ["accessToken"] = "demo-" + who, ["refreshToken"] = "demo-refresh-" + who,
                    ["expiresAt"] = DateTimeOffset.UtcNow.AddYears(5).ToUnixTimeMilliseconds(), ["subscriptionType"] = who == "work" ? "team" : "max",
                },
            }.ToJsonString());
            File.WriteAllText(paths.ClaudeConfigFile, new JsonObject
            {
                ["oauthAccount"] = new JsonObject { ["accountUuid"] = "demo-" + who, ["emailAddress"] = who + "@example.com" },
            }.ToJsonString());
            code.Sync(state);
        }

        // Two Claude Desktop accounts: "Work" parked, "Personal" live.
        var desktop = new DesktopSwitcher(paths, host);
        SeedDesktop(host.Install.DataDir, "demo-work", 64, 81);
        desktop.Reconcile(state);
        state.Of(AccountKind.Desktop).Single().Name = "Work";
        desktop.AddNew(state, "Personal", copySettings: false, relaunch: false);
        SeedDesktop(host.Install.DataDir, "demo-personal", 12, 30);

        state.Log.Clear();
        new StateStore(paths).Save(state);
        return (paths, host, new HttpClient(new DemoApi()));
    }

    private static void SeedDesktop(string dir, string accountUuid, int fiveHour, int sevenDay)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "config.json"), new JsonObject { ["lastKnownAccountUuid"] = accountUuid }.ToJsonString());
        File.WriteAllText(Path.Combine(dir, "plan-usage-history.json"), new JsonObject
        {
            ["samples"] = new JsonArray(new JsonObject
            {
                ["t"] = DateTimeOffset.UtcNow.AddMinutes(-20).ToUnixTimeMilliseconds(),
                ["u"] = new JsonObject { ["fh"] = fiveHour, ["sd"] = sevenDay },
            }),
        }.ToJsonString());
    }

    private sealed class DemoHost(DesktopInstall install) : IDesktopHost
    {
        public DesktopInstall Install { get; } = install;
        public DesktopInstall? Detect() => Install;
        public bool IsRunning(DesktopInstall i) => true;
        public bool Close(DesktopInstall i, TimeSpan timeout) { Thread.Sleep(1200); return true; }
        public void Launch(DesktopInstall i) { }
        public void DeleteFolder(string dir) => Directory.Delete(dir, true);
    }

    private sealed class DemoApi : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var work = request.Headers.Authorization?.Parameter?.EndsWith("work") == true;
            var reset = DateTimeOffset.UtcNow.AddHours(2).ToString("o");
            var week = DateTimeOffset.UtcNow.AddDays(3).ToString("o");
            var body = work
                ? $"{{\"five_hour\":{{\"utilization\":93,\"resets_at\":\"{reset}\"}},\"seven_day\":{{\"utilization\":71,\"resets_at\":\"{week}\"}}}}"
                : $"{{\"five_hour\":{{\"utilization\":18,\"resets_at\":\"{reset}\"}},\"seven_day\":{{\"utilization\":34,\"resets_at\":\"{week}\"}}}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
