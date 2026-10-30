using System.IO.Pipelines;
using System.Text.Json;
using VibeUi.Acp;
using VibeUi.Acp.JsonRpc;

namespace VibeUi.Acp.Tests;

/// <summary>An in-process ACP agent: the far end of a pair of pipes, driven by per-method scripts.</summary>
public sealed class FakeAgent : IAsyncDisposable
{
    private readonly Pipe _clientToAgent = new();
    private readonly Pipe _agentToClient = new();
    private readonly Dictionary<string, Func<JsonElement?, Task<object?>>> _methods = new();
    private readonly List<(string Method, JsonElement? Parameters)> _notifications = [];
    private JsonRpcConnection? _connection;

    public IReadOnlyList<(string Method, JsonElement? Parameters)> ReceivedNotifications => _notifications;

    public Stream OutputForClient => _agentToClient.Reader.AsStream();

    public Stream InputForClient => _clientToAgent.Writer.AsStream();

    public FakeAgent On(string method, Func<JsonElement?, Task<object?>> handler)
    {
        _methods[method] = handler;
        return this;
    }

    public FakeAgent On(string method, Func<JsonElement?, object?> handler) => On(method, parameters => Task.FromResult(handler(parameters)));

    public void Start()
    {
        _connection = new JsonRpcConnection(_clientToAgent.Reader.AsStream(), _agentToClient.Writer.AsStream(), HandleRequestAsync, (method, parameters) =>
        {
            lock (_notifications)
                _notifications.Add((method, parameters));
            return Task.CompletedTask;
        });
    }

    public AcpClient ConnectClient(IAcpClientHandler handler) => new(OutputForClient, InputForClient, handler);

    public Task<JsonElement> RequestClientAsync(string method, object? parameters) => Connection.RequestAsync(method, parameters);

    public Task NotifyClientAsync(string method, object? parameters) => Connection.NotifyAsync(method, parameters);

    public Task SendUpdateAsync(string sessionId, object update) => NotifyClientAsync("session/update", new { sessionId, update });

    private JsonRpcConnection Connection => _connection ?? throw new InvalidOperationException("Call Start first.");

    private Task<object?> HandleRequestAsync(string method, JsonElement? parameters, CancellationToken cancellationToken)
        => _methods.TryGetValue(method, out var handler)
            ? handler(parameters)
            : throw new JsonRpcException(JsonRpcException.MethodNotFound, $"Unscripted method {method}");

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync();
        await _agentToClient.Writer.CompleteAsync();
        await _clientToAgent.Writer.CompleteAsync();
    }
}

public sealed class RecordingHandler : IAcpClientHandler
{
    public List<(string SessionId, SessionUpdate Update)> Updates { get; } = [];
    public List<RequestPermissionRequest> PermissionRequests { get; } = [];
    public Func<RequestPermissionRequest, Task<RequestPermissionResponse>> PermissionPolicy { get; set; } =
        request => Task.FromResult(new RequestPermissionResponse(PermissionOutcome.Selected(request.Options[0].OptionId)));
    public Dictionary<string, string> Files { get; } = new();

    public Task OnSessionUpdateAsync(string sessionId, SessionUpdate update)
    {
        lock (Updates)
            Updates.Add((sessionId, update));
        return Task.CompletedTask;
    }

    public Task<RequestPermissionResponse> RequestPermissionAsync(RequestPermissionRequest request, CancellationToken cancellationToken)
    {
        lock (PermissionRequests)
            PermissionRequests.Add(request);
        return PermissionPolicy(request);
    }

    public Task<ReadTextFileResponse> ReadTextFileAsync(ReadTextFileRequest request, CancellationToken cancellationToken)
        => Files.TryGetValue(request.Path, out var content)
            ? Task.FromResult(new ReadTextFileResponse(content))
            : throw new JsonRpcException(JsonRpcException.InvalidParams, $"No such file: {request.Path}");

    public Task WriteTextFileAsync(WriteTextFileRequest request, CancellationToken cancellationToken)
    {
        Files[request.Path] = request.Content;
        return Task.CompletedTask;
    }
}
