using System.Text.Json;
using VibeUi.Acp.JsonRpc;

namespace VibeUi.Acp.Tests;

public sealed class AcpClientTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static readonly object VibeSessionResponse = new
    {
        sessionId = "s-1",
        modes = new
        {
            currentModeId = "accept-edits",
            availableModes = new[]
            {
                new { id = "ask", name = "Ask", description = "Requires approval for tool executions" },
                new { id = "accept-edits", name = "Accept Edits", description = "Auto-approves file edits only" },
            },
        },
        configOptions = new object[]
        {
            new { id = "mode", name = "Session Mode", category = "mode", type = "select", currentValue = "accept-edits", options = new[] { new { value = "ask", name = "Ask" } } },
            new { id = "model", name = "Model", category = "model", type = "select", currentValue = "mistral-medium-3.5", options = new[] { new { value = "mistral-medium-3.5", name = "Mistral Medium 3.5" }, new { value = "devstral-small", name = "devstral-small" } } },
        },
        _meta = new { workspace_trust = new { status = "untrusted" } },
    };

    [Fact]
    public async Task InitializeAndNewSessionExposeModesAndConfigOptions()
    {
        await using var agent = new FakeAgent();
        JsonElement? initializeParameters = null;
        agent.On("initialize", parameters =>
        {
            initializeParameters = parameters;
            return new
            {
                protocolVersion = 1,
                agentCapabilities = new { loadSession = true },
                authMethods = new[] { new { id = "browser-auth", name = "Sign in through Mistral AI Studio" } },
                agentInfo = new { name = "@mistralai/mistral-vibe", title = "Mistral Vibe", version = "2.25.0" },
            };
        });
        agent.On("session/new", _ => VibeSessionResponse);
        agent.Start();
        var handler = new RecordingHandler();
        await using var client = agent.ConnectClient(handler);

        var initialized = await client.InitializeAsync(new InitializeRequest(AcpClient.ProtocolVersion,
            new ClientCapabilities(new FileSystemCapabilities(true, true), Meta: new Dictionary<string, object?> { ["browser-auth-delegated"] = true }),
            new Implementation("vibe-ui", "0.1.0"))).WaitAsync(Timeout);
        var session = await client.NewSessionAsync(new NewSessionRequest("/tmp/work", [])).WaitAsync(Timeout);

        Assert.Equal("Mistral Vibe", initialized.AgentInfo?.Title);
        Assert.Equal("browser-auth", Assert.Single(initialized.AuthMethods!).Id);
        Assert.True(initializeParameters!.Value.GetProperty("clientCapabilities").GetProperty("_meta").GetProperty("browser-auth-delegated").GetBoolean());
        Assert.Equal("s-1", session.SessionId);
        Assert.Equal("accept-edits", session.Modes!.CurrentModeId);
        Assert.Equal(["ask", "accept-edits"], session.Modes.AvailableModes.Select(mode => mode.Id));
        var model = Assert.Single(session.ConfigOptions!, option => option.Category == "model");
        Assert.Equal("mistral-medium-3.5", model.CurrentValueText);
        Assert.Equal(2, model.Options!.Count);
    }

    [Fact]
    public async Task PromptStreamsUpdatesAndRoundTripsPermissionPrompts()
    {
        await using var agent = new FakeAgent();
        JsonElement? permissionAnswer = null;
        agent.On("session/prompt", async parameters =>
        {
            var sessionId = parameters!.Value.GetProperty("sessionId").GetString()!;
            await agent.SendUpdateAsync(sessionId, new { sessionUpdate = "user_message_chunk", content = new { type = "text", text = "hi" }, messageId = "m1" });
            await agent.SendUpdateAsync(sessionId, new { sessionUpdate = "agent_thought_chunk", content = new { type = "text", text = "thinking" } });
            await agent.SendUpdateAsync(sessionId, new { sessionUpdate = "tool_call", toolCallId = "t1", title = "read_file", kind = "read", status = "in_progress" });
            permissionAnswer = await agent.RequestClientAsync("session/request_permission", new
            {
                sessionId,
                toolCall = new { toolCallId = "t1" },
                options = new[]
                {
                    new { optionId = "allow_once", name = "Allow once", kind = "allow_once" },
                    new { optionId = "reject_once", name = "Deny", kind = "reject_once" },
                },
            });
            await agent.SendUpdateAsync(sessionId, new
            {
                sessionUpdate = "tool_call_update", toolCallId = "t1", status = "completed",
                content = new[] { new { type = "content", content = new { type = "text", text = "hello world" } } },
                locations = new[] { new { path = "/tmp/work/note.txt" } },
            });
            await agent.SendUpdateAsync(sessionId, new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "It says hello." } });
            await agent.SendUpdateAsync(sessionId, new { sessionUpdate = "usage_update", used = 5029, size = 200000, cost = new { amount = 0.005, currency = "USD" } });
            await agent.SendUpdateAsync(sessionId, new { sessionUpdate = "something_new", payload = 1 });
            return new { stopReason = "end_turn" };
        });
        agent.Start();
        var handler = new RecordingHandler();
        await using var client = agent.ConnectClient(handler);

        var response = await client.PromptAsync(new PromptRequest("s-1", [ContentBlock.FromText("hi")])).WaitAsync(Timeout);

        Assert.Equal(StopReasons.EndTurn, response.StopReason);
        Assert.Equal("allow_once", permissionAnswer!.Value.GetProperty("outcome").GetProperty("optionId").GetString());
        Assert.Equal("t1", Assert.Single(handler.PermissionRequests).ToolCall.ToolCallId);
        // Notifications are dispatched independently of the response, so the last ones may still be in flight.
        var deadline = DateTime.UtcNow + Timeout;
        while (handler.Updates.Count < 7 && DateTime.UtcNow < deadline)
            await Task.Delay(5);
        List<SessionUpdate> updates;
        lock (handler.Updates)
            updates = handler.Updates.Select(entry => entry.Update).ToList();
        Assert.Equal(["user_message_chunk", "agent_thought_chunk", "tool_call", "tool_call_update", "agent_message_chunk", "usage_update", "something_new"],
            updates.Select(update => update.Kind));
        var toolUpdate = Assert.IsType<ToolCallSessionUpdate>(updates[3]);
        Assert.Equal("completed", toolUpdate.ToolCall.Status);
        Assert.Equal("hello world", toolUpdate.ToolCall.Content![0].Content!.Text);
        Assert.Equal("/tmp/work/note.txt", toolUpdate.ToolCall.Locations![0].Path);
        Assert.Equal(5029, Assert.IsType<UsageSessionUpdate>(updates[5]).Used);
        Assert.IsType<UnknownSessionUpdate>(updates[6]);
    }

    [Fact]
    public async Task FileRequestsFromTheAgentUseTheHandler()
    {
        await using var agent = new FakeAgent();
        agent.Start();
        var handler = new RecordingHandler();
        await using var client = agent.ConnectClient(handler);

        await agent.RequestClientAsync("fs/write_text_file", new { sessionId = "s-1", path = "/tmp/work/a.txt", content = "alpha" }).WaitAsync(Timeout);
        var read = await agent.RequestClientAsync("fs/read_text_file", new { sessionId = "s-1", path = "/tmp/work/a.txt" }).WaitAsync(Timeout);

        Assert.Equal("alpha", read.GetProperty("content").GetString());
        var missing = await Assert.ThrowsAsync<JsonRpcException>(() => agent.RequestClientAsync("fs/read_text_file", new { sessionId = "s-1", path = "/nope" }).WaitAsync(Timeout));
        Assert.Equal(JsonRpcException.InvalidParams, missing.Code);
    }

    [Fact]
    public async Task AuthenticatePutsItsArgumentsInMeta()
    {
        await using var agent = new FakeAgent();
        JsonElement? seen = null;
        agent.On("authenticate", parameters =>
        {
            seen = parameters;
            return new { _meta = new Dictionary<string, object> { ["browser-auth-delegated"] = new { attemptId = "a1", signInUrl = "https://example.test/login" } } };
        });
        agent.Start();
        await using var client = agent.ConnectClient(new RecordingHandler());

        var response = await client.AuthenticateAsync("browser-auth-delegated", new Dictionary<string, object?> { ["action"] = "start" }).WaitAsync(Timeout);

        Assert.Equal("browser-auth-delegated", seen!.Value.GetProperty("methodId").GetString());
        Assert.False(seen.Value.TryGetProperty("action", out _)); // Agents drop unknown top-level fields; Vibe reads _meta.
        Assert.Equal("start", seen.Value.GetProperty("_meta").GetProperty("action").GetString());
        Assert.Equal("https://example.test/login", response.Meta!.Value.GetProperty("browser-auth-delegated").GetProperty("signInUrl").GetString());
    }

    [Fact]
    public async Task CancelIsANotificationAndModeChangesAreRequests()
    {
        await using var agent = new FakeAgent();
        JsonElement? modeParameters = null;
        agent.On("session/set_mode", parameters =>
        {
            modeParameters = parameters;
            return new { };
        });
        agent.Start();
        await using var client = agent.ConnectClient(new RecordingHandler());

        await client.SetModeAsync("s-1", "ask").WaitAsync(Timeout);
        await client.CancelAsync("s-1");
        await Task.Delay(100);

        Assert.Equal("ask", modeParameters!.Value.GetProperty("modeId").GetString());
        var cancel = Assert.Single(agent.ReceivedNotifications);
        Assert.Equal("session/cancel", cancel.Method);
        Assert.Equal("s-1", cancel.Parameters!.Value.GetProperty("sessionId").GetString());
    }
}

public sealed class WorkspaceTrustTests
{
    [Fact]
    public void UntrustedWithoutADecisionIsReportedAsNothingToTrust()
    {
        static NewSessionResponse With(string json) => new("s", Meta: JsonSerializer.Deserialize<JsonElement>(json));

        Assert.Equal("trusted", With("""{"workspace_trust":{"status":"trusted","details":null}}""").WorkspaceTrustStatus);
        Assert.Equal("untrusted", With("""{"workspace_trust":{"status":"untrusted","details":{"detected_files":["AGENTS.md"]}}}""").WorkspaceTrustStatus);
        Assert.Null(With("""{"workspace_trust":{"status":"untrusted","details":null}}""").WorkspaceTrustStatus);
        Assert.Null(With("""{}""").WorkspaceTrustStatus);
    }
}
