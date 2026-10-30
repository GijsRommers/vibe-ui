using System.Text.Json;
using VibeUi.Acp.JsonRpc;

namespace VibeUi.Acp;

/// <summary>ACP client over a JSON-RPC connection to an agent such as <c>vibe-acp</c>.</summary>
public sealed class AcpClient : IAcpClient
{
    public const int ProtocolVersion = 1;

    private readonly JsonRpcConnection _connection;
    private readonly IAcpClientHandler _handler;
    private readonly Func<ValueTask>? _onDispose;

    /// <param name="agentOutput">Stream the agent writes to (its stdout).</param>
    /// <param name="agentInput">Stream the agent reads from (its stdin).</param>
    /// <param name="handler">Receives updates and agent-to-client requests.</param>
    /// <param name="onDispose">Runs before the streams close, for example to stop the agent process.</param>
    public AcpClient(Stream agentOutput, Stream agentInput, IAcpClientHandler handler, Func<ValueTask>? onDispose = null)
    {
        _handler = handler;
        _onDispose = onDispose;
        _connection = new JsonRpcConnection(agentOutput, agentInput, HandleRequestAsync, HandleNotificationAsync);
    }

    public Task<Exception?> Closed => _connection.Closed;

    public event EventHandler<Exception>? HandlerFailed
    {
        add => _connection.HandlerFailed += value;
        remove => _connection.HandlerFailed -= value;
    }

    public Task<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken = default)
        => RequestAsync<InitializeResponse>("initialize", request, cancellationToken);

    public async Task<AuthenticateResponse> AuthenticateAsync(string methodId, IReadOnlyDictionary<string, object?>? arguments = null, CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, object?> { ["methodId"] = methodId };
        foreach (var (key, value) in arguments ?? new Dictionary<string, object?>())
            parameters[key] = value;
        var result = await _connection.RequestAsync("authenticate", parameters, cancellationToken);
        return Read<AuthenticateResponse>(result) ?? new AuthenticateResponse();
    }

    public Task<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken = default)
        => RequestAsync<NewSessionResponse>("session/new", request, cancellationToken);

    public Task<LoadSessionResponse> LoadSessionAsync(LoadSessionRequest request, CancellationToken cancellationToken = default)
        => RequestAsync<LoadSessionResponse>("session/load", request, cancellationToken);

    public Task<ListSessionsResponse> ListSessionsAsync(ListSessionsRequest request, CancellationToken cancellationToken = default)
        => RequestAsync<ListSessionsResponse>("session/list", request, cancellationToken);

    public Task<PromptResponse> PromptAsync(PromptRequest request, CancellationToken cancellationToken = default)
        => RequestAsync<PromptResponse>("session/prompt", request, cancellationToken);

    public Task CancelAsync(string sessionId)
        => _connection.NotifyAsync("session/cancel", new CancelNotification(sessionId));

    public Task SetModeAsync(string sessionId, string modeId, CancellationToken cancellationToken = default)
        => _connection.RequestAsync("session/set_mode", new SetSessionModeRequest(sessionId, modeId), cancellationToken);

    public Task<SetSessionConfigOptionResponse> SetConfigOptionAsync(string sessionId, string configId, string value, CancellationToken cancellationToken = default)
        => RequestAsync<SetSessionConfigOptionResponse>("session/set_config_option", new SetSessionConfigOptionRequest(sessionId, configId, value), cancellationToken);

    public Task CloseSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        => _connection.RequestAsync("session/close", new CloseSessionRequest(sessionId), cancellationToken);

    public Task<JsonElement> ExtensionRequestAsync(string method, object? parameters, CancellationToken cancellationToken = default)
        => _connection.RequestAsync(method, parameters, cancellationToken);

    private async Task<T> RequestAsync<T>(string method, object parameters, CancellationToken cancellationToken)
    {
        var result = await _connection.RequestAsync(method, parameters, cancellationToken);
        return Read<T>(result) ?? throw new JsonRpcException(JsonRpcException.InternalError, $"The agent returned no result for {method}.");
    }

    private static T? Read<T>(JsonElement element)
        => element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? default : element.Deserialize<T>(JsonRpcConnection.SerializerOptions);

    private async Task<object?> HandleRequestAsync(string method, JsonElement? parameters, CancellationToken cancellationToken)
    {
        switch (method)
        {
            case "session/request_permission":
                return await _handler.RequestPermissionAsync(Parameters<RequestPermissionRequest>(parameters), cancellationToken);
            case "fs/read_text_file":
                return await _handler.ReadTextFileAsync(Parameters<ReadTextFileRequest>(parameters), cancellationToken);
            case "fs/write_text_file":
                await _handler.WriteTextFileAsync(Parameters<WriteTextFileRequest>(parameters), cancellationToken);
                return null;
            default:
                throw new JsonRpcException(JsonRpcException.MethodNotFound, $"Vibe UI does not support {method}.");
        }
    }

    private Task HandleNotificationAsync(string method, JsonElement? parameters)
    {
        if (method != "session/update" || parameters is not { ValueKind: JsonValueKind.Object } body)
            return Task.CompletedTask;
        var sessionId = body.TryGetProperty("sessionId", out var idElement) ? idElement.GetString() ?? "" : "";
        if (!body.TryGetProperty("update", out var updateElement))
            return Task.CompletedTask;
        return _handler.OnSessionUpdateAsync(sessionId, SessionUpdateParser.Parse(updateElement));
    }

    private static T Parameters<T>(JsonElement? parameters)
    {
        if (parameters is not { ValueKind: JsonValueKind.Object } element)
            throw new JsonRpcException(JsonRpcException.InvalidParams, "Missing request parameters.");
        try
        {
            return element.Deserialize<T>(JsonRpcConnection.SerializerOptions)
                ?? throw new JsonRpcException(JsonRpcException.InvalidParams, "Empty request parameters.");
        }
        catch (JsonException exception)
        {
            throw new JsonRpcException(JsonRpcException.InvalidParams, exception.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_onDispose is not null)
            await _onDispose();
        await _connection.DisposeAsync();
    }
}
