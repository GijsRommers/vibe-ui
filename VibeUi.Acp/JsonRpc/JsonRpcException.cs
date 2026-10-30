using System.Text.Json;

namespace VibeUi.Acp.JsonRpc;

/// <summary>An error returned by the peer, or a local failure reported with a JSON-RPC error code.</summary>
public sealed class JsonRpcException(int code, string message, JsonElement? data = null) : Exception(message)
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;

    /// <summary>ACP: the agent needs <c>authenticate</c> before it can serve the request.</summary>
    public const int AuthenticationRequired = -32000;

    /// <summary>Local: the connection closed before a response arrived.</summary>
    public const int ConnectionClosed = -32099;

    public int Code { get; } = code;
    public JsonElement? ErrorData { get; } = data;
}
