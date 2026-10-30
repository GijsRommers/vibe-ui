using VibeUi.Acp;
using VibeUi.Desktop.Services;
using VibeUi.Desktop.ViewModels;

namespace VibeUi.Desktop.Tests;

public sealed class MainViewModelTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static (MainViewModel Model, FakeAcpClient Client, List<string> OpenedUrls) Build(Action<FakeAcpClient>? configure = null, string? executable = "/usr/local/bin/vibe-acp")
    {
        var client = new FakeAcpClient();
        configure?.Invoke(client);
        var opened = new List<string>();
        var workspace = Directory.CreateTempSubdirectory("vibe-ui-vm").FullName;
        var model = new MainViewModel(
            locateAgent: () => executable,
            startAgent: (_, cwd, handler, _) =>
            {
                client.Handler = handler;
                client.Workspace = cwd;
                return client;
            },
            openUrl: opened.Add,
            dispatch: action => action(),
            settings: new DesktopSettings { WorkspacePath = workspace },
            clientVersion: "0.1.0-test");
        return (model, client, opened);
    }

    [Fact]
    public async Task MissingAgentShowsInstallGuidance()
    {
        var (model, _, _) = Build(executable: null);

        await model.ConnectAsync();

        Assert.Equal(AgentState.NotInstalled, model.State);
        Assert.False(model.IsInstalled);
        Assert.Contains("install.sh", model.InstallCommand);
    }

    [Fact]
    public async Task ConnectOpensASessionWithModesAndModels()
    {
        var (model, client, _) = Build();

        await model.ConnectAsync();

        Assert.Equal(AgentState.Ready, model.State);
        Assert.Equal(model.WorkspacePath, client.Workspace);
        Assert.Equal("Mistral Vibe 2.25.0", model.AgentDescription);
        Assert.Equal(["ask", "accept-edits", "auto-approve"], model.Modes.Select(mode => mode.Id));
        Assert.Equal("accept-edits", model.SelectedMode?.Id);
        Assert.Equal("mistral-medium-3.5", model.SelectedModel?.Value);
        Assert.False(model.IsWorkspaceUntrusted);
        Assert.DoesNotContain(client.Calls, call => call.StartsWith("session/set_mode"));
    }

    [Fact]
    public async Task SignInUsesTheDelegatedBrowserFlowAndThenOpensTheSession()
    {
        var (model, client, opened) = Build(client =>
        {
            client.RequireAuthentication = true;
            client.SignInCompletion = new TaskCompletionSource();
        });
        await model.ConnectAsync();
        Assert.Equal(AgentState.SignInRequired, model.State);
        Assert.True(model.SignInCommand.CanExecute(null));

        var signIn = model.SignInCommand.ExecuteAsync(null);
        Assert.Equal(AgentState.SigningIn, model.State);
        Assert.Equal("https://auth.example.test/login", model.SignInUrl);
        Assert.Equal(["https://auth.example.test/login"], opened);
        client.SignInCompletion!.SetResult();
        await signIn.WaitAsync(Timeout);

        Assert.Equal(AgentState.Ready, model.State);
        Assert.Null(model.SignInUrl);
        Assert.Equal(["initialize", $"session/new:{model.WorkspacePath}", "authenticate:browser-auth-delegated:start", "authenticate:browser-auth-delegated:complete", $"session/new:{model.WorkspacePath}"], client.Calls);
    }

    [Fact]
    public async Task SendingAPromptStreamsTheTranscriptAndAnswersPermissions()
    {
        var (model, client, _) = Build(client => client.PromptScript = async (fake, _) =>
        {
            await fake.SendUpdateAsync("""{"sessionUpdate":"user_message_chunk","content":{"type":"text","text":"read it"}}""");
            await fake.SendUpdateAsync("""{"sessionUpdate":"agent_thought_chunk","content":{"type":"text","text":"Let me "},"messageId":"t1"}""");
            await fake.SendUpdateAsync("""{"sessionUpdate":"agent_thought_chunk","content":{"type":"text","text":"look."},"messageId":"t1"}""");
            await fake.SendUpdateAsync("""{"sessionUpdate":"tool_call","toolCallId":"c1","title":"read_file","kind":"read","status":"in_progress"}""");
            var permission = fake.Handler.RequestPermissionAsync(new RequestPermissionRequest(fake.SessionId, new ToolCallUpdate("c1", "Reading note.txt"),
                [new PermissionOption("allow_once", "Allow once", "allow_once"), new PermissionOption("reject_once", "Deny", "reject_once")]), CancellationToken.None);
            await Task.Yield();
            return await permission.ContinueWith(async answer =>
            {
                Assert.Equal("allow_once", answer.Result.Outcome.OptionId);
                await fake.SendUpdateAsync("""{"sessionUpdate":"tool_call_update","toolCallId":"c1","status":"completed","content":[{"type":"content","content":{"type":"text","text":"hello world"}}],"locations":[{"path":"/w/note.txt"}]}""");
                await fake.SendUpdateAsync("""{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"It says "},"messageId":"m1"}""");
                await fake.SendUpdateAsync("""{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"hello."},"messageId":"m1"}""");
                await fake.SendUpdateAsync("""{"sessionUpdate":"usage_update","used":1200,"size":200000,"cost":{"amount":0.0012,"currency":"USD"}}""");
                return new PromptResponse(StopReasons.EndTurn);
            }, TaskScheduler.Default).Unwrap();
        });
        await model.ConnectAsync();
        model.PromptText = "read it";
        Assert.True(model.SendCommand.CanExecute(null));

        var send = model.SendCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => model.PendingPermission is not null);
        Assert.Equal(AgentState.Working, model.State);
        Assert.Equal("Reading note.txt", model.PendingPermission!.Title);
        model.PendingPermission.Options.Single(option => option.IsAllow).ChooseCommand.Execute(null);
        await send.WaitAsync(Timeout);

        Assert.Equal(AgentState.Ready, model.State);
        Assert.Null(model.PendingPermission);
        Assert.Equal("", model.PromptText);
        var entries = model.Transcript.ToList();
        Assert.Equal(4, entries.Count);
        var user = Assert.IsType<MessageEntry>(entries[0]);
        Assert.Equal((MessageRole.User, "read it"), (user.Role, user.Text));
        var thought = Assert.IsType<MessageEntry>(entries[1]);
        Assert.Equal((MessageRole.Thought, "Let me look.", true), (thought.Role, thought.Text, thought.IsHidden));
        var tool = Assert.IsType<ToolCallEntry>(entries[2]);
        Assert.Equal(("Reading note.txt", "completed", "hello world", "/w/note.txt"), (tool.Title, tool.Status, tool.Output, tool.Locations));
        var reply = Assert.IsType<MessageEntry>(entries[3]);
        Assert.Equal((MessageRole.Agent, "It says hello."), (reply.Role, reply.Text));
        Assert.Contains("1,200", model.UsageText);
        Assert.Equal("Done.", model.Status);

        model.ShowThoughts = true;
        Assert.False(thought.IsHidden);
    }

    [Fact]
    public async Task StopSendsCancelAndCancelsAPendingPermission()
    {
        var finish = new TaskCompletionSource<PromptResponse>();
        var (model, client, _) = Build(client => client.PromptScript = async (fake, _) =>
        {
            await fake.SendUpdateAsync("""{"sessionUpdate":"tool_call","toolCallId":"c1","title":"bash","kind":"execute","status":"in_progress"}""");
            var answer = fake.Handler.RequestPermissionAsync(new RequestPermissionRequest(fake.SessionId, new ToolCallUpdate("c1"),
                [new PermissionOption("allow_once", "Allow once", "allow_once")]), CancellationToken.None);
            var outcome = await answer;
            Assert.Equal("cancelled", outcome.Outcome.Outcome);
            return await finish.Task;
        });
        await model.ConnectAsync();
        model.PromptText = "run it";
        var send = model.SendCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => model.PendingPermission is not null);
        Assert.True(model.StopCommand.CanExecute(null));

        await model.StopCommand.ExecuteAsync(null);
        finish.SetResult(new PromptResponse(StopReasons.Cancelled));
        await send.WaitAsync(Timeout);

        Assert.Contains("session/cancel", client.Calls);
        Assert.Null(model.PendingPermission);
        Assert.Equal(AgentState.Ready, model.State);
        Assert.Equal("Stopped.", model.Status);
    }

    [Fact]
    public async Task ChangingModeAndModelTalksToTheAgent()
    {
        var (model, client, _) = Build();
        await model.ConnectAsync();

        model.SelectedMode = model.Modes.Single(mode => mode.Id == "ask");
        model.SelectedModel = model.Models.Single(choice => choice.Value == "devstral-small");
        await WaitUntilAsync(() => client.Calls.Contains("session/set_config_option:model=devstral-small"));

        Assert.Contains("session/set_mode:ask", client.Calls);
        Assert.Equal("devstral-small", model.SelectedModel?.Value);
    }

    [Fact]
    public async Task ModeChangesAnnouncedByTheAgentAreReflected()
    {
        var (model, client, _) = Build();
        await model.ConnectAsync();

        await client.SendUpdateAsync("""{"sessionUpdate":"current_mode_update","currentModeId":"auto-approve"}""");
        await client.SendUpdateAsync("""{"sessionUpdate":"session_info_update","title":"Reading notes"}""");

        Assert.Equal("auto-approve", model.SelectedMode?.Id);
        Assert.Equal("Reading notes", model.SessionTitle);
        Assert.DoesNotContain(client.Calls, call => call.StartsWith("session/set_mode"));
    }

    [Fact]
    public async Task AgentExitDisconnectsAndAllowsReconnect()
    {
        var (model, client, _) = Build();
        await model.ConnectAsync();

        client.SimulateExit();
        await WaitUntilAsync(() => model.State == AgentState.Disconnected);

        Assert.Contains("exited", model.Status);
        Assert.True(model.ConnectCommand.CanExecute(null));
    }

    [Fact]
    public async Task UntrustedWorkspaceCanBeTrusted()
    {
        var (model, client, _) = Build(client => client.TrustStatus = "untrusted");
        await model.ConnectAsync();
        Assert.True(model.IsWorkspaceUntrusted);

        await model.TrustWorkspaceCommand.ExecuteAsync(null);

        Assert.False(model.IsWorkspaceUntrusted);
        Assert.Contains($"ext:{MainViewModel.TrustDecideMethod}", client.Calls);
    }

    [Fact]
    public async Task SignOutReturnsToTheSignInScreen()
    {
        var (model, client, _) = Build();
        await model.ConnectAsync();
        Assert.True(model.SignOutCommand.CanExecute(null));

        await model.SignOutCommand.ExecuteAsync(null);

        Assert.Equal(AgentState.SignInRequired, model.State);
        Assert.Contains($"ext:{MainViewModel.SignOutMethod}", client.Calls);
        Assert.True(model.SignInCommand.CanExecute(null));
    }

    [Fact]
    public async Task NewSessionClosesTheOldOneAndClearsTheTranscript()
    {
        var (model, client, _) = Build();
        await model.ConnectAsync();
        await client.SendUpdateAsync("""{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"hi"}}""");
        Assert.Single(model.Transcript);

        await model.NewSessionCommand.ExecuteAsync(null);

        Assert.Empty(model.Transcript);
        Assert.Contains("session/close:s-1", client.Calls);
        Assert.Equal(AgentState.Ready, model.State);
    }

    [Fact]
    public async Task ChangingTheWorkspaceRestartsTheAgentThere()
    {
        var (model, client, _) = Build();
        await model.ConnectAsync();
        var other = Directory.CreateTempSubdirectory("vibe-ui-other").FullName;

        await model.ChangeWorkspaceAsync(other);

        Assert.True(client.Disposed);
        Assert.Equal(other, client.Workspace);
        Assert.Equal(AgentState.Ready, model.State);
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
