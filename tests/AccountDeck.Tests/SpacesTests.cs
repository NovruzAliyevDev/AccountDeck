using System.Text.Json.Nodes;
using AccountDeck.Core;
using Xunit;

namespace AccountDeck.Tests;

public class SpacesTests
{
    private const string ClaudeMd = """
        # My rules

        Answer in short sentences.

        ## Read first, act later
        List the problems before editing.

        ## Tayqa database
        Use the tayqasale-db plugin for the dev database.

        ```md
        ## not a heading inside a fence
        ```
        """;

    /// <summary>A ~/.claude with one work project, one personal project, a skill each, settings and plugins.</summary>
    private static (Sandbox Box, string Claude) Seed()
    {
        var box = new Sandbox();
        var c = box.Paths.ClaudeDir;
        Write(c, "CLAUDE.md", ClaudeMd);
        Write(c, "skills/test-ticket/SKILL.md", "---\nname: test-ticket\ndescription: Test a Tayqa ticket\n---\nbody");
        Write(c, "skills/drawing/SKILL.md", "---\nname: drawing\ndescription: Sketch things\n---\nbody");
        Write(c, "settings.json", "{\"model\":\"work\"}");
        Write(c, "plugins/installed_plugins.json", "{}");
        Write(c, ".credentials.json", "{}");
        Project(c, "C:\\Work\\Erp", "s-work");
        Project(c, "D:\\GameDev\\Necro", "s-game");
        File.WriteAllText(Path.Combine(c, "history.jsonl"),
            Line("C:\\Work\\Erp", 1) + "\n" + Line("D:\\GameDev\\Necro", 2) + "\n" + Line("C:\\Work\\Erp", 3) + "\n");
        File.WriteAllText(box.Paths.ClaudeConfigFile, "{\"mcpServers\":{\"work-db\":{}}}");
        return (box, c);
    }

    private static string Line(string project, long t) => new JsonObject { ["display"] = "p" + t, ["timestamp"] = t, ["project"] = project }.ToJsonString();

    private static void Project(string claude, string path, string session)
    {
        var dir = SpaceItems.ProjectDirName(path);
        Write(claude, $"projects/{dir}/{session}.jsonl", new JsonObject { ["type"] = "user", ["cwd"] = path }.ToJsonString() + "\n");
        Write(claude, $"projects/{dir}/memory/MEMORY.md", "memory of " + path);
        Write(claude, $"file-history/{session}/abc@v1", "old version");
    }

    private static void Write(string root, string rel, string text)
    {
        var path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static bool Has(string root, string rel) => SpaceItems.Exists(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>Work is in use; the game project, the drawing skill and its rules go to Personal.</summary>
    private static (Space Work, Space Personal, SpaceSetupPlan Plan) WorkAndPersonal(Sandbox box, AppState state)
    {
        var work = new Space { Name = "Work" };
        var personal = new Space { Name = "Personal" };
        state.Spaces.List = [work, personal];
        var items = SpaceItems.List(box.Paths, state);
        var choices = items.ToDictionary(i => i.Key, i => SpaceItems.Suggest(i, work.Id, ["Tayqa"]));
        choices["skills/drawing"] = ItemChoice.Only(personal.Id);
        var projects = SpaceItems.Projects(box.Paths);
        var owners = projects.ToDictionary(p => p.DirName, p => SpaceItems.SuggestProject(p, [("D:\\GameDev", personal.Id)], work.Id));
        return (work, personal, new SpaceSetupPlan
        {
            Spaces = [work, personal], LiveSpaceId = work.Id, Items = items, Choices = choices, ProjectOwners = owners,
        });
    }

    [Fact]
    public void Each_section_of_CLAUDE_md_and_each_skill_is_its_own_row_with_a_first_guess()
    {
        var (box, _) = Seed();
        using var _box = box;
        var work = new Space { Name = "Work" };

        var items = SpaceItems.List(box.Paths, new AppState());

        Assert.Equal(["CLAUDE.md", "rules/read-first-act-later.md", "rules/tayqa-database.md"],
            items.Where(i => i.Group == ItemGroup.Instructions).Select(i => i.Key));
        Assert.Contains("## not a heading inside a fence", items.Single(i => i.Key == "rules/tayqa-database.md").SectionText);
        Assert.Equal(["test-ticket", "drawing"], items.Where(i => i.Group == ItemGroup.Skills).Select(i => i.Title).OrderByDescending(t => t));
        Assert.Equal(["settings.json", "plugins", "home/.claude.json"], items.Where(i => i.Group == ItemGroup.Settings).Select(i => i.Key));

        ItemChoice Guess(string key) => SpaceItems.Suggest(items.Single(i => i.Key == key), work.Id, ["Tayqa"]);
        Assert.Equal(ItemMode.Shared, Guess("rules/read-first-act-later.md").Mode);
        Assert.Equal(ItemMode.OnlyIn, Guess("rules/tayqa-database.md").Mode);
        Assert.Equal(ItemMode.OnlyIn, Guess("skills/test-ticket").Mode);
        Assert.Equal(ItemMode.Separate, Guess("settings.json").Mode);
    }

    [Fact]
    public void Names_in_the_instructions_are_listed_so_nobody_has_to_remember_them()
    {
        SpaceItem Rule(string title, string text) => new("rules/" + SpaceItems.Slug(title) + ".md", ItemGroup.Instructions, title, "Rule file", text);
        var items = new List<SpaceItem>
        {
            Rule("Ticket naming", "## Ticket naming\nWhen given a Jira ticket like ACM-1234, rename the session. Never push."),
            Rule("Database", "## Database\nUse the acme-db plugin for Acme dev databases. Ask before writes to AcmePortal."),
            Rule("Style", "## Style\nAnswer in short sentences. Read files first.\n- Keep it simple."),
            new("skills/review", ItemGroup.Skills, "review", "Review an Acme ticket in Jira", "---\nname: review\n---\nCheck the AcmePortal screens."),
            new("settings.json", ItemGroup.Settings, "Settings and permissions", "", ""),
        };

        var names = SpaceItems.FindNames(items);
        var words = names.Select(n => n.Word).ToList();

        Assert.Contains("Acme", words);
        Assert.Contains("Jira", words);
        Assert.Contains("ACM", words);          // ticket prefix from ACM-1234
        Assert.Contains("AcmePortal", words);
        Assert.Contains("acme-db", words);
        Assert.Equal(2, names.Single(n => n.Word == "Jira").Items.Count);
        // Grammar and common words are not names.
        Assert.DoesNotContain("When", words);
        Assert.DoesNotContain("Answer", words);
        Assert.DoesNotContain("Keep", words);
        Assert.DoesNotContain("Never", words);
        Assert.DoesNotContain("Database", words); // only ever a heading, at the start of a line
    }

    [Fact]
    public void Projects_are_found_with_their_real_path_and_sorted_by_path_rules()
    {
        var (box, _) = Seed();
        using var _box = box;

        var projects = SpaceItems.Projects(box.Paths);

        var game = projects.Single(p => p.ProjectPath == "D:\\GameDev\\Necro");
        Assert.Equal(1, game.Conversations);
        Assert.Equal("personal", SpaceItems.SuggestProject(game, [("D:/GameDev/", "personal"), ("D:\\", "other")], "work"));
        Assert.Equal("work", SpaceItems.SuggestProject(projects.Single(p => p.ProjectPath == "C:\\Work\\Erp"), [("D:\\GameDev", "personal")], "work"));
    }

    [Fact]
    public void A_project_lists_its_conversations_by_title_or_first_prompt()
    {
        using var box = new Sandbox();
        var dir = Path.Combine(box.Paths.ClaudeDir, "projects", "C--x");
        Directory.CreateDirectory(Path.Combine(dir, "s2"));
        File.WriteAllText(Path.Combine(dir, "s1.jsonl"), string.Join("\n",
            "{\"type\":\"user\",\"isMeta\":true,\"message\":{\"content\":\"meta\"}}",
            "{\"type\":\"user\",\"message\":{\"content\":\"<command-name>/login</command-name>\"}}",
            "{\"type\":\"user\",\"timestamp\":\"2026-10-08T12:00:00Z\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"how do I   restart?\"}]}}",
            "{\"type\":\"user\",\"message\":{\"content\":\"second prompt\"}}"));
        File.WriteAllText(Path.Combine(dir, "s2.jsonl"), "{\"type\":\"user\",\"message\":{\"content\":\"hi\"}}\n{\"type\":\"custom-title\",\"customTitle\":\"Old name\"}\n");
        File.WriteAllText(Path.Combine(dir, "s2", "custom-title.json"), "{\"customTitle\":\"Plugin setup\"}");
        File.WriteAllText(Path.Combine(dir, "s3.jsonl"), "{\"type\":\"user\",\"message\":{\"content\":\"<command-name>/login</command-name>\"}}\n");

        var list = SpaceItems.Conversations(box.Paths, "C--x");

        var s1 = list.Single(c => c.SessionId == "s1");
        Assert.Equal("how do I restart?", s1.FirstPrompt);
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero), s1.Started);
        Assert.Null(s1.Title);
        Assert.Equal("Plugin setup", list.Single(c => c.SessionId == "s2").Title); // the title shown in Claude's sidebar wins
        Assert.Null(list.Single(c => c.SessionId == "s3").FirstPrompt);
    }

    [Fact]
    public void Setup_splits_everything_and_switching_brings_each_side_back()
    {
        var (box, c) = Seed();
        using var _box = box;
        var state = new AppState();
        var (work, personal, plan) = WorkAndPersonal(box, state);
        var spaces = new SpaceSwitcher(box.Paths);
        var gameDir = SpaceItems.ProjectDirName("D:\\GameDev\\Necro");
        var workDir = SpaceItems.ProjectDirName("C:\\Work\\Erp");

        var backup = spaces.SetUp(state, plan);

        // Work is live: only work conversations, work prompts, all shared and work-only instructions.
        Assert.True(Has(c, $"projects/{workDir}/s-work.jsonl"));
        Assert.False(Has(c, $"projects/{gameDir}"));
        Assert.False(Has(c, "file-history/s-game"));
        Assert.Equal(["p1", "p3"], File.ReadAllLines(Path.Combine(c, "history.jsonl")).Select(l => JsonNode.Parse(l)!["display"]!.ToString()));
        Assert.True(Has(c, "rules/read-first-act-later.md"));
        Assert.True(Has(c, "rules/tayqa-database.md"));
        Assert.DoesNotContain("##", File.ReadAllText(Path.Combine(c, "CLAUDE.md")));
        Assert.Contains("Answer in short sentences.", File.ReadAllText(Path.Combine(c, "CLAUDE.md")));
        Assert.True(Has(c, "skills/test-ticket"));
        Assert.False(Has(c, "skills/drawing"));
        Assert.True(File.Exists(Path.Combine(backup, "CLAUDE.md")));
        Assert.Contains("## Tayqa database", File.ReadAllText(Path.Combine(backup, "CLAUDE.md")));

        // Personal's part is parked, settings were copied for it.
        var parked = Path.Combine(box.Paths.SpacesStore, personal.Id);
        Assert.True(Has(parked, $"projects/{gameDir}/memory/MEMORY.md"));
        Assert.True(Has(parked, "file-history/s-game/abc@v1"));
        Assert.True(Has(parked, "skills/drawing/SKILL.md"));
        Assert.Equal("{\"model\":\"work\"}", File.ReadAllText(Path.Combine(parked, "settings.json")));
        Assert.True(Has(parked, "home/.claude.json"));

        spaces.Switch(state, personal.Id);

        Assert.Equal(personal.Id, state.Spaces.ActiveId);
        Assert.True(Has(c, $"projects/{gameDir}/s-game.jsonl"));
        Assert.False(Has(c, $"projects/{workDir}"));
        Assert.Equal(["p2"], File.ReadAllLines(Path.Combine(c, "history.jsonl")).Select(l => JsonNode.Parse(l)!["display"]!.ToString()));
        Assert.True(Has(c, "rules/read-first-act-later.md")); // shared: never moves
        Assert.False(Has(c, "rules/tayqa-database.md"));      // work only
        Assert.False(Has(c, "skills/test-ticket"));
        Assert.True(Has(c, "skills/drawing"));
        Assert.True(Has(c, ".credentials.json"));             // sign-in file stays for the switcher to rewrite

        // Personal changes its own settings; work's copy is untouched.
        File.WriteAllText(Path.Combine(c, "settings.json"), "{\"model\":\"personal\"}");
        spaces.Switch(state, work.Id);
        Assert.Equal("{\"model\":\"work\"}", File.ReadAllText(Path.Combine(c, "settings.json")));
        Assert.True(Has(c, $"projects/{workDir}"));
        Assert.True(Has(c, "rules/tayqa-database.md"));
        Assert.False(Has(c, $"projects/{gameDir}"));
        Assert.Equal(work.Id, FileUtil.ReadJsonObject(Path.Combine(c, SpaceSwitcher.MarkerName))!["id"]!.ToString());
    }

    [Fact]
    public void A_clash_stops_the_switch_before_anything_moves()
    {
        var (box, c) = Seed();
        using var _box = box;
        var state = new AppState();
        var (_, personal, plan) = WorkAndPersonal(box, state);
        var spaces = new SpaceSwitcher(box.Paths);
        spaces.SetUp(state, plan);

        // A new shared skill with the same name as Personal's own one appears while Work is live.
        Write(c, "skills/drawing/SKILL.md", "work copy");

        var e = Assert.Throws<DeckException>(() => spaces.Switch(state, personal.Id));
        Assert.Contains("skills/drawing", e.Message);
        Assert.NotEqual(personal.Id, state.Spaces.ActiveId);
        Assert.True(Has(c, "projects"));                                   // nothing moved
        Assert.False(File.Exists(box.Paths.SpaceJournal));
    }

    [Fact]
    public void An_interrupted_switch_is_undone_on_the_next_start()
    {
        var (box, c) = Seed();
        using var _box = box;
        var state = new AppState();
        var (work, personal, plan) = WorkAndPersonal(box, state);
        var spaces = new SpaceSwitcher(box.Paths);
        spaces.SetUp(state, plan);

        // Simulate a crash half way: work's projects were parked, nothing came in yet.
        var from = Path.Combine(c, "projects");
        var to = SpaceItems.ParkedPath(box.Paths, work.Id, "projects");
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        Directory.Move(from, to);
        FileUtil.WriteAtomic(box.Paths.SpaceJournal, new JsonObject
        {
            ["from"] = work.Id, ["to"] = personal.Id,
            ["moves"] = new JsonArray(new JsonObject { ["from"] = from, ["to"] = to },
                                      new JsonObject { ["from"] = SpaceItems.ParkedPath(box.Paths, personal.Id, "projects"), ["to"] = from }),
        }.ToJsonString());

        var log = new List<string>();
        spaces.Reconcile(state, log.Add);

        Assert.True(Has(c, $"projects/{SpaceItems.ProjectDirName("C:\\Work\\Erp")}"));
        Assert.True(Has(SpaceItems.ParkedPath(box.Paths, personal.Id, ""), "projects"));
        Assert.Equal(work.Id, state.Spaces.ActiveId);
        Assert.False(File.Exists(box.Paths.SpaceJournal));
        Assert.Single(log);
    }

    [Fact]
    public void A_row_can_be_moved_to_the_other_side_and_back_later()
    {
        var (box, c) = Seed();
        using var _box = box;
        var state = new AppState();
        var (work, personal, plan) = WorkAndPersonal(box, state);
        var spaces = new SpaceSwitcher(box.Paths);
        spaces.SetUp(state, plan);
        var parked = SpaceItems.ParkedPath(box.Paths, personal.Id, "rules/read-first-act-later.md");

        spaces.ChangeItem(state, "rules/read-first-act-later.md", ItemChoice.Only(personal.Id));
        Assert.False(Has(c, "rules/read-first-act-later.md"));
        Assert.True(File.Exists(parked));

        spaces.ChangeItem(state, "rules/read-first-act-later.md", ItemChoice.Shared);
        Assert.True(Has(c, "rules/read-first-act-later.md"));
        Assert.False(File.Exists(parked));
        Assert.False(state.Spaces.Items.ContainsKey("rules/read-first-act-later.md"));

        // Work-only rule becomes separate: Personal gets its own copy.
        spaces.ChangeItem(state, "rules/tayqa-database.md", ItemChoice.Separate);
        Assert.True(File.Exists(SpaceItems.ParkedPath(box.Paths, personal.Id, "rules/tayqa-database.md")));
        Assert.True(Has(c, "rules/tayqa-database.md"));
        Assert.Equal(work.Id, state.Spaces.ActiveId);
    }

    [Fact]
    public void Turning_off_puts_every_conversation_back_together()
    {
        var (box, c) = Seed();
        using var _box = box;
        var state = new AppState();
        var (_, personal, plan) = WorkAndPersonal(box, state);
        var spaces = new SpaceSwitcher(box.Paths);
        spaces.SetUp(state, plan);

        var leftovers = spaces.TurnOff(state);

        Assert.False(state.Spaces.Enabled);
        Assert.True(Has(c, $"projects/{SpaceItems.ProjectDirName("D:\\GameDev\\Necro")}/s-game.jsonl"));
        Assert.True(Has(c, "file-history/s-game"));
        Assert.True(Has(c, "skills/drawing"));
        Assert.Equal(["p1", "p2", "p3"], File.ReadAllLines(Path.Combine(c, "history.jsonl")).Select(l => JsonNode.Parse(l)!["display"]!.ToString()));
        // Personal's separate copy of the settings is not merged; it is reported.
        Assert.Contains(leftovers, l => l.Contains(personal.Id));
    }

    [Fact]
    public async Task Switching_a_space_switches_both_sign_ins_and_keeps_tokens_with_their_account()
    {
        var (box, c) = Seed();
        using var _box = box;
        box.SeedLive("alice");
        ClaudeCodeTests.SignIn(box, "alice");
        var service = new DeckService(box.Paths, box.Host, new PlainProtector(), new HttpClient());
        await service.ReloadAsync();
        var aliceCode = service.State.Active(AccountKind.ClaudeCode)!;
        var aliceDesktop = service.State.Active(AccountKind.Desktop)!;
        service.State.Settings.ShareDesktopSessions = false;
        await service.AddDesktopAsync("Bob", copySettings: false);
        box.SeedLive("bob");
        ClaudeCodeTests.SignIn(box, "bob");
        await service.ReloadAsync();
        var bobCode = service.State.Active(AccountKind.ClaudeCode)!;
        var bobDesktop = service.State.Active(AccountKind.Desktop)!;
        ClaudeCodeTests.SignIn(box, "alice"); // back on Alice everywhere before setting up
        await service.SwitchAsync(aliceDesktop.Id);

        var (work, personal, plan) = WorkAndPersonal(box, service.State);
        work.DesktopProfileId = aliceDesktop.Id; work.ClaudeCodeProfileId = aliceCode.Id;
        personal.DesktopProfileId = bobDesktop.Id; personal.ClaudeCodeProfileId = bobCode.Id;
        await service.SetUpSpacesAsync(plan);

        await service.SwitchSpaceAsync(personal.Id);

        Assert.Equal("bob", box.LiveCookies);
        Assert.Equal("refresh-bob", FileUtil.ReadJsonObject(box.Paths.ClaudeCredentialsFile)!["claudeAiOauth"]!["refreshToken"]!.ToString());
        Assert.Equal("uuid-bob", FileUtil.ReadJsonObject(box.Paths.ClaudeConfigFile)!["oauthAccount"]!["accountUuid"]!.ToString());
        Assert.True(Has(c, $"projects/{SpaceItems.ProjectDirName("D:\\GameDev\\Necro")}"));

        await service.SwitchSpaceAsync(work.Id);
        await service.ReloadAsync();

        Assert.Equal("alice", box.LiveCookies);
        Assert.Equal("uuid-alice", FileUtil.ReadJsonObject(box.Paths.ClaudeConfigFile)!["oauthAccount"]!["accountUuid"]!.ToString());
        Assert.True(service.State.IsActive(aliceCode));
        Assert.Equal(2, service.State.Of(AccountKind.ClaudeCode).Count()); // no mixed-up third account
        Assert.True(Has(c, $"projects/{SpaceItems.ProjectDirName("C:\\Work\\Erp")}"));

        // Switching a single account of the other space is refused.
        await Assert.ThrowsAsync<DeckException>(() => service.SwitchAsync(bobDesktop.Id));
    }

    [Fact]
    public void A_conversation_can_be_moved_to_the_other_space_and_continued_there()
    {
        var (box, c) = Seed();
        using var _box = box;
        var workDir = SpaceItems.ProjectDirName("C:\\Work\\Erp");
        Write(c, $"projects/{workDir}/s-move.jsonl", new JsonObject { ["type"] = "user", ["cwd"] = "C:\\Work\\Erp" }.ToJsonString() + "\n");
        Write(c, $"projects/{workDir}/s-move/custom-title.json", "{\"customTitle\":\"Mixed up\"}");
        Write(c, "file-history/s-move/f@v1", "v1");
        Write(c, "todos/s-move-agent-s-move.json", "[]");
        File.AppendAllText(Path.Combine(c, "history.jsonl"),
            new JsonObject { ["display"] = "moved", ["project"] = "C:\\Work\\Erp", ["sessionId"] = "s-move" }.ToJsonString() + "\n");
        var state = new AppState();
        var (work, personal, plan) = WorkAndPersonal(box, state);
        var spaces = new SpaceSwitcher(box.Paths);
        spaces.SetUp(state, plan);
        var parked = Path.Combine(box.Paths.SpacesStore, personal.Id);

        Assert.Contains(SpaceItems.AllConversations(box.Paths, state.Spaces), x => x.Conversation.SessionId == "s-move" && x.Space == work);

        spaces.MoveConversation(state, "s-move", personal.Id);

        Assert.False(Has(c, $"projects/{workDir}/s-move.jsonl"));
        Assert.True(Has(c, $"projects/{workDir}/s-work.jsonl"));              // the rest of the project stays
        Assert.True(Has(parked, $"projects/{workDir}/s-move.jsonl"));
        Assert.True(Has(parked, $"projects/{workDir}/s-move/custom-title.json"));
        Assert.True(Has(parked, "file-history/s-move/f@v1"));
        Assert.True(Has(parked, "todos/s-move-agent-s-move.json"));
        Assert.DoesNotContain("moved", File.ReadAllText(Path.Combine(c, "history.jsonl")));
        Assert.Contains("moved", File.ReadAllText(Path.Combine(parked, "history.jsonl")));
        Assert.Equal(personal, spaces.FindConversation(state, "s-move")!.Value.Space);

        // Switching to Personal brings it in, ready to continue.
        spaces.Switch(state, personal.Id);
        Assert.True(Has(c, $"projects/{workDir}/s-move.jsonl"));
        Assert.False(Has(c, $"projects/{workDir}/s-work.jsonl"));

        // And the undo puts it all back.
        var undo = spaces.MoveConversation(state, "s-move", work.Id);
        Assert.False(Has(c, $"projects/{workDir}/s-move.jsonl"));
        undo();
        Assert.True(Has(c, $"projects/{workDir}/s-move.jsonl"));
        Assert.Contains("moved", File.ReadAllText(Path.Combine(c, "history.jsonl")));
    }

    [Fact]
    public void Moving_a_conversation_refiles_its_Claude_Desktop_sidebar_entry()
    {
        using var box = new Sandbox();
        var root = Path.Combine(box.Live, "claude-code-sessions");
        Write(root, "acc-alice/org-a/local_1.json", "{\"cliSessionId\":\"s-move\",\"title\":\"Mixed up\"}");
        Write(root, "acc-alice/org-a/local_2.json", "{\"cliSessionId\":\"s-stay\"}");
        Directory.CreateDirectory(Path.Combine(root, "acc-bob", "org-b1"));
        Directory.CreateDirectory(Path.Combine(root, "acc-bob", "org-b2"));
        var state = new AppState();
        var alice = new Profile { Kind = AccountKind.Desktop, Name = "Alice", AccountKey = "acc-alice" };
        var bob = new Profile { Kind = AccountKind.Desktop, Name = "Bob", AccountKey = "acc-bob" };
        state.Profiles.AddRange([alice, bob]);
        var desktop = new DesktopSwitcher(box.Paths, box.Host);

        var (filed, undo) = desktop.MoveSessionEntry(state, "s-move", bob);

        Assert.Equal(2, filed);
        Assert.False(File.Exists(Path.Combine(root, "acc-alice", "org-a", "local_1.json")));
        Assert.True(File.Exists(Path.Combine(root, "acc-alice", "org-a", "local_2.json")));
        Assert.Contains("Mixed up", File.ReadAllText(Path.Combine(root, "acc-bob", "org-b1", "local_1.json")));
        Assert.True(File.Exists(Path.Combine(root, "acc-bob", "org-b2", "local_1.json")));

        undo();
        Assert.True(File.Exists(Path.Combine(root, "acc-alice", "org-a", "local_1.json")));
        Assert.False(File.Exists(Path.Combine(root, "acc-bob", "org-b1", "local_1.json")));
    }

    [Fact]
    public async Task Nothing_moves_while_Claude_Code_runs_in_a_terminal()
    {
        var (box, c) = Seed();
        using var _box = box;
        box.SeedLive("alice");
        var service = new DeckService(box.Paths, box.Host, new PlainProtector(), new HttpClient());
        await service.ReloadAsync();
        var (_, personal, plan) = WorkAndPersonal(box, service.State);
        await service.SetUpSpacesAsync(plan);
        box.Host.RunningTerminals.Add("claude (4242)");

        var e = await Assert.ThrowsAsync<DeckException>(() => service.SwitchSpaceAsync(personal.Id));

        Assert.Contains("claude (4242)", e.Message);
        Assert.NotEqual(personal.Id, service.State.Spaces.ActiveId);
        Assert.True(Has(c, $"projects/{SpaceItems.ProjectDirName("C:\\Work\\Erp")}"));
    }
}
