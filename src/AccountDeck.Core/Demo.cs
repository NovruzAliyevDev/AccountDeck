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

        SeedClaudeFolder(paths);
        state.Log.Clear();
        new StateStore(paths).Save(state);
        return (paths, host, new HttpClient(new DemoApi()));
    }

    /// <summary>A small mixed ~/.claude, so "Set up spaces" has instructions, skills and projects to sort.</summary>
    private static void SeedClaudeFolder(AppPaths paths)
    {
        void Write(string rel, string text)
        {
            var path = Path.Combine(paths.ClaudeDir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        Write("CLAUDE.md", "# My rules\n\n## Read first, act later\nList the problems before editing anything.\n\n" +
                           "## Company database\nUse the Acme dev database for every Acme ticket.\n\n## Commit messages\nStart with the ticket key.\n");
        Write("skills/review-ticket/SKILL.md", "---\nname: review-ticket\ndescription: Review an Acme ticket before handing it to QA\n---\n");
        Write("skills/sketch/SKILL.md", "---\nname: sketch\ndescription: Turn an idea into a quick diagram\n---\n");
        Write("settings.json", "{}");
        var history = new System.Text.StringBuilder();
        foreach (var (project, session, n) in new[] { ("C:\\Acme\\Billing", "demo-1", 3), ("C:\\Acme\\Portal", "demo-2", 5), ("D:\\Games\\Tiles", "demo-3", 2) })
        {
            var dir = SpaceItems.ProjectDirName(project);
            for (var i = 0; i < n; i++)
                Write($"projects/{dir}/{session}-{i}.jsonl", new JsonObject { ["cwd"] = project }.ToJsonString() + "\n");
            history.Append(new JsonObject { ["display"] = "demo prompt", ["timestamp"] = n, ["project"] = project }.ToJsonString()).Append('\n');
        }
        Write("history.jsonl", history.ToString());
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
