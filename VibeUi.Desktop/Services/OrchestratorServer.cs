using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VibeUi.Desktop.Services;

/// <summary>What an orchestrator can see of a delegated task.</summary>
public sealed record TaskInfo(string TaskId, string Title, string Status, string Project, string Workspace, string? Branch, string Reply, string? StopReason);

/// <summary>The application side of the orchestrator tools; every call may come from a worker thread.</summary>
public interface IOrchestratorHost
{
    Task<TaskInfo> DelegateAsync(string orchestratorId, string title, string prompt, bool worktree, string? model, string? project, CancellationToken cancellationToken);

    Task<TaskInfo?> WaitAsync(string orchestratorId, string taskId, TimeSpan timeout, CancellationToken cancellationToken);

    Task<TaskInfo?> GetAsync(string orchestratorId, string taskId);

    Task<IReadOnlyList<TaskInfo>> ListAsync(string orchestratorId);

    Task<TaskInfo?> SendAsync(string orchestratorId, string taskId, string prompt, CancellationToken cancellationToken);

    Task<TaskInfo?> CancelAsync(string orchestratorId, string taskId);

    /// <summary>Runs a vibe.re action in a project and returns its exit code and output.</summary>
    Task<ActionOutcome> RunActionAsync(string orchestratorId, string actionName, string? project, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProjectAction>> ListActionsAsync(string orchestratorId, string? project);

    /// <summary>Ranked, offline code search over a project.</summary>
    Task<IReadOnlyList<SearchHit>> SearchCodeAsync(string orchestratorId, string query, string? project, int maxResults, CancellationToken cancellationToken);

    /// <summary>GitHub, through gh: open pull requests, the checks of one, or a new one for the current branch.</summary>
    Task<object> GitHubAsync(string orchestratorId, string operation, string? project, IReadOnlyDictionary<string, string?> arguments, CancellationToken cancellationToken);
}

/// <summary>
/// A small MCP server (Streamable HTTP, JSON responses) on localhost. An orchestrator thread's Vibe
/// session gets it as an MCP server, which gives the model tools to delegate work to child threads,
/// wait for them and read their results. The path names the orchestrator: <c>/mcp/{threadId}</c>.
/// </summary>
public sealed class OrchestratorServer : IAsyncDisposable
{
    public const string ServerName = "vibe-ui-orchestrator";
    private const string DefaultProtocolVersion = "2025-06-18";
    private static readonly HashSet<string> KnownProtocolVersions = ["2024-11-05", "2025-03-26", "2025-06-18", "2025-11-25"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IOrchestratorHost _host;
    private readonly CancellationTokenSource _stopping = new();
    private HttpListener? _listener;
    private Task? _loop;

    public OrchestratorServer(IOrchestratorHost host) => _host = host;

    public Uri? BaseUri { get; private set; }

    public bool IsRunning => _listener is { IsListening: true };

    public void Start()
    {
        if (_listener is not null)
            return;
        var random = new Random();
        for (var attempt = 0; attempt < 25; attempt++)
        {
            var port = random.Next(20000, 60000);
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
            }
            catch (HttpListenerException)
            {
                continue;
            }
            _listener = listener;
            BaseUri = new Uri($"http://127.0.0.1:{port}/");
            _loop = Task.Run(() => AcceptLoopAsync(listener));
            return;
        }
        throw new InvalidOperationException("No free localhost port for the orchestrator server.");
    }

    /// <summary>The ACP <c>mcpServers</c> entry that connects a session to this server as the given orchestrator.</summary>
    public object McpServerFor(string orchestratorId)
    {
        if (BaseUri is null)
            Start();
        return new { type = "http", name = ServerName, url = new Uri(BaseUri!, $"mcp/{orchestratorId}").ToString(), headers = Array.Empty<object>() };
    }

    private async Task AcceptLoopAsync(HttpListener listener)
    {
        while (listener.IsListening && !_stopping.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        try
        {
            var segments = request.Url?.AbsolutePath.Trim('/').Split('/') ?? [];
            if (segments.Length != 2 || segments[0] != "mcp")
            {
                response.StatusCode = 404;
                return;
            }
            var orchestratorId = segments[1];
            switch (request.HttpMethod)
            {
                case "POST":
                    break;
                case "DELETE":
                    response.StatusCode = 200;
                    return;
                default:
                    response.StatusCode = 405;
                    response.Headers["Allow"] = "POST, DELETE";
                    return;
            }
            string body;
            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
                body = await reader.ReadToEndAsync(_stopping.Token);
            var message = JsonNode.Parse(body);
            if (message is JsonArray batch)
            {
                var results = new JsonArray();
                foreach (var item in batch.ToList())
                    if (item is JsonObject single && await HandleMessageAsync(orchestratorId, single, _stopping.Token) is { } answer)
                        results.Add(answer);
                await WriteAsync(response, results.Count == 0 ? null : results);
                return;
            }
            if (message is not JsonObject request1)
            {
                response.StatusCode = 400;
                return;
            }
            await WriteAsync(response, await HandleMessageAsync(orchestratorId, request1, _stopping.Token));
        }
        catch (Exception exception) when (exception is JsonException or IOException or OperationCanceledException or HttpListenerException or ObjectDisposedException)
        {
            try
            {
                response.StatusCode = 400;
            }
            catch (ObjectDisposedException)
            {
            }
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch (Exception exception) when (exception is ObjectDisposedException or HttpListenerException or InvalidOperationException)
            {
            }
        }
    }

    private static async Task WriteAsync(HttpListenerResponse response, JsonNode? result)
    {
        if (result is null)
        {
            response.StatusCode = 202;
            return;
        }
        var bytes = Encoding.UTF8.GetBytes(result.ToJsonString());
        response.StatusCode = 200;
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }

    /// <summary>Handles one JSON-RPC message. Notifications return null (HTTP 202).</summary>
    public async Task<JsonObject?> HandleMessageAsync(string orchestratorId, JsonObject message, CancellationToken cancellationToken)
    {
        var id = message["id"]?.DeepClone();
        var method = message["method"]?.GetValue<string>() ?? "";
        var parameters = message["params"] as JsonObject;
        if (id is null)
            return null; // notifications/initialized, notifications/cancelled: nothing to answer.
        try
        {
            JsonNode? result = method switch
            {
                "initialize" => Initialize(parameters),
                "ping" => new JsonObject(),
                "tools/list" => new JsonObject { ["tools"] = ToolDefinitions() },
                "tools/call" => await CallToolAsync(orchestratorId, parameters, cancellationToken),
                "resources/list" => new JsonObject { ["resources"] = new JsonArray() },
                "prompts/list" => new JsonObject { ["prompts"] = new JsonArray() },
                _ => throw new McpError(-32601, $"Method not found: {method}"),
            };
            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
        }
        catch (McpError error)
        {
            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = error.Code, ["message"] = error.Message } };
        }
    }

    private static JsonObject Initialize(JsonObject? parameters)
    {
        var requested = parameters?["protocolVersion"]?.GetValue<string>();
        return new JsonObject
        {
            ["protocolVersion"] = requested is not null && KnownProtocolVersions.Contains(requested) ? requested : DefaultProtocolVersion,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["version"] = "1.0.0" },
            ["instructions"] = "Delegate independent pieces of work to child threads with delegate_task, then wait_for_task to collect each result. Children run in the same project (or an isolated git worktree when asked) and see your prompt only, so make each prompt self-contained.",
        };
    }

    private async Task<JsonObject> CallToolAsync(string orchestratorId, JsonObject? parameters, CancellationToken cancellationToken)
    {
        var name = parameters?["name"]?.GetValue<string>() ?? throw new McpError(-32602, "Missing tool name.");
        var arguments = parameters["arguments"] as JsonObject ?? new JsonObject();
        string Required(string key) => arguments[key]?.GetValue<string>() is { Length: > 0 } value ? value : throw new McpError(-32602, $"Missing argument: {key}");
        try
        {
            object? result = name switch
            {
                "delegate_task" => await _host.DelegateAsync(orchestratorId, Required("title"), Required("prompt"),
                    arguments["worktree"]?.GetValue<bool>() ?? false, arguments["model"]?.GetValue<string>(), arguments["project"]?.GetValue<string>(), cancellationToken),
                "wait_for_task" => await _host.WaitAsync(orchestratorId, Required("task_id"),
                    TimeSpan.FromSeconds(Math.Clamp(arguments["timeout_seconds"]?.GetValue<double>() ?? 600, 1, 3600)), cancellationToken)
                    ?? throw new McpError(-32602, "Unknown task_id."),
                "check_task" => await _host.GetAsync(orchestratorId, Required("task_id")) ?? throw new McpError(-32602, "Unknown task_id."),
                "list_tasks" => await _host.ListAsync(orchestratorId),
                "send_to_task" => await _host.SendAsync(orchestratorId, Required("task_id"), Required("prompt"), cancellationToken) ?? throw new McpError(-32602, "Unknown task_id."),
                "cancel_task" => await _host.CancelAsync(orchestratorId, Required("task_id")) ?? throw new McpError(-32602, "Unknown task_id."),
                "run_action" => await _host.RunActionAsync(orchestratorId, Required("name"), arguments["project"]?.GetValue<string>(), cancellationToken),
                "list_actions" => await _host.ListActionsAsync(orchestratorId, arguments["project"]?.GetValue<string>()),
                "search_code" => await _host.SearchCodeAsync(orchestratorId, Required("query"), arguments["project"]?.GetValue<string>(),
                    (int)Math.Clamp(arguments["max_results"]?.GetValue<double>() ?? 10, 1, 30), cancellationToken),
                "list_pull_requests" => await _host.GitHubAsync(orchestratorId, "list", arguments["project"]?.GetValue<string>(), Strings(arguments), cancellationToken),
                "pull_request_checks" => await _host.GitHubAsync(orchestratorId, "checks", arguments["project"]?.GetValue<string>(), Strings(arguments), cancellationToken),
                "create_pull_request" => await _host.GitHubAsync(orchestratorId, "create", arguments["project"]?.GetValue<string>(), Strings(arguments), cancellationToken),
                _ => throw new McpError(-32602, $"Unknown tool: {name}"),
            };
            return ToolResult(JsonSerializer.Serialize(result, Json), isError: false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            return ToolResult(exception.Message, isError: true);
        }
    }

    private static IReadOnlyDictionary<string, string?> Strings(JsonObject arguments)
        => arguments.ToDictionary(pair => pair.Key, pair => pair.Value?.ToString());

    private static JsonObject ToolResult(string text, bool isError)
        => new()
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["isError"] = isError,
        };

    public static JsonArray ToolDefinitions() => new(
        Tool("delegate_task",
            "Start a child thread that works on one self-contained task in this project and returns immediately with its task_id. The child is a separate Mistral Vibe session: it does not see this conversation, so put every detail it needs in the prompt. Set worktree=true to give it an isolated git worktree on a new branch, so several tasks can edit files in parallel.",
            new JsonObject
            {
                ["title"] = Property("string", "Short label for the task, shown in the sidebar."),
                ["prompt"] = Property("string", "Complete instructions for the child thread."),
                ["worktree"] = Property("boolean", "Run in a fresh git worktree and branch instead of the project folder. Default false."),
                ["model"] = Property("string", "Optional model id for the child; defaults to this thread's model."),
                ["project"] = Property("string", "Project name or folder path to run the task in. Required for the Chief of Staff, which spans projects; ignored inside a project orchestrator."),
            }, ["title", "prompt"]),
        Tool("wait_for_task",
            "Block until a delegated task finishes its current turn (or the timeout passes) and return its status and final reply.",
            new JsonObject
            {
                ["task_id"] = Property("string", "The task_id from delegate_task."),
                ["timeout_seconds"] = Property("number", "How long to wait at most. Default 600. Returns status 'running' when the time runs out."),
            }, ["task_id"]),
        Tool("check_task", "Return a task's current status and latest reply without waiting.",
            new JsonObject { ["task_id"] = Property("string", "The task_id from delegate_task.") }, ["task_id"]),
        Tool("list_tasks", "List every task this orchestrator delegated, with status.", new JsonObject(), []),
        Tool("send_to_task", "Send a follow-up prompt to a finished task's thread, keeping its context. Returns immediately; use wait_for_task afterwards.",
            new JsonObject
            {
                ["task_id"] = Property("string", "The task_id from delegate_task."),
                ["prompt"] = Property("string", "The follow-up instructions."),
            }, ["task_id", "prompt"]),
        Tool("cancel_task", "Stop a running task.", new JsonObject { ["task_id"] = Property("string", "The task_id from delegate_task.") }, ["task_id"]),
        Tool("list_actions", "List the project's declared actions (from vibe.re): build, test, dev commands you can run with run_action.",
            new JsonObject { ["project"] = Property("string", "Project name; required for the Chief of Staff.") }, []),
        Tool("run_action", "Run one of the project's declared actions and return its exit code and output. Use it to build or test after tasks finish.",
            new JsonObject
            {
                ["name"] = Property("string", "The action name from list_actions."),
                ["project"] = Property("string", "Project name; required for the Chief of Staff."),
            }, ["name"]),
        Tool("search_code", "Find the files most relevant to a question or identifiers, ranked by a local index of the project (no network). Returns paths with matching lines. Good first step before delegating: name the files in the task prompt.",
            new JsonObject
            {
                ["query"] = Property("string", "Words or identifiers to look for, e.g. 'session trust decision' or 'ApplyConfigOptions'."),
                ["max_results"] = Property("number", "How many files to return. Default 10."),
                ["project"] = Property("string", "Project name; required for the Chief of Staff."),
            }, ["query"]),
        Tool("list_pull_requests", "Open pull requests of the project's GitHub repository.",
            new JsonObject { ["project"] = Property("string", "Project name; required for the Chief of Staff.") }, []),
        Tool("pull_request_checks", "CI check results and review state of a pull request.",
            new JsonObject
            {
                ["number"] = Property("string", "The pull request number."),
                ["project"] = Property("string", "Project name; required for the Chief of Staff."),
            }, ["number"]),
        Tool("create_pull_request", "Open a pull request for the project's current branch (pushes first when needed).",
            new JsonObject
            {
                ["title"] = Property("string", "Pull request title."),
                ["body"] = Property("string", "Pull request description (markdown)."),
                ["draft"] = Property("boolean", "Open as a draft. Default false."),
                ["project"] = Property("string", "Project name; required for the Chief of Staff."),
            }, ["title"]));

    private static JsonObject Tool(string name, string description, JsonObject properties, string[] required)
        => new()
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JsonArray(required.Select(item => (JsonNode)item).ToArray()),
                ["additionalProperties"] = false,
            },
        };

    private static JsonObject Property(string type, string description) => new() { ["type"] = type, ["description"] = description };

    private sealed class McpError(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        if (_listener is { } listener)
        {
            try
            {
                listener.Close();
            }
            catch (Exception)
            {
                // The managed listener can throw while unregistering its prefix on shutdown; nothing is left to clean up.
            }
        }
        if (_loop is { } loop)
        {
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
            {
            }
        }
        _stopping.Dispose();
    }
}
