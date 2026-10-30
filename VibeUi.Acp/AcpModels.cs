using System.Text.Json;
using System.Text.Json.Serialization;

namespace VibeUi.Acp;

// Wire types for the Agent Client Protocol subset Vibe UI uses (schema v1). Property names follow
// the camelCase JSON; discriminator values stay snake_case strings so new agent values pass through.

public sealed record Implementation(string Name, string Version, string? Title = null);

public sealed record FileSystemCapabilities(bool ReadTextFile, bool WriteTextFile);

public sealed record ClientCapabilities(
    FileSystemCapabilities Fs,
    bool Terminal = false,
    [property: JsonPropertyName("_meta")] IReadOnlyDictionary<string, object?>? Meta = null);

public sealed record InitializeRequest(int ProtocolVersion, ClientCapabilities ClientCapabilities, Implementation ClientInfo);

public sealed record AgentCapabilities(bool LoadSession = false, JsonElement? SessionCapabilities = null)
{
    private bool Supports(string capability)
        => SessionCapabilities is { ValueKind: JsonValueKind.Object } caps && caps.TryGetProperty(capability, out _);

    public bool CanListSessions => Supports("list");
    public bool CanCloseSessions => Supports("close");
}

public sealed record AuthMethod(string Id, string Name, string? Description = null);

public sealed record InitializeResponse(
    int ProtocolVersion,
    AgentCapabilities? AgentCapabilities = null,
    IReadOnlyList<AuthMethod>? AuthMethods = null,
    Implementation? AgentInfo = null);

public sealed record AuthenticateResponse([property: JsonPropertyName("_meta")] JsonElement? Meta = null);

public sealed record NewSessionRequest(string Cwd, IReadOnlyList<object> McpServers);

public sealed record SessionMode(string Id, string Name, string? Description = null);

public sealed record SessionModeState(string CurrentModeId, IReadOnlyList<SessionMode> AvailableModes);

public sealed record ConfigOptionChoice(string Value, string Name, string? Description = null);

public sealed record SessionConfigOption(
    string Id,
    string Name,
    string Type,
    JsonElement CurrentValue,
    string? Category = null,
    IReadOnlyList<ConfigOptionChoice>? Options = null)
{
    public string? CurrentValueText => CurrentValue.ValueKind switch
    {
        JsonValueKind.String => CurrentValue.GetString(),
        JsonValueKind.Undefined or JsonValueKind.Null => null,
        _ => CurrentValue.GetRawText(),
    };
}

public sealed record NewSessionResponse(
    string SessionId,
    SessionModeState? Modes = null,
    IReadOnlyList<SessionConfigOption>? ConfigOptions = null,
    [property: JsonPropertyName("_meta")] JsonElement? Meta = null)
{
    /// <summary>
    /// Mistral Vibe reports <c>_meta.workspace_trust.status</c> ("trusted" or "untrusted"). A folder with
    /// nothing to trust (no AGENTS.md, no .vibe config) is "untrusted" without <c>details</c> and offers no
    /// decision, so that case is reported as null: there is nothing for the user to do.
    /// </summary>
    public string? WorkspaceTrustStatus => ReadWorkspaceTrust(Meta);

    internal static string? ReadWorkspaceTrust(JsonElement? meta)
    {
        if (meta is not { ValueKind: JsonValueKind.Object } element
            || !element.TryGetProperty("workspace_trust", out var trust)
            || trust.ValueKind != JsonValueKind.Object
            || !trust.TryGetProperty("status", out var status))
            return null;
        var value = status.GetString();
        if (value == "untrusted" && (!trust.TryGetProperty("details", out var details) || details.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined))
            return null;
        return value;
    }
}

/// <summary>Resumes an earlier session; the agent replays its history as <c>session/update</c> notifications first.</summary>
public sealed record LoadSessionRequest(string SessionId, string Cwd, IReadOnlyList<object> McpServers);

public sealed record LoadSessionResponse(
    SessionModeState? Modes = null,
    IReadOnlyList<SessionConfigOption>? ConfigOptions = null,
    [property: JsonPropertyName("_meta")] JsonElement? Meta = null)
{
    public string? WorkspaceTrustStatus => NewSessionResponse.ReadWorkspaceTrust(Meta);
}

public sealed record ListSessionsRequest(string? Cwd = null);

public sealed record SessionInfo(string SessionId, string Cwd, string? Title = null, string? UpdatedAt = null);

public sealed record ListSessionsResponse(IReadOnlyList<SessionInfo> Sessions);

public sealed record ContentBlock(string Type, string? Text = null)
{
    public static ContentBlock FromText(string text) => new("text", text);
}

public sealed record PromptRequest(string SessionId, IReadOnlyList<ContentBlock> Prompt);

public sealed record Usage(long TotalTokens, long InputTokens, long OutputTokens);

public sealed record PromptResponse(string StopReason, Usage? Usage = null);

public static class StopReasons
{
    public const string EndTurn = "end_turn";
    public const string MaxTokens = "max_tokens";
    public const string MaxTurnRequests = "max_turn_requests";
    public const string Refusal = "refusal";
    public const string Cancelled = "cancelled";
}

public sealed record CancelNotification(string SessionId);

public sealed record SetSessionModeRequest(string SessionId, string ModeId);

public sealed record SetSessionConfigOptionRequest(string SessionId, string ConfigId, string Value);

public sealed record SetSessionConfigOptionResponse(IReadOnlyList<SessionConfigOption> ConfigOptions);

public sealed record CloseSessionRequest(string SessionId);

public sealed record ToolCallLocation(string Path, int? Line = null);

/// <summary>One tool-call content item: <c>content</c> (a block), <c>diff</c> (path/oldText/newText) or <c>terminal</c>.</summary>
public sealed record ToolCallContent(
    string Type,
    ContentBlock? Content = null,
    string? Path = null,
    string? OldText = null,
    string? NewText = null,
    string? TerminalId = null);

public sealed record ToolCallUpdate(
    string ToolCallId,
    string? Title = null,
    string? Kind = null,
    string? Status = null,
    IReadOnlyList<ToolCallContent>? Content = null,
    IReadOnlyList<ToolCallLocation>? Locations = null,
    JsonElement? RawInput = null,
    JsonElement? RawOutput = null);

public static class ToolCallStatuses
{
    public const string Pending = "pending";
    public const string InProgress = "in_progress";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

public sealed record PermissionOption(string OptionId, string Name, string Kind);

public static class PermissionOptionKinds
{
    public const string AllowOnce = "allow_once";
    public const string AllowAlways = "allow_always";
    public const string RejectOnce = "reject_once";
    public const string RejectAlways = "reject_always";
}

public sealed record RequestPermissionRequest(string SessionId, ToolCallUpdate ToolCall, IReadOnlyList<PermissionOption> Options);

public sealed record PermissionOutcome(string Outcome, string? OptionId = null)
{
    public static PermissionOutcome Selected(string optionId) => new("selected", optionId);
    public static PermissionOutcome Cancelled { get; } = new("cancelled");
}

public sealed record RequestPermissionResponse(PermissionOutcome Outcome);

public sealed record ReadTextFileRequest(string SessionId, string Path, int? Line = null, int? Limit = null);

public sealed record ReadTextFileResponse(string Content);

public sealed record WriteTextFileRequest(string SessionId, string Path, string Content);

public sealed record PlanEntry(string Content, string Priority, string Status);

public sealed record Cost(double Amount, string Currency);

public sealed record AvailableCommand(string Name, string Description);

/// <summary>A <c>session/update</c> payload, discriminated by its <c>sessionUpdate</c> value.</summary>
public abstract record SessionUpdate(string Kind);

public static class SessionUpdateKinds
{
    public const string UserMessageChunk = "user_message_chunk";
    public const string AgentMessageChunk = "agent_message_chunk";
    public const string AgentThoughtChunk = "agent_thought_chunk";
    public const string ToolCall = "tool_call";
    public const string ToolCallUpdate = "tool_call_update";
    public const string Plan = "plan";
    public const string CurrentMode = "current_mode_update";
    public const string ConfigOptions = "config_option_update";
    public const string SessionInfo = "session_info_update";
    public const string Usage = "usage_update";
    public const string AvailableCommands = "available_commands_update";
}

public sealed record MessageChunkUpdate(string Kind, ContentBlock Content, string? MessageId = null) : SessionUpdate(Kind);

public sealed record ToolCallSessionUpdate(string Kind, ToolCallUpdate ToolCall) : SessionUpdate(Kind);

public sealed record PlanSessionUpdate(IReadOnlyList<PlanEntry> Entries) : SessionUpdate(SessionUpdateKinds.Plan);

public sealed record CurrentModeSessionUpdate(string CurrentModeId) : SessionUpdate(SessionUpdateKinds.CurrentMode);

public sealed record ConfigOptionsSessionUpdate(IReadOnlyList<SessionConfigOption> ConfigOptions) : SessionUpdate(SessionUpdateKinds.ConfigOptions);

public sealed record SessionInfoSessionUpdate(string? Title) : SessionUpdate(SessionUpdateKinds.SessionInfo);

public sealed record UsageSessionUpdate(long Used, long Size, Cost? Cost = null) : SessionUpdate(SessionUpdateKinds.Usage);

public sealed record AvailableCommandsSessionUpdate(IReadOnlyList<AvailableCommand> AvailableCommands) : SessionUpdate(SessionUpdateKinds.AvailableCommands);

public sealed record UnknownSessionUpdate(string Kind, JsonElement Raw) : SessionUpdate(Kind);

public static class SessionUpdateParser
{
    private static readonly JsonSerializerOptions Options = JsonRpc.JsonRpcConnection.SerializerOptions;

    public static SessionUpdate Parse(JsonElement update)
    {
        var kind = update.TryGetProperty("sessionUpdate", out var kindElement) ? kindElement.GetString() ?? "" : "";
        switch (kind)
        {
            case SessionUpdateKinds.UserMessageChunk:
            case SessionUpdateKinds.AgentMessageChunk:
            case SessionUpdateKinds.AgentThoughtChunk:
            {
                var chunk = Deserialize<ContentChunk>(update);
                return new MessageChunkUpdate(kind, chunk.Content, chunk.MessageId);
            }
            case SessionUpdateKinds.ToolCall:
            case SessionUpdateKinds.ToolCallUpdate:
                return new ToolCallSessionUpdate(kind, Deserialize<ToolCallUpdate>(update));
            case SessionUpdateKinds.Plan:
                return new PlanSessionUpdate(Deserialize<PlanBody>(update).Entries ?? []);
            case SessionUpdateKinds.CurrentMode:
                return new CurrentModeSessionUpdate(Deserialize<CurrentModeBody>(update).CurrentModeId);
            case SessionUpdateKinds.ConfigOptions:
                return new ConfigOptionsSessionUpdate(Deserialize<ConfigOptionsBody>(update).ConfigOptions ?? []);
            case SessionUpdateKinds.SessionInfo:
                return new SessionInfoSessionUpdate(Deserialize<SessionInfoBody>(update).Title);
            case SessionUpdateKinds.Usage:
            {
                var usage = Deserialize<UsageBody>(update);
                return new UsageSessionUpdate(usage.Used, usage.Size, usage.Cost);
            }
            case SessionUpdateKinds.AvailableCommands:
                return new AvailableCommandsSessionUpdate(Deserialize<AvailableCommandsBody>(update).AvailableCommands ?? []);
            default:
                return new UnknownSessionUpdate(kind, update);
        }
    }

    private static T Deserialize<T>(JsonElement element)
        => element.Deserialize<T>(Options) ?? throw new JsonException($"Could not read session update as {typeof(T).Name}.");

    private sealed record ContentChunk(ContentBlock Content, string? MessageId = null);

    private sealed record PlanBody(IReadOnlyList<PlanEntry>? Entries);

    private sealed record CurrentModeBody(string CurrentModeId);

    private sealed record ConfigOptionsBody(IReadOnlyList<SessionConfigOption>? ConfigOptions);

    private sealed record SessionInfoBody(string? Title);

    private sealed record UsageBody(long Used, long Size, Cost? Cost = null);

    private sealed record AvailableCommandsBody(IReadOnlyList<AvailableCommand>? AvailableCommands);
}
