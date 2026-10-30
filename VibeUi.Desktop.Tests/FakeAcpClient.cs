using System.Text.Json;
using VibeUi.Acp;
using VibeUi.Acp.JsonRpc;

namespace VibeUi.Desktop.Tests;

/// <summary>A scripted in-memory agent for view-model tests; the handler is the view model itself.</summary>
public sealed class FakeAcpClient : IAcpClient
{
    private readonly TaskCompletionSource<Exception?> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IAcpClientHandler Handler { get; set; } = null!;
    public string Workspace { get; set; } = "";
    public List<string> Calls { get; } = [];
    public bool Disposed { get; private set; }
    public bool RequireAuthentication { get; set; }
    public IReadOnlyList<AuthMethod> AuthMethods { get; set; } = [new("browser-auth-delegated", "Sign in through Mistral AI Studio")];
    public Func<FakeAcpClient, string, Task<PromptResponse>> PromptScript { get; set; } = (_, _) => Task.FromResult(new PromptResponse(StopReasons.EndTurn));
    public TaskCompletionSource? SignInCompletion { get; set; }
    public string SessionId { get; set; } = "s-1";
    public string TrustStatus { get; set; } = "trusted";

    public Task<Exception?> Closed => _closed.Task;

    public void SimulateExit(Exception? failure = null) => _closed.TrySetResult(failure);

    public Task<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken = default)
    {
        Calls.Add("initialize");
        Assert.True((bool)request.ClientCapabilities.Meta!["browser-auth-delegated"]!);
        return Task.FromResult(new InitializeResponse(1, new AgentCapabilities(true), AuthMethods, new Implementation("@mistralai/mistral-vibe", "2.25.0", "Mistral Vibe")));
    }

    public async Task<AuthenticateResponse> AuthenticateAsync(string methodId, IReadOnlyDictionary<string, object?>? arguments = null, CancellationToken cancellationToken = default)
    {
        var action = arguments?.GetValueOrDefault("action") as string;
        if (action == "complete")
            Assert.Equal("a-1", arguments!["attemptId"]);
        Calls.Add($"authenticate:{methodId}:{action}");
        if (action == "start")
            return new AuthenticateResponse(JsonSerializer.SerializeToElement(new Dictionary<string, object>
            {
                ["browser-auth-delegated"] = new { attemptId = "a-1", signInUrl = "https://auth.example.test/login" },
            }));
        if (SignInCompletion is not null)
            await SignInCompletion.Task;
        RequireAuthentication = false;
        return new AuthenticateResponse();
    }

    private int _sessionCounter;

    public Task<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken = default)
    {
        Calls.Add($"session/new:{request.Cwd}");
        McpServersSeen.Add(request.McpServers);
        if (RequireAuthentication)
            throw new JsonRpcException(JsonRpcException.AuthenticationRequired, "Authentication required");
        var id = _sessionCounter++ == 0 ? SessionId : $"{SessionId}-{_sessionCounter}";
        return NewSessionCoreAsync(request.Cwd, id);
    }

    private Task<NewSessionResponse> NewSessionCoreAsync(string cwd, string? id = null)
    {
        id ??= SessionId;
        var modes = new SessionModeState("accept-edits",
        [
            new SessionMode("ask", "Ask"),
            new SessionMode("accept-edits", "Accept Edits"),
            new SessionMode("auto-approve", "Auto Approve"),
        ]);
        var options = new List<SessionConfigOption>
        {
            new("mode", "Session Mode", "select", JsonSerializer.SerializeToElement("accept-edits"), "mode"),
            new("model", "Model", "select", JsonSerializer.SerializeToElement("mistral-medium-3.5"), "model",
                [new ConfigOptionChoice("mistral-medium-3.5", "Mistral Medium 3.5"), new ConfigOptionChoice("devstral-small", "devstral-small")]),
            new("thinking", "Thinking", "select", JsonSerializer.SerializeToElement("medium"), "thought_level",
                [new ConfigOptionChoice("off", "Off"), new ConfigOptionChoice("medium", "Medium"), new ConfigOptionChoice("high", "High")]),
        };
        var meta = JsonSerializer.SerializeToElement(new { workspace_trust = new { status = TrustStatus } });
        return Task.FromResult(new NewSessionResponse(id, modes, options, meta));
    }

    /// <summary>Session ids that <c>session/load</c> accepts; others fail like Vibe does for unknown sessions.</summary>
    public HashSet<string> LoadableSessions { get; } = [];

    /// <summary>Replayed as session updates (JSON) while a session loads.</summary>
    public List<string> ReplayOnLoad { get; } = [];

    public List<IReadOnlyList<object>> McpServersSeen { get; } = [];

    public async Task<LoadSessionResponse> LoadSessionAsync(LoadSessionRequest request, CancellationToken cancellationToken = default)
    {
        Calls.Add($"session/load:{request.SessionId}:{request.Cwd}");
        McpServersSeen.Add(request.McpServers);
        if (RequireAuthentication)
            throw new JsonRpcException(JsonRpcException.AuthenticationRequired, "Authentication required");
        if (!LoadableSessions.Contains(request.SessionId))
            throw new JsonRpcException(JsonRpcException.InvalidParams, $"Session {request.SessionId} not found");
        foreach (var update in ReplayOnLoad)
            await Handler.OnSessionUpdateAsync(request.SessionId, SessionUpdateParser.Parse(JsonSerializer.Deserialize<JsonElement>(update)));
        var fresh = await NewSessionCoreAsync(request.Cwd);
        return new LoadSessionResponse(fresh.Modes, fresh.ConfigOptions, fresh.Meta);
    }

    public Task<ListSessionsResponse> ListSessionsAsync(ListSessionsRequest request, CancellationToken cancellationToken = default)
    {
        Calls.Add("session/list");
        return Task.FromResult(new ListSessionsResponse(LoadableSessions.Select(id => new SessionInfo(id, Workspace, "Saved " + id)).ToList()));
    }

    public Task<PromptResponse> PromptAsync(PromptRequest request, CancellationToken cancellationToken = default)
    {
        Calls.Add($"session/prompt:{request.Prompt[0].Text}");
        return PromptScript(this, request.SessionId);
    }

    public Task CancelAsync(string sessionId)
    {
        Calls.Add("session/cancel");
        return Task.CompletedTask;
    }

    public Task SetModeAsync(string sessionId, string modeId, CancellationToken cancellationToken = default)
    {
        Calls.Add($"session/set_mode:{modeId}");
        return Task.CompletedTask;
    }

    public Task<SetSessionConfigOptionResponse> SetConfigOptionAsync(string sessionId, string configId, string value, CancellationToken cancellationToken = default)
    {
        Calls.Add($"session/set_config_option:{configId}={value}");
        return Task.FromResult(new SetSessionConfigOptionResponse(
        [
            new SessionConfigOption("model", "Model", "select", JsonSerializer.SerializeToElement(value), "model",
                [new ConfigOptionChoice("mistral-medium-3.5", "Mistral Medium 3.5"), new ConfigOptionChoice("devstral-small", "devstral-small")]),
        ]));
    }

    public Task CloseSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        Calls.Add($"session/close:{sessionId}");
        return Task.CompletedTask;
    }

    public Task<JsonElement> ExtensionRequestAsync(string method, object? parameters, CancellationToken cancellationToken = default)
    {
        Calls.Add($"ext:{method}");
        if (method is "_session/set_title" or "_session/delete")
            return Task.FromResult(JsonSerializer.SerializeToElement(new { }));
        if (method == "_auth/signOut")
        {
            RequireAuthentication = true;
            return Task.FromResult(JsonSerializer.SerializeToElement(new { }));
        }
        TrustStatus = "trusted";
        return Task.FromResult(JsonSerializer.SerializeToElement(new { trust_status = "trusted" }));
    }

    public Task SendUpdateAsync(string update, string? sessionId = null)
        => Handler.OnSessionUpdateAsync(sessionId ?? SessionId, SessionUpdateParser.Parse(JsonSerializer.Deserialize<JsonElement>(update)));

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _closed.TrySetResult(null);
        return ValueTask.CompletedTask;
    }
}
