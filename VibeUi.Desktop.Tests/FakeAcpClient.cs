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

    public Task<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken = default)
    {
        Calls.Add($"session/new:{request.Cwd}");
        if (RequireAuthentication)
            throw new JsonRpcException(JsonRpcException.AuthenticationRequired, "Authentication required");
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
        };
        var meta = JsonSerializer.SerializeToElement(new { workspace_trust = new { status = TrustStatus } });
        return Task.FromResult(new NewSessionResponse(SessionId, modes, options, meta));
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
        if (method == "_auth/signOut")
        {
            RequireAuthentication = true;
            return Task.FromResult(JsonSerializer.SerializeToElement(new { }));
        }
        TrustStatus = "trusted";
        return Task.FromResult(JsonSerializer.SerializeToElement(new { trust_status = "trusted" }));
    }

    public Task SendUpdateAsync(string update) => Handler.OnSessionUpdateAsync(SessionId, SessionUpdateParser.Parse(JsonSerializer.Deserialize<JsonElement>(update)));

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _closed.TrySetResult(null);
        return ValueTask.CompletedTask;
    }
}
