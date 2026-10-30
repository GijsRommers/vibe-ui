using System.Text.Json;
using VibeUi.Acp;
using System.Diagnostics;
using VibeUi.Desktop.Services;
using VibeUi.Desktop.ViewModels;

namespace VibeUi.Desktop.Tests;

public sealed class ServicesTests
{
    [Fact]
    public void AppStateRoundTripsAndMigratesTheOldSingleWorkspaceSettings()
    {
        var directory = Directory.CreateTempSubdirectory("vibe-ui-appstate").FullName;
        var workspace = Directory.CreateTempSubdirectory("vibe-ui-ws").FullName;
        File.WriteAllText(Path.Combine(directory, "settings.json"), $$"""{"WorkspacePath":{{System.Text.Json.JsonSerializer.Serialize(workspace)}},"ShowThoughts":true}""");

        var migrated = AppState.Load(directory);
        Assert.Equal(workspace, Assert.Single(migrated.Projects).Path);
        Assert.True(migrated.ShowThoughts);

        migrated.Projects[0].Threads.Add(new ThreadRecord { Id = "t1", SessionId = "s1", Title = "Hello", Kind = "Orchestrator" });
        migrated.SelectedThreadId = "t1";
        migrated.ShowChanges = false;
        migrated.Save();

        var reloaded = AppState.Load(directory);
        var thread = Assert.Single(Assert.Single(reloaded.Projects).Threads);
        Assert.Equal(("t1", "s1", "Hello", "Orchestrator"), (thread.Id, thread.SessionId, thread.Title, thread.Kind));
        Assert.Equal("t1", reloaded.SelectedThreadId);
        Assert.False(reloaded.ShowChanges);
    }

    [Fact]
    public void ThreadStoreRoundTripsATranscript()
    {
        var store = new ThreadStore(Directory.CreateTempSubdirectory("vibe-ui-threads").FullName);
        var entries = new List<TranscriptEntry>
        {
            new MessageEntry(MessageRole.User, "hi"),
            new MessageEntry(MessageRole.Thought, "hmm"),
            new ToolCallEntry("c1") { Title = "bash", Kind = "execute", Status = "in_progress", Output = "ok", Locations = "/a" },
            new PlanEntryItem { Text = "☐ step" },
            new NoticeEntry("note"),
        };

        store.Save("t1", new TranscriptSnapshot(entries.Select(entry => entry.ToRecord()).ToList()));
        var restored = store.Load("t1")!.Entries.Select(TranscriptEntry.FromRecord).ToList();

        Assert.Equal(5, restored.Count);
        Assert.Equal(MessageRole.Thought, Assert.IsType<MessageEntry>(restored[1]).Role);
        var tool = Assert.IsType<ToolCallEntry>(restored[2]);
        Assert.Equal(("bash", "execute", "completed", "ok", "/a"), (tool.Title, tool.Kind, tool.Status, tool.Output, tool.Locations));
        Assert.Equal("note", Assert.IsType<NoticeEntry>(restored[4]).Text);
        store.Delete("t1");
        Assert.Null(store.Load("t1"));
    }

    [Fact]
    public void MessageBlocksSplitOnFencesWhileStreaming()
    {
        var message = new MessageEntry(MessageRole.Agent, "Intro\n```cs\nvar x");
        Assert.Equal([(false, "", "Intro"), (true, "cs", "var x")], message.Blocks.Select(block => (block.IsCode, block.Language, block.Text)));
        var codeBlock = message.Blocks[1];

        message.Append(" = 1;\n```\nOutro");

        Assert.Same(codeBlock, message.Blocks[1]); // streaming updates the open block in place
        Assert.Equal([(false, "Intro"), (true, "var x = 1;"), (false, "Outro")], message.Blocks.Select(block => (block.IsCode, block.Text)));
    }

    [Fact]
    public void DiffParsingClassifiesLines()
    {
        var lines = GitChanges.ParseDiff("diff --git a/f b/f\nindex 1..2 100644\n--- a/f\n+++ b/f\n@@ -1,2 +1,2 @@\n same\n-old\n+new\n");

        Assert.Equal(
        [
            (DiffLineKind.Meta, "diff --git a/f b/f"), (DiffLineKind.Meta, "index 1..2 100644"), (DiffLineKind.Meta, "--- a/f"), (DiffLineKind.Meta, "+++ b/f"),
            (DiffLineKind.Hunk, "@@ -1,2 +1,2 @@"), (DiffLineKind.Context, "same"), (DiffLineKind.Removed, "old"), (DiffLineKind.Added, "new"),
        ], lines.Select(line => (line.Kind, line.Text)));
    }

    [Fact]
    public async Task GitChangesReadsTheWorkingTreeAndWorktreesCanBeCreated()
    {
        Assert.SkipWhen(!GitAvailable(), "git is not installed.");
        var repo = Directory.CreateTempSubdirectory("vibe-ui-repo").FullName;
        Git(repo, "init", "-q", "-b", "main");
        Git(repo, "-c", "user.email=t@example.com", "-c", "user.name=T", "commit", "-q", "--allow-empty", "-m", "init");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "one\ntwo\n");
        Git(repo, "add", "a.txt");
        Git(repo, "-c", "user.email=t@example.com", "-c", "user.name=T", "commit", "-q", "-m", "a");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "one\nthree\nfour\n");
        File.WriteAllText(Path.Combine(repo, "new.txt"), "hello\n");

        var snapshot = await GitChanges.LoadAsync(repo);

        Assert.True(snapshot.IsRepository);
        Assert.Equal("main", snapshot.Branch);
        Assert.Equal([("a.txt", "M", 2, 1), ("new.txt", "?", 1, 0)], snapshot.Files.Select(file => (file.Path, file.Status, file.Added, file.Removed)));
        var diff = await GitChanges.DiffAsync(repo, snapshot.Files[0]);
        Assert.Contains(diff, line => line is { Kind: DiffLineKind.Added, Text: "three" });
        var untracked = await GitChanges.DiffAsync(repo, snapshot.Files[1]);
        Assert.Contains(untracked, line => line is { Kind: DiffLineKind.Added, Text: "hello" });

        var root = Directory.CreateTempSubdirectory("vibe-ui-worktrees").FullName;
        var worktree = await GitWorktrees.CreateAsync(repo, root, "Write tests!");
        Assert.Equal("vibe/write-tests", worktree.Branch);
        Assert.True(File.Exists(Path.Combine(worktree.Path, "a.txt")));
        var second = await GitWorktrees.CreateAsync(repo, root, "Write tests!");
        Assert.Equal("vibe/write-tests-2", second.Branch);
        Assert.True(await GitWorktrees.RemoveAsync(repo, worktree.Path));
        Assert.False(Directory.Exists(worktree.Path));

        var notRepo = await GitChanges.LoadAsync(Directory.CreateTempSubdirectory("vibe-ui-plain").FullName);
        Assert.False(notRepo.IsRepository);
    }

    private static bool GitAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", "--version") { RedirectStandardOutput = true, UseShellExecute = false });
            process!.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static void Git(string directory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git") { WorkingDirectory = directory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}

public sealed class GitActionsTests
{
    [Fact]
    public async Task CommitAndPushWorkAgainstALocalRemoteAndPullRequestsFailGracefully()
    {
        Assert.SkipWhen(!GitAvailable(), "git is not installed.");
        var remote = Directory.CreateTempSubdirectory("vibe-ui-remote").FullName;
        Git(remote, "init", "-q", "--bare", "-b", "main");
        var repo = Directory.CreateTempSubdirectory("vibe-ui-local").FullName;
        Git(repo, "init", "-q", "-b", "main");
        Git(repo, "remote", "add", "origin", remote);
        Git(repo, "-c", "user.email=t@example.com", "-c", "user.name=T", "commit", "-q", "--allow-empty", "-m", "init");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "hello\n");

        Assert.False((await GitActions.CommitAsync(repo, "   ")).Ok);
        var commit = await GitActions.CommitAsync(repo, "add a");
        Assert.True(commit.Ok, commit.Message);
        Assert.StartsWith("Committed ", commit.Message);
        Assert.Empty((await GitChanges.LoadAsync(repo)).Files);
        Assert.Null(await GitActions.UpstreamStatusAsync(repo));

        var push = await GitActions.PushAsync(repo);
        Assert.True(push.Ok, push.Message);
        Assert.Equal("in sync with upstream", await GitActions.UpstreamStatusAsync(repo));

        var (pulls, error) = await GitActions.ListPullRequestsAsync(repo);
        Assert.Empty(pulls);
        Assert.NotNull(error); // No GitHub remote (or no gh): the pane shows why instead of failing.
    }

    private static bool GitAvailable()
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", "--version") { RedirectStandardOutput = true, UseShellExecute = false });
            process!.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static void Git(string directory, params string[] arguments)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = directory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(startInfo)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}

public sealed class VibeUpdaterTests
{
    [Theory]
    [InlineData("2.25.0", "3.0.4", true)]
    [InlineData("3.0.4", "3.0.4", false)]
    [InlineData("3.1.0", "3.0.4", false)]
    [InlineData("2.25.0", "2.25.1rc1", true)]
    [InlineData(null, "3.0.4", false)]
    [InlineData("2.25.0", "", false)]
    public void IsNewerComparesReleases(string? installed, string latest, bool expected) => Assert.Equal(expected, VibeUpdater.IsNewer(installed, latest));

    [Fact]
    public void PlanPrefersUvWhenUvInstalledVibe()
    {
        var home = Directory.CreateTempSubdirectory("vibe-ui-home").FullName;
        var plan = VibeUpdater.Plan(home);
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The script fallback is for macOS and Linux.");
        Assert.Equal(VibeAcpLauncher.InstallCommand, plan!.Description); // no uv receipt under this home: install script
    }
}

public sealed class ProjectActionsTests
{
    [Fact]
    public async Task ActionsLoadFromVibeReAndRunThroughTheShell()
    {
        var project = Directory.CreateTempSubdirectory("vibe-ui-actions").FullName;
        File.WriteAllText(Path.Combine(project, "vibe.re"), """
            {
              // comments and trailing commas are fine
              "actions": [
                { "name": "hello", "command": "echo hi there && echo done >&2", "description": "Says hi" },
                { "name": "setup", "command": "touch setup-ran", "runOnWorktreeCreate": true, "tool": false },
                { "name": "", "command": "ignored" },
              ]
            }
            """);

        var actions = ProjectActions.Load(project);
        Assert.Equal(["hello", "setup"], actions.Select(action => action.Name));
        Assert.True(actions[1].RunOnWorktreeCreate);
        Assert.False(actions[1].Tool);
        Assert.Empty(ProjectActions.Load(Directory.CreateTempSubdirectory("vibe-ui-noactions").FullName));

        Assert.SkipWhen(OperatingSystem.IsWindows(), "Shell commands here are POSIX.");
        var lines = new System.Collections.Concurrent.ConcurrentBag<string>(); // stdout and stderr report from different threads
        var outcome = await ProjectActions.RunAsync(actions[0], project, lines.Add, CancellationToken.None);
        Assert.True(outcome.Ok, outcome.Output);
        Assert.Contains("hi there", outcome.Output);
        Assert.Contains("done", outcome.Output);
        Assert.Contains("hi there", lines);

        var failing = await ProjectActions.RunAsync(new ProjectAction("boom", "exit 3"), project, null, CancellationToken.None);
        Assert.Equal(3, failing.ExitCode);
        Assert.False(failing.Ok);
    }
}

public sealed class CodeSearchTests
{
    [Fact]
    public async Task SearchRanksFilesByIdentifierTokens()
    {
        var root = Directory.CreateTempSubdirectory("vibe-ui-search").FullName;
        Directory.CreateDirectory(Path.Combine(root, "src"));
        Directory.CreateDirectory(Path.Combine(root, "node_modules", "junk"));
        File.WriteAllText(Path.Combine(root, "src", "TrustService.cs"), "class TrustService {\n  void DecideWorkspaceTrust(string cwd) { }\n  void ApplyConfigOptions() { }\n}\n");
        File.WriteAllText(Path.Combine(root, "src", "Other.cs"), "class Other { void Nothing() { } }\n");
        File.WriteAllText(Path.Combine(root, "node_modules", "junk", "trust.js"), "workspace trust decision everywhere trust trust");
        File.WriteAllBytes(Path.Combine(root, "src", "blob.bin"), [0, 1, 2, 0, 0]);

        var hits = await CodeSearch.SearchAsync(root, "workspace trust decision");

        var top = Assert.Single(hits);
        Assert.Equal("src/TrustService.cs", top.Path);
        Assert.Contains(top.Lines, line => line.Number == 2 && line.Text.Contains("DecideWorkspaceTrust"));
        Assert.Equal(["applyconfigoptions", "apply", "config", "options"], CodeSearch.Tokenize("ApplyConfigOptions"));
        Assert.Equal(["snake_case", "snake", "case"], CodeSearch.Tokenize("snake_case"));
        CodeSearch.Forget(root);
    }
}

public sealed class MarkdownInlinesTests
{
    [Fact]
    public void InlineSpansBecomeStyledRuns()
    {
        var runs = VibeUi.Desktop.Views.MarkdownInlines.Parse("Use **bold**, `code` and [a link](https://x.test) here.").ToList();
        Assert.Equal(["Use ", "bold", ", ", "code", " and ", "a link", " here."], runs.Select(run => run.Text));
        Assert.Equal(Avalonia.Media.FontWeight.SemiBold, runs[1].FontWeight);
        Assert.NotNull(runs[3].Background);
        Assert.NotNull(runs[5].TextDecorations);
    }

    [Fact]
    public void ToolTitlesDropMcpPrefixesAndShortenHome()
    {
        var entry = new ToolCallEntry("c");
        entry.Apply(new VibeUi.Acp.ToolCallUpdate("c", "mcp_vibe_ui_orchestrator.open_in_browser"));
        Assert.Equal("open_in_browser", entry.DisplayTitle);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        entry.Apply(new VibeUi.Acp.ToolCallUpdate("c", $"Reading {home}/notes.txt"));
        Assert.Equal("Reading ~/notes.txt", entry.DisplayTitle);
    }
}

public sealed class PullRequestTests
{
    [Fact]
    public void ChecksAndSummaryComeFromTheRollup()
    {
        static JsonElement Check(string json) => JsonSerializer.Deserialize<JsonElement>(json);
        var passing = new PullRequest(12, "Fix", "OPEN", "https://x/12", "vibe/fix", false, DateTimeOffset.UtcNow, "APPROVED",
            [Check("""{"status":"COMPLETED","conclusion":"SUCCESS"}"""), Check("""{"state":"SUCCESS"}""")]);
        Assert.Equal("passing", passing.Checks);
        Assert.Equal("#12 · checks passing · approved", passing.Summary);

        var failing = passing with { StatusCheckRollup = [Check("""{"status":"COMPLETED","conclusion":"FAILURE"}"""), Check("""{"status":"IN_PROGRESS"}""")], ReviewDecision = null, IsDraft = true };
        Assert.Equal("failing", failing.Checks);
        Assert.Equal("#12 · draft · checks failing", failing.Summary);

        var pending = passing with { StatusCheckRollup = [Check("""{"status":"QUEUED"}""")], ReviewDecision = "CHANGES_REQUESTED" };
        Assert.Equal("pending", pending.Checks);
        Assert.Contains("changes requested", pending.Summary);

        Assert.Equal("", (passing with { StatusCheckRollup = null }).Checks);
        Assert.Equal("#12", (passing with { StatusCheckRollup = null, ReviewDecision = null }).Summary);
    }
}
