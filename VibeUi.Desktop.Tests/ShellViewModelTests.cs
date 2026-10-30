using System.Text.Json.Nodes;
using VibeUi.Acp;
using VibeUi.Desktop.Services;
using VibeUi.Desktop.ViewModels;

namespace VibeUi.Desktop.Tests;

public sealed class ShellViewModelTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>One fake agent per project, like the real launcher; configure the next one before it starts.</summary>
    private sealed class Harness
    {
        public string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("vibe-ui-state").FullName;
        public string Workspace { get; } = System.IO.Directory.CreateTempSubdirectory("vibe-ui-project").FullName;
        public List<FakeAcpClient> Clients { get; } = [];
        public List<string> OpenedUrls { get; } = [];
        public Action<FakeAcpClient>? Configure { get; set; }
        public string? Executable { get; set; } = "/usr/local/bin/vibe-acp";

        public FakeAcpClient Client => Clients[^1];

        public ShellViewModel Build(Action<AppState>? state = null)
        {
            var appState = AppState.Load(Directory);
            state?.Invoke(appState);
            return new ShellViewModel(appState, new ThreadStore(Directory),
                locateAgent: () => Executable,
                startAgent: (_, cwd, handler, _) =>
                {
                    var client = new FakeAcpClient { Handler = handler, Workspace = cwd };
                    Configure?.Invoke(client);
                    Clients.Add(client);
                    return client;
                },
                openUrl: OpenedUrls.Add,
                dispatch: action => action(),
                clientVersion: "0.1.0-test",
                latestVibeVersion: _ => Task.FromResult<string?>(null));
        }
    }

    [Fact]
    public async Task AddingAProjectCreatesAThreadAndOpensASession()
    {
        var harness = new Harness();
        var shell = harness.Build();
        Assert.True(shell.ShowGate);
        Assert.Same(shell.Chief, shell.SelectedThread);

        var project = await shell.AddProjectAsync(harness.Workspace);

        Assert.NotNull(project);
        Assert.False(shell.ShowEmptyState);
        Assert.Same(project, shell.SelectedProject);
        var thread = Assert.IsType<ThreadViewModel>(shell.SelectedThread);
        Assert.True(thread.IsSelected);
        Assert.Equal(ThreadStatus.Ready, thread.State);
        Assert.True(shell.ShowThread);
        Assert.Equal(harness.Workspace, harness.Client.Workspace);
        Assert.Equal("Mistral Vibe 2.25.0", project!.Connection.AgentDescription);
        Assert.Equal(["ask", "accept-edits", "smart-approve", "auto-approve"], thread.Modes.Select(mode => mode.Id));
        Assert.Equal("smart-approve", thread.SelectedMode?.Id); // Vibe UI's default; Vibe itself starts in accept-edits
        Assert.Contains("session/set_mode:smart-approve", harness.Client.Calls);
        Assert.Equal("mistral-medium-3.5", thread.SelectedModel?.Value);
        Assert.Equal("medium", thread.SelectedThinking?.Value);
        Assert.Empty(harness.Client.McpServersSeen.Single());
    }

    [Fact]
    public async Task MissingAgentShowsInstallGuidance()
    {
        var harness = new Harness { Executable = null };
        var shell = harness.Build();

        await shell.AddProjectAsync(harness.Workspace);

        Assert.True(shell.ShowGate);
        Assert.True(shell.ShowInstall);
        Assert.False(shell.ShowThread);
        Assert.Equal(ConnectionState.NotInstalled, shell.ActiveConnection!.State);
        Assert.Contains("install.sh", shell.InstallCommand);
    }

    [Fact]
    public async Task SignInUsesTheDelegatedBrowserFlowAndThenOpensTheSession()
    {
        var harness = new Harness
        {
            Configure = client =>
            {
                client.RequireAuthentication = true;
                client.SignInCompletion = new TaskCompletionSource();
            },
        };
        var shell = harness.Build();
        await shell.AddProjectAsync(harness.Workspace);
        Assert.True(shell.ShowSignIn);
        Assert.Equal(ThreadStatus.New, shell.SelectedThread!.State);

        var signIn = shell.SignInCommand.ExecuteAsync(null);
        Assert.Equal(ConnectionState.SigningIn, shell.ActiveConnection!.State);
        Assert.Equal(["https://auth.example.test/login"], harness.OpenedUrls);
        harness.Client.SignInCompletion!.SetResult();
        await signIn.WaitAsync(Timeout);
        await WaitUntilAsync(() => shell.SelectedThread.State == ThreadStatus.Ready);

        Assert.True(shell.ShowThread);
        Assert.Equal(["initialize", $"session/new:{harness.Workspace}", "authenticate:browser-auth-delegated:start", "authenticate:browser-auth-delegated:complete", $"session/new:{harness.Workspace}", "session/set_mode:smart-approve"],
            harness.Client.Calls);
    }

    [Fact]
    public async Task SendingAPromptStreamsTheTranscriptAnswersPermissionsAndPersists()
    {
        var harness = new Harness
        {
            Configure = client => client.PromptScript = async (fake, sessionId) =>
            {
                await fake.SendUpdateAsync("""{"sessionUpdate":"agent_thought_chunk","content":{"type":"text","text":"Let me look."},"messageId":"t1"}""", sessionId);
                await fake.SendUpdateAsync("""{"sessionUpdate":"tool_call","toolCallId":"c1","title":"read_file","kind":"read","status":"in_progress"}""", sessionId);
                var permission = fake.Handler.RequestPermissionAsync(new RequestPermissionRequest(sessionId, new ToolCallUpdate("c1", "Reading note.txt"),
                    [new PermissionOption("allow_once", "Allow once", "allow_once"), new PermissionOption("reject_once", "Deny", "reject_once")]), CancellationToken.None);
                await Task.Yield();
                var answer = await permission;
                Assert.Equal("allow_once", answer.Outcome.OptionId);
                await fake.SendUpdateAsync("""{"sessionUpdate":"tool_call_update","toolCallId":"c1","status":"completed","content":[{"type":"content","content":{"type":"text","text":"hello world"}}],"locations":[{"path":"/w/note.txt"}]}""", sessionId);
                await fake.SendUpdateAsync("""{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"It says:\n```text\nhello"},"messageId":"m1"}""", sessionId);
                await fake.SendUpdateAsync("""{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":" world\n```\nDone."},"messageId":"m1"}""", sessionId);
                await fake.SendUpdateAsync("""{"sessionUpdate":"usage_update","used":1200,"size":200000,"cost":{"amount":0.0012,"currency":"USD"}}""", sessionId);
                return new PromptResponse(StopReasons.EndTurn);
            },
        };
        var shell = harness.Build();
        await shell.AddProjectAsync(harness.Workspace);
        var thread = shell.SelectedThread!;
        thread.PromptText = "read it";
        Assert.True(thread.SendCommand.CanExecute(null));

        var send = thread.SendCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => thread.PendingPermission is not null);
        Assert.Equal(ThreadStatus.Working, thread.State);
        Assert.True(thread.NeedsAttention);
        Assert.Equal("Reading note.txt", thread.PendingPermission!.Title);
        thread.PendingPermission.Options.Single(option => option.IsAllow).ChooseCommand.Execute(null);
        await send.WaitAsync(Timeout);

        Assert.Equal(ThreadStatus.Ready, thread.State);
        Assert.Null(thread.PendingPermission);
        Assert.Equal("read it", thread.Title);
        var entries = thread.Transcript.ToList();
        Assert.Equal(4, entries.Count);
        Assert.Equal((MessageRole.User, "read it"), (((MessageEntry)entries[0]).Role, ((MessageEntry)entries[0]).Text));
        var thought = Assert.IsType<MessageEntry>(entries[1]);
        Assert.True(thought.IsHidden);
        var tool = Assert.IsType<ToolCallEntry>(entries[2]);
        Assert.Equal(("Reading note.txt", "completed", "hello world", "/w/note.txt"), (tool.Title, tool.Status, tool.Output, tool.Locations));
        var reply = Assert.IsType<MessageEntry>(entries[3]);
        Assert.Equal("It says:\n```text\nhello world\n```\nDone.", reply.Text);
        Assert.Equal([(false, "It says:"), (true, "hello world"), (false, "Done.")], reply.Blocks.Select(block => (block.IsCode, block.Text)));
        Assert.Equal("text", reply.Blocks[1].Language);
        Assert.Equal("1.2k of 200k context", thread.UsageText);
        Assert.Equal("", thread.Status);
        Assert.Equal("Done.", thread.LastReply[^5..]);

        shell.ShowThoughts = true;
        Assert.False(thought.IsHidden);

        // Everything needed to come back after a restart is on disk.
        var saved = AppState.Load(harness.Directory);
        var record = Assert.Single(Assert.Single(saved.Projects).Threads);
        Assert.Equal(("read it", "s-1", thread.Id), (record.Title, record.SessionId, saved.SelectedThreadId));
        var snapshot = new ThreadStore(harness.Directory).Load(thread.Id);
        Assert.Equal(["message", "message", "tool", "message"], snapshot!.Entries.Select(entry => entry.Type));
    }

    [Fact]
    public async Task RestartResumesTheSavedSessionAndReplaysItsHistory()
    {
        var harness = new Harness();
        var shell = harness.Build();
        await shell.AddProjectAsync(harness.Workspace);
        var thread = shell.SelectedThread!;
        await thread.SendAsync("hello");
        await shell.ShutdownAsync();

        harness.Configure = client =>
        {
            client.LoadableSessions.Add("s-1");
            client.ReplayOnLoad.Add("""{"sessionUpdate":"session_info_update","title":"Greeting"}""");
            client.ReplayOnLoad.Add("""{"sessionUpdate":"user_message_chunk","content":{"type":"text","text":"hello"}}""");
            client.ReplayOnLoad.Add("""{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"hi there"}}""");
        };
        var restarted = harness.Build();
        await restarted.InitializeAsync();

        var resumed = restarted.SelectedThread!;
        Assert.Equal(thread.Id, resumed.Id);
        Assert.Equal(ThreadStatus.Ready, resumed.State);
        Assert.Equal("Greeting", resumed.Title);
        Assert.Contains($"session/load:s-1:{harness.Workspace}", harness.Client.Calls);
        Assert.Equal([(MessageRole.User, "hello"), (MessageRole.Agent, "hi there")],
            resumed.Transcript.OfType<MessageEntry>().Select(entry => (entry.Role, entry.Text)));
    }

    [Fact]
    public async Task WhenVibeCannotResumeTheThreadKeepsItsSavedCopyAndStartsFresh()
    {
        var harness = new Harness();
        var shell = harness.Build();
        await shell.AddProjectAsync(harness.Workspace);
        await shell.SelectedThread!.SendAsync("hello");
        await shell.ShutdownAsync();

        var restarted = harness.Build();
        await restarted.InitializeAsync();

        var thread = restarted.SelectedThread!;
        Assert.Equal(ThreadStatus.Ready, thread.State);
        Assert.Contains("session/load:s-1", harness.Client.Calls[1]);
        Assert.Equal($"session/new:{harness.Workspace}", harness.Client.Calls[2]);
        Assert.Equal("hello", Assert.IsType<MessageEntry>(thread.Transcript[0]).Text);
        Assert.Contains("could not resume", Assert.IsType<NoticeEntry>(thread.Transcript[^1]).Text);
        Assert.Equal("s-1", thread.Record.SessionId); // The fake hands out s-1 again for the first session.
    }

    [Fact]
    public async Task ThreadsInOneProjectShareTheAgentAndKeepSeparateTranscripts()
    {
        var harness = new Harness();
        var shell = harness.Build();
        await shell.AddProjectAsync(harness.Workspace);
        var first = shell.SelectedThread!;
        await shell.NewThreadAsync(shell.SelectedProject);
        var second = shell.SelectedThread!;

        Assert.NotSame(first, second);
        Assert.Single(harness.Clients);
        Assert.Equal(2, shell.SelectedProject!.Threads.Count);
        await harness.Client.SendUpdateAsync("""{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"to first"}}""", "s-1");
        await harness.Client.SendUpdateAsync("""{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"to second"}}""", "s-1-2");

        Assert.Equal("to first", Assert.IsType<MessageEntry>(Assert.Single(first.Transcript)).Text);
        Assert.Equal("to second", Assert.IsType<MessageEntry>(Assert.Single(second.Transcript)).Text);
        Assert.False(first.IsSelected);
        Assert.True(second.IsSelected);
    }

    [Fact]
    public async Task EachProjectGetsItsOwnAgentProcess()
    {
        var harness = new Harness();
        var shell = harness.Build();
        await shell.AddProjectAsync(harness.Workspace);
        var other = Directory.CreateTempSubdirectory("vibe-ui-other").FullName;

        await shell.AddProjectAsync(other);

        Assert.Equal(2, harness.Clients.Count);
        Assert.Equal([harness.Workspace, other], harness.Clients.Select(client => client.Workspace));
        Assert.Equal(other, shell.SelectedProject!.Path);
    }

    [Fact]
    public async Task DeletingAThreadRemovesItEverywhere()
    {
        var harness = new Harness();
        var shell = harness.Build();
        await shell.AddProjectAsync(harness.Workspace);
        var doomed = shell.SelectedThread!;
        await doomed.SendAsync("hello");
        await shell.NewThreadAsync(shell.SelectedProject);

        await shell.DeleteThreadAsync(doomed);

        Assert.DoesNotContain(doomed, shell.SelectedProject!.Threads);
        Assert.Contains("ext:_session/delete", harness.Client.Calls);
        Assert.Null(new ThreadStore(harness.Directory).Load(doomed.Id));
        Assert.NotSame(doomed, shell.SelectedThread);
    }

    [Fact]
    public async Task StopSendsCancelAndCancelsAPendingPermission()
    {
        var finish = new TaskCompletionSource<PromptResponse>();
        var harness = new Harness
        {
            Configure = client => client.PromptScript = async (fake, sessionId) =>
            {
                await fake.SendUpdateAsync("""{"sessionUpdate":"tool_call","toolCallId":"c1","title":"bash","kind":"execute","status":"in_progress"}""", sessionId);
                var answer = await fake.Handler.RequestPermissionAsync(new RequestPermissionRequest(sessionId, new ToolCallUpdate("c1"),
                    [new PermissionOption("allow_once", "Allow once", "allow_once")]), CancellationToken.None);
                Assert.Equal("cancelled", answer.Outcome.Outcome);
                return await finish.Task;
            },
        };
        var shell = harness.Build();
        await shell.AddProjectAsync(harness.Workspace);
        var thread = shell.SelectedThread!;
        var send = thread.SendAsync("run it");
        await WaitUntilAsync(() => thread.PendingPermission is not null);

        await thread.StopCommand.ExecuteAsync(null);
        finish.SetResult(new PromptResponse(StopReasons.Cancelled));
        var result = await send.WaitAsync(Timeout);

        Assert.Contains("session/cancel", harness.Client.Calls);
        Assert.Equal(StopReasons.Cancelled, result.StopReason);
        Assert.Null(thread.PendingPermission);
        Assert.Equal(ThreadStatus.Ready, thread.State);
    }

    [Fact]
    public async Task ChangingModeModelAndThinkingTalksToTheAgent()
    {
        var harness = new Harness();
        var shell = harness.Build();
        await shell.AddProjectAsync(harness.Workspace);
        var thread = shell.SelectedThread!;

        thread.SelectedMode = thread.Modes.Single(mode => mode.Id == "ask");
        thread.SelectedModel = thread.Models.Single(choice => choice.Value == "devstral-small");
        thread.SelectedThinking = thread.ThinkingLevels.Single(choice => choice.Value == "high");
        await WaitUntilAsync(() => harness.Client.Calls.Contains("session/set_config_option:thinking=high"));

        Assert.Contains("session/set_mode:ask", harness.Client.Calls);
        Assert.Contains("session/set_config_option:model=devstral-small", harness.Client.Calls);
        Assert.Equal(("ask", "devstral-small"), (thread.Record.ModeId, thread.Record.ModelId));
    }

    [Fact]
    public async Task AgentExitShowsTheConnectPanelAndReconnectReopensTheThread()
    {
        var harness = new Harness();
        var shell = harness.Build();
        await shell.AddProjectAsync(harness.Workspace);
        var thread = shell.SelectedThread!;

        harness.Client.SimulateExit();
        await WaitUntilAsync(() => shell.ActiveConnection!.State == ConnectionState.Disconnected);
        Assert.True(shell.ShowGate);
        Assert.True(shell.ShowConnect);
        Assert.Equal(ThreadStatus.New, thread.State);

        await shell.ReconnectCommand.ExecuteAsync(null);

        Assert.Equal(2, harness.Clients.Count);
        Assert.Equal(ThreadStatus.Ready, thread.State);
        Assert.True(shell.ShowThread);
    }

    [Fact]
    public async Task UntrustedWorkspaceCanBeTrusted()
    {
        var harness = new Harness { Configure = client => client.TrustStatus = "untrusted" };
        var shell = harness.Build();
        await shell.AddProjectAsync(harness.Workspace);
        var thread = shell.SelectedThread!;
        Assert.True(thread.IsWorkspaceUntrusted);

        await thread.TrustWorkspaceCommand.ExecuteAsync(null);

        Assert.False(thread.IsWorkspaceUntrusted);
        Assert.Contains($"ext:{AgentConnection.TrustDecideMethod}", harness.Client.Calls);
    }

    [Fact]
    public async Task SignOutReturnsToTheSignInScreen()
    {
        var harness = new Harness();
        var shell = harness.Build();
        await shell.AddProjectAsync(harness.Workspace);

        await shell.SignOutCommand.ExecuteAsync(null);

        Assert.True(shell.ShowSignIn);
        Assert.Contains($"ext:{AgentConnection.SignOutMethod}", harness.Client.Calls);
        Assert.Equal(ThreadStatus.New, shell.SelectedThread!.State);
    }

    [Fact]
    public async Task RenamingAThreadTellsVibe()
    {
        var harness = new Harness();
        var shell = harness.Build();
        await shell.AddProjectAsync(harness.Workspace);

        await shell.SelectedThread!.RenameAsync("  Payments refactor ");

        Assert.Equal("Payments refactor", shell.SelectedThread.Title);
        Assert.Contains($"ext:{AgentConnection.SetTitleMethod}", harness.Client.Calls);
        Assert.Equal("Payments refactor", AppState.Load(harness.Directory).Projects[0].Threads[0].Title);
    }

    [Fact]
    public async Task OrchestratorDelegatesWorkToChildThreadsThroughTheMcpServer()
    {
        var harness = new Harness
        {
            Configure = client => client.PromptScript = async (fake, sessionId) =>
            {
                await fake.SendUpdateAsync($$$"""{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"done in {{{sessionId}}}"}}""", sessionId);
                return new PromptResponse(StopReasons.EndTurn);
            },
        };
        var shell = harness.Build();
        await shell.AddProjectAsync(harness.Workspace);
        await shell.NewOrchestratorAsync(shell.SelectedProject);
        var orchestrator = shell.SelectedThread!;
        Assert.True(orchestrator.IsOrchestrator);
        Assert.Equal(ThreadStatus.Ready, orchestrator.State);

        // The orchestrator's session was created with the MCP server; the plain thread's was not.
        var server = Assert.Single(harness.Client.McpServersSeen[1]);
        var json = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(server))!;
        Assert.Equal("http", json["type"]!.GetValue<string>());
        Assert.Equal(OrchestratorServer.ServerName, json["name"]!.GetValue<string>());
        var url = new Uri(json["url"]!.GetValue<string>());
        Assert.Equal("127.0.0.1", url.Host);
        Assert.EndsWith("/mcp/" + orchestrator.Id, url.AbsolutePath);

        // The first prompt carries the hidden orchestration preamble.
        await orchestrator.SendAsync("Build the feature");
        Assert.Contains(harness.Client.Calls, call => call.StartsWith("session/prompt:" + ShellViewModel.OrchestratorPreamble[..40]));
        Assert.Equal("Build the feature", Assert.IsType<MessageEntry>(orchestrator.Transcript[0]).Text);

        // Speak MCP to the server over HTTP, as Vibe would.
        using var http = new HttpClient();
        var initialized = await PostAsync(http, url, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"test","version":"0"}}}""");
        Assert.Equal("2025-11-25", initialized!["result"]!["protocolVersion"]!.GetValue<string>());
        var tools = await PostAsync(http, url, """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        Assert.Contains("delegate_task", tools!["result"]!["tools"]!.AsArray().Select(tool => tool!["name"]!.GetValue<string>()));

        var delegated = await PostAsync(http, url, """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"delegate_task","arguments":{"title":"Write tests","prompt":"Add tests for the parser."}}}""");
        var task = JsonNode.Parse(delegated!["result"]!["content"]![0]!["text"]!.GetValue<string>())!;
        var taskId = task["taskId"]!.GetValue<string>();
        var child = shell.SelectedProject!.Threads.Single(thread => thread.Id == taskId);
        Assert.True(child.IsTask);
        Assert.Equal(orchestrator.Id, child.ParentId);
        Assert.Equal("Write tests", child.Title);
        Assert.Equal(3, shell.SelectedProject.Threads.Count);
        Assert.Equal(child, shell.SelectedProject.Threads[2]); // nested right after its orchestrator

        var waited = await PostAsync(http, url, """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"wait_for_task","arguments":{"task_id":"TASK","timeout_seconds":5}}}""".Replace("TASK", taskId));
        var outcome = JsonNode.Parse(waited!["result"]!["content"]![0]!["text"]!.GetValue<string>())!;
        Assert.Equal("done", outcome["status"]!.GetValue<string>());
        Assert.Equal("done in s-1-3", outcome["reply"]!.GetValue<string>());
        Assert.Equal("Add tests for the parser.", Assert.IsType<MessageEntry>(child.Transcript[0]).Text);

        var listed = await PostAsync(http, url, """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"list_tasks","arguments":{}}}""");
        Assert.Single(JsonNode.Parse(listed!["result"]!["content"]![0]!["text"]!.GetValue<string>())!.AsArray());

        var unknown = await PostAsync(http, url, """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"check_task","arguments":{"task_id":"nope"}}}""");
        Assert.NotNull(unknown!["error"]);

        // Deleting the orchestrator takes its tasks with it.
        await shell.DeleteThreadAsync(orchestrator);
        Assert.Single(shell.SelectedProject.Threads);
        await shell.ShutdownAsync();
    }

    private static async Task<JsonNode?> PostAsync(HttpClient http, Uri url, string body)
    {
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(url, content);
        Assert.True(response.IsSuccessStatusCode, response.StatusCode.ToString());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            await Task.Delay(10);
        }
    }
}

public sealed class ChiefOfStaffTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private sealed class Harness
    {
        public string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("vibe-ui-chief").FullName;
        public string Workspace { get; } = System.IO.Directory.CreateTempSubdirectory("vibe-ui-chief-project").FullName;
        public List<FakeAcpClient> Clients { get; } = [];
        public Action<FakeAcpClient>? Configure { get; set; }

        public ShellViewModel Build() => new(AppState.Load(Directory), new ThreadStore(Directory),
            () => "/usr/local/bin/vibe-acp",
            (_, cwd, handler, _) =>
            {
                var client = new FakeAcpClient { Handler = handler, Workspace = cwd };
                Configure?.Invoke(client);
                Clients.Add(client);
                return client;
            },
            _ => { },
            action => action(),
            "0.1.0-test",
            latestVibeVersion: _ => Task.FromResult<string?>(null));
    }

    [Fact]
    public async Task ChiefIsPinnedOutsideTheProjectsAndRunsInItsOwnWorkspace()
    {
        var harness = new Harness();
        var shell = harness.Build();
        Assert.True(shell.Chief.IsChief);
        Assert.True(shell.Chief.IsOrchestrator);
        Assert.Equal("Chief of Staff", shell.Chief.Title);
        Assert.DoesNotContain(shell.Projects, project => project.IsChief);

        await shell.SelectThreadAsync(shell.Chief);

        Assert.Equal(ThreadStatus.Ready, shell.Chief.State);
        Assert.Equal(Path.Combine(harness.Directory, "chief-of-staff"), harness.Clients.Single().Workspace);
        Assert.Single(harness.Clients.Single().McpServersSeen.Single());
        Assert.False(shell.ShowChangesPane);
        Assert.True(shell.ShowThread);

        await shell.ShutdownAsync();
        var restarted = harness.Build();
        Assert.Same(restarted.Chief, restarted.SelectedThread);
        Assert.Equal("s-1", restarted.Chief.Record.SessionId);
    }

    [Fact]
    public async Task ChiefDelegatesIntoANamedProjectAndGetsBriefedWhenTheTaskFinishes()
    {
        var release = new TaskCompletionSource();
        var harness = new Harness
        {
            Configure = client => client.PromptScript = async (fake, sessionId) =>
            {
                if (fake.Workspace.EndsWith("chief-of-staff", StringComparison.Ordinal))
                {
                    await fake.SendUpdateAsync("""{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Noted."}}""", sessionId);
                    return new PromptResponse(StopReasons.EndTurn);
                }
                await fake.SendUpdateAsync("""{"sessionUpdate":"tool_call","toolCallId":"c1","title":"edit parser.cs","kind":"edit","status":"completed","locations":[{"path":"/p/parser.cs"}]}""", sessionId);
                await fake.SendUpdateAsync("""{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Parser fixed."}}""", sessionId);
                await release.Task;
                return new PromptResponse(StopReasons.EndTurn);
            },
        };
        var shell = harness.Build();
        shell.BriefingInterval = TimeSpan.FromMilliseconds(50);
        await shell.AddProjectAsync(harness.Workspace);
        var projectName = shell.Projects.Single().Name;
        await shell.SelectThreadAsync(shell.Chief);
        await shell.Chief.SendAsync("Fix the parser in " + projectName);
        Assert.Contains(harness.Clients.Last().Calls, call => call.StartsWith("session/prompt:" + ShellViewModel.ChiefPreamble[..30]) && call.Contains(projectName));

        // A wrong project name is refused, and the error lists the projects that exist.
        var wrong = await Assert.ThrowsAsync<InvalidOperationException>(() => shell.DelegateAsync(ShellViewModel.ChiefId, "t", "p", false, null, "nope", CancellationToken.None));
        Assert.Contains(projectName, wrong.Message);

        var info = await shell.DelegateAsync(ShellViewModel.ChiefId, "Fix parser", "Fix the parser.", false, null, projectName, CancellationToken.None);
        var task = shell.Projects.Single().Threads.Single(thread => thread.Id == info.TaskId);
        Assert.True(task.IsChiefTask);
        Assert.Equal(projectName, info.Project);
        Assert.Equal("Task from the Chief of Staff", task.KindLabel);
        await WaitUntilAsync(() => task.IsWorking);

        // While the task runs, interim briefings reach the chief on the timer. The first tick may land before the
        // task has done anything, so wait for one that reflects its progress.
        static bool Complete(MessageEntry entry) => entry.IsBriefing && entry.Text.Contains("/p/parser.cs") && entry.Text.Contains("Latest reply: Parser fixed.");
        await WaitUntilAsync(() => shell.Chief.Transcript.ToList().OfType<MessageEntry>().Any(Complete));
        var interim = shell.Chief.Transcript.ToList().OfType<MessageEntry>().First(Complete);
        Assert.StartsWith("[Briefing]\nTask progress: \"Fix parser\"", interim.Text);

        // When it finishes, a final briefing follows, and the chief answers it.
        release.SetResult();
        await WaitUntilAsync(() => shell.Chief.Transcript.ToList().OfType<MessageEntry>().Any(entry => entry.IsBriefing && entry.Text.Contains("Task finished")));
        await WaitUntilAsync(() => !shell.Chief.IsBusy);
        var final = shell.Chief.Transcript.ToList().OfType<MessageEntry>().Last(entry => entry.IsBriefing);
        Assert.Contains("status done", final.Text);
        Assert.Contains("Final reply: Parser fixed.", final.Text);
        Assert.Equal("Noted.", Assert.IsType<MessageEntry>(shell.Chief.Transcript[^1]).Text);
        Assert.Equal("Chief of Staff", shell.Chief.Title);

        // Briefings persist with the rest of the transcript.
        var snapshot = new ThreadStore(harness.Directory).Load(ShellViewModel.ChiefId)!;
        Assert.Contains(snapshot.Entries, entry => entry.Role == "briefing");
        await shell.ShutdownAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            await Task.Delay(10);
        }
    }
}

public sealed class SettingsTests
{
    [Fact]
    public void SettingsPersistAndDriveTheBriefingInterval()
    {
        var directory = Directory.CreateTempSubdirectory("vibe-ui-settings").FullName;
        var shell = new ShellViewModel(AppState.Load(directory), new ThreadStore(directory), () => null, (_, _, _, _) => throw new InvalidOperationException(), _ => { }, action => action());
        Assert.Equal(TimeSpan.FromMinutes(3), shell.BriefingInterval);

        shell.BriefingMinutes = 7;
        shell.VibeExecutable = " /opt/vibe/bin/vibe-acp ";
        shell.BriefingMinutes = 0;

        Assert.Equal(1, shell.BriefingMinutes);
        var saved = AppState.Load(directory);
        Assert.Equal(("/opt/vibe/bin/vibe-acp", 1), (saved.VibeExecutable, saved.BriefingMinutes));
        var reloaded = new ShellViewModel(saved, new ThreadStore(directory), () => null, (_, _, _, _) => throw new InvalidOperationException(), _ => { }, action => action());
        Assert.Equal(TimeSpan.FromMinutes(1), reloaded.BriefingInterval);
        Assert.Equal("/opt/vibe/bin/vibe-acp", reloaded.VibeExecutable);
    }
}

public sealed class VibeUpdateTests
{
    [Fact]
    public async Task ReadyAgentTriggersAnUpdateCheckAndOffersTheNewerVersion()
    {
        var directory = Directory.CreateTempSubdirectory("vibe-ui-update").FullName;
        var checks = 0;
        var shell = new ShellViewModel(AppState.Load(directory), new ThreadStore(directory),
            () => "/usr/local/bin/vibe-acp",
            (_, cwd, handler, _) => new FakeAcpClient { Handler = handler, Workspace = cwd },
            _ => { },
            action => action(),
            latestVibeVersion: _ =>
            {
                checks++;
                return Task.FromResult<string?>("3.0.4");
            });

        await shell.InitializeAsync();
        await Task.Yield();

        Assert.Equal(1, checks);
        Assert.Equal("2.25.0", shell.InstalledVibeVersion);
        Assert.Equal("3.0.4", shell.AvailableVibeVersion);
        Assert.True(shell.HasVibeUpdate);
        Assert.Contains("3.0.4 is available", shell.VibeUpdateStatus);
        await shell.ShutdownAsync();
    }
}

public sealed class ActionToolTests
{
    [Fact]
    public async Task OrchestratorCanListAndRunProjectActions()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Shell commands here are POSIX.");
        var directory = Directory.CreateTempSubdirectory("vibe-ui-actiontools").FullName;
        var workspace = Directory.CreateTempSubdirectory("vibe-ui-actiontools-project").FullName;
        File.WriteAllText(Path.Combine(workspace, "vibe.re"), """{"actions":[{"name":"test","command":"echo all green"},{"name":"run","command":"echo nope","tool":false}]}""");
        var shell = new ShellViewModel(AppState.Load(directory), new ThreadStore(directory),
            () => "/usr/local/bin/vibe-acp",
            (_, cwd, handler, _) => new FakeAcpClient { Handler = handler, Workspace = cwd },
            _ => { },
            action => action(),
            latestVibeVersion: _ => Task.FromResult<string?>(null));
        await shell.AddProjectAsync(workspace);
        await shell.NewOrchestratorAsync(shell.SelectedProject);
        var orchestrator = shell.SelectedThread!;

        var listed = await shell.ListActionsAsync(orchestrator.Id, null);
        Assert.Equal(["test"], listed.Select(action => action.Name)); // "run" is not offered to agents

        var outcome = await shell.RunActionAsync(orchestrator.Id, "test", null, CancellationToken.None);
        Assert.True(outcome.Ok);
        Assert.Contains("all green", outcome.Output);
        Assert.True(shell.SelectedProject!.Actions.IsDrawerOpen);
        Assert.Equal("done", shell.SelectedProject.Actions.CurrentRun!.Summary);

        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => shell.RunActionAsync(orchestrator.Id, "deploy", null, CancellationToken.None));
        Assert.Contains("test", missing.Message);
        var hidden = await Assert.ThrowsAsync<InvalidOperationException>(() => shell.RunActionAsync(orchestrator.Id, "run", null, CancellationToken.None));
        Assert.Contains("not available", hidden.Message);
        await shell.ShutdownAsync();
    }
}

public sealed class WorktreeThreadTests
{
    [Fact]
    public async Task WorktreeThreadGetsItsOwnCheckoutBranchAndChangesPane()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Shell commands here are POSIX.");
        var directory = Directory.CreateTempSubdirectory("vibe-ui-wt").FullName;
        var repo = Directory.CreateTempSubdirectory("vibe-ui-wt-repo").FullName;
        Run(repo, "git", "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(repo, "vibe.re"), """{"actions":[{"name":"setup","command":"echo prepared > setup.txt","runOnWorktreeCreate":true}]}""");
        Run(repo, "git", "add", "-A");
        Run(repo, "git", "-c", "user.email=t@example.com", "-c", "user.name=T", "commit", "-q", "-m", "init");
        var clients = new List<FakeAcpClient>();
        var shell = new ShellViewModel(AppState.Load(directory), new ThreadStore(directory),
            () => "/usr/local/bin/vibe-acp",
            (_, cwd, handler, _) =>
            {
                var client = new FakeAcpClient { Handler = handler, Workspace = cwd };
                clients.Add(client);
                return client;
            },
            _ => { },
            action => action(),
            latestVibeVersion: _ => Task.FromResult<string?>(null));
        await shell.AddProjectAsync(repo);
        var project = shell.SelectedProject!;

        await shell.NewWorktreeThreadAsync(project);

        var thread = shell.SelectedThread!;
        Assert.True(thread.HasWorktree);
        Assert.StartsWith("vibe/thread-", thread.Record.Branch);
        Assert.True(Directory.Exists(thread.WorkspacePath));
        Assert.NotEqual(repo, thread.WorkspacePath);
        Assert.Equal("prepared", File.ReadAllText(Path.Combine(thread.WorkspacePath, "setup.txt")).Trim()); // runOnWorktreeCreate ran there
        Assert.Contains($"session/new:{thread.WorkspacePath}", clients.Single().Calls); // one agent per project, session in the worktree
        Assert.NotSame(project.Changes, shell.ActiveChanges);
        await shell.ActiveChanges!.RefreshAsync();
        Assert.Equal(thread.Record.Branch, shell.ActiveChanges.Branch);
        Assert.Equal(2, project.Threads.Count);

        File.Delete(Path.Combine(thread.WorkspacePath, "setup.txt")); // leave the worktree clean so deleting the thread removes it
        await shell.DeleteThreadAsync(thread);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Directory.Exists(thread.WorkspacePath) && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        Assert.False(Directory.Exists(thread.WorkspacePath));
        await shell.ShutdownAsync();
    }

    private static void Run(string directory, string file, params string[] arguments)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo(file) { WorkingDirectory = directory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(startInfo)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}

public sealed class BrowserTests
{
    [Theory]
    [InlineData("3000", "http://localhost:3000/")]
    [InlineData("localhost:5173", "http://localhost:5173/")]
    [InlineData("127.0.0.1:8080/app", "http://127.0.0.1:8080/app")]
    [InlineData("example.com/docs", "https://example.com/docs")]
    [InlineData("https://mistral.ai", "https://mistral.ai/")]
    public void AddressesAreNormalised(string typed, string expected)
    {
        var browser = new BrowserViewModel(_ => { });
        browser.Navigate(typed);
        Assert.Equal(expected, browser.Source.ToString());
        Assert.Null(browser.Error);
    }

    [Fact]
    public void BadAddressesAreRefused()
    {
        var browser = new BrowserViewModel(_ => { });
        browser.Navigate("file:///etc/passwd");
        Assert.NotNull(browser.Error);
        Assert.Equal(BrowserViewModel.DefaultUrl, browser.Source.ToString());
    }

    [Fact]
    public async Task AgentsCanOpenTheBrowserPaneAndTheUrlIsRemembered()
    {
        var directory = Directory.CreateTempSubdirectory("vibe-ui-browser").FullName;
        var workspace = Directory.CreateTempSubdirectory("vibe-ui-browser-project").FullName;
        var shell = new ShellViewModel(AppState.Load(directory), new ThreadStore(directory),
            () => "/usr/local/bin/vibe-acp",
            (_, cwd, handler, _) => new FakeAcpClient { Handler = handler, Workspace = cwd },
            _ => { }, action => action(), latestVibeVersion: _ => Task.FromResult<string?>(null));
        await shell.AddProjectAsync(workspace);
        await shell.NewOrchestratorAsync(shell.SelectedProject);
        Assert.False(shell.ShowBrowserPane);

        shell.IsChangesVisible = true;
        await shell.OpenInBrowserAsync(shell.SelectedThread!.Id, "localhost:4321", null);

        Assert.True(shell.ShowBrowserPane);
        Assert.False(shell.ShowChangesPane); // one right-hand pane at a time
        Assert.Equal("http://localhost:4321/", shell.ActiveBrowser!.Source.ToString());
        shell.ActiveBrowser.ReportNavigation(new Uri("http://localhost:4321/about"), true, true, false);
        shell.Save();
        Assert.Equal("http://localhost:4321/about", AppState.Load(directory).Projects[0].BrowserUrl);
        await shell.ShutdownAsync();
    }
}
