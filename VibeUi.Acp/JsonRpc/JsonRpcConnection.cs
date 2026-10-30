using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace VibeUi.Acp.JsonRpc;

public delegate Task<object?> JsonRpcRequestHandler(string method, JsonElement? parameters, CancellationToken cancellationToken);

public delegate Task JsonRpcNotificationHandler(string method, JsonElement? parameters);

/// <summary>
/// JSON-RPC 2.0 over newline-delimited JSON frames, the framing ACP agents use on stdio.
/// Outgoing requests are correlated by numeric id. Incoming notifications are delivered one at a
/// time in arrival order; incoming requests run concurrently so a handler that waits for the user
/// (a permission prompt) never stalls the stream.
/// </summary>
public sealed class JsonRpcConnection : IAsyncDisposable
{
    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly byte[] NewLine = [(byte)'\n'];

    private readonly StreamReader _reader;
    private readonly Stream _output;
    private readonly JsonRpcRequestHandler _requests;
    private readonly JsonRpcNotificationHandler _notifications;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly Channel<(string Method, JsonElement? Parameters)> _notificationQueue =
        Channel.CreateUnbounded<(string, JsonElement?)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TaskCompletionSource<Exception?> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _readLoop;
    private readonly Task _notifyLoop;
    private long _nextId;

    public JsonRpcConnection(Stream input, Stream output, JsonRpcRequestHandler requests, JsonRpcNotificationHandler notifications)
    {
        _reader = new StreamReader(input, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 1 << 16);
        _output = output;
        _requests = requests;
        _notifications = notifications;
        _readLoop = Task.Run(ReadLoopAsync);
        _notifyLoop = Task.Run(NotifyLoopAsync);
    }

    /// <summary>Completes when the peer closes its stream; the result is the read failure, if any.</summary>
    public Task<Exception?> Closed => _closed.Task;

    /// <summary>Raised when a notification or request handler throws; the connection keeps running.</summary>
    public event EventHandler<Exception>? HandlerFailed;

    public async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_closed.Task.IsCompleted)
            throw new JsonRpcException(JsonRpcException.ConnectionClosed, "The agent connection is closed.");
        _pending[id] = completion;
        try
        {
            await SendAsync(new OutgoingRequest(id, method, parameters), cancellationToken);
            using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            return await completion.Task;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public Task NotifyAsync(string method, object? parameters, CancellationToken cancellationToken = default)
        => SendAsync(new OutgoingNotification(method, parameters), cancellationToken);

    private async Task SendAsync(object message, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, SerializerOptions);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _output.WriteAsync(bytes, cancellationToken);
            await _output.WriteAsync(NewLine, cancellationToken);
            await _output.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        Exception? failure = null;
        try
        {
            while (true)
            {
                var line = await _reader.ReadLineAsync(_lifetime.Token);
                if (line is null)
                    break;
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                JsonElement message;
                try
                {
                    message = JsonSerializer.Deserialize<JsonElement>(line, SerializerOptions);
                }
                catch (JsonException)
                {
                    continue; // Not a frame; agents occasionally leak diagnostics onto stdout.
                }
                Dispatch(message);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            if (!_lifetime.IsCancellationRequested)
                failure = exception;
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        _notificationQueue.Writer.TryComplete();
        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var pending))
                pending.TrySetException(new JsonRpcException(JsonRpcException.ConnectionClosed, "The agent connection closed before it answered."));
        }
        _closed.TrySetResult(failure);
    }

    private void Dispatch(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object)
            return;
        var hasMethod = message.TryGetProperty("method", out var methodElement) && methodElement.ValueKind == JsonValueKind.String;
        var hasId = message.TryGetProperty("id", out var idElement) && idElement.ValueKind != JsonValueKind.Null;
        JsonElement? parameters = message.TryGetProperty("params", out var parametersElement) ? parametersElement : null;
        if (hasMethod && hasId)
        {
            _ = HandleRequestAsync(idElement, methodElement.GetString()!, parameters);
            return;
        }
        if (hasMethod)
        {
            _notificationQueue.Writer.TryWrite((methodElement.GetString()!, parameters));
            return;
        }
        if (!hasId || idElement.ValueKind != JsonValueKind.Number || !idElement.TryGetInt64(out var id))
            return;
        if (!_pending.TryRemove(id, out var completion))
            return;
        if (message.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var code = error.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var parsedCode)
                ? parsedCode
                : JsonRpcException.InternalError;
            var text = error.TryGetProperty("message", out var messageElement) ? messageElement.GetString() ?? "" : "JSON-RPC error";
            JsonElement? data = error.TryGetProperty("data", out var dataElement) ? dataElement : null;
            completion.TrySetException(new JsonRpcException(code, text, data));
            return;
        }
        completion.TrySetResult(message.TryGetProperty("result", out var result) ? result : default);
    }

    private async Task HandleRequestAsync(JsonElement id, string method, JsonElement? parameters)
    {
        object response;
        try
        {
            var result = await _requests(method, parameters, _lifetime.Token);
            response = new OutgoingResult(id, result ?? EmptyObject.Instance);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (JsonRpcException exception)
        {
            response = new OutgoingError(id, new ErrorBody(exception.Code, exception.Message, exception.ErrorData));
        }
        catch (Exception exception)
        {
            HandlerFailed?.Invoke(this, exception);
            response = new OutgoingError(id, new ErrorBody(JsonRpcException.InternalError, exception.Message, null));
        }
        try
        {
            await SendAsync(response, CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The peer went away; nothing to answer.
        }
    }

    private async Task NotifyLoopAsync()
    {
        await foreach (var (method, parameters) in _notificationQueue.Reader.ReadAllAsync())
        {
            try
            {
                await _notifications(method, parameters);
            }
            catch (Exception exception)
            {
                HandlerFailed?.Invoke(this, exception);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        _notificationQueue.Writer.TryComplete();
        try { _output.Dispose(); } catch (IOException) { }
        _reader.Dispose();
        try { await _readLoop; } catch (Exception) { }
        try { await _notifyLoop; } catch (Exception) { }
        _closed.TrySetResult(null);
        _writeLock.Dispose();
        _lifetime.Dispose();
    }

    private sealed class EmptyObject
    {
        public static readonly EmptyObject Instance = new();
    }

    private sealed record OutgoingRequest(long Id, string Method, [property: JsonPropertyName("params")] object? Parameters)
    {
        [property: JsonPropertyOrder(-1)] public string Jsonrpc => "2.0";
    }

    private sealed record OutgoingNotification(string Method, [property: JsonPropertyName("params")] object? Parameters)
    {
        [property: JsonPropertyOrder(-1)] public string Jsonrpc => "2.0";
    }

    private sealed record OutgoingResult(JsonElement Id, object Result)
    {
        [property: JsonPropertyOrder(-1)] public string Jsonrpc => "2.0";
    }

    private sealed record OutgoingError(JsonElement Id, ErrorBody Error)
    {
        [property: JsonPropertyOrder(-1)] public string Jsonrpc => "2.0";
    }

    private sealed record ErrorBody(int Code, string Message, JsonElement? Data);
}
