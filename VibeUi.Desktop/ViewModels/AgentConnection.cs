using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using VibeUi.Acp;
using VibeUi.Acp.JsonRpc;

namespace VibeUi.Desktop.ViewModels;

public enum ConnectionState
{
    NotInstalled,
    Disconnected,
    Starting,
    SignInRequired,
    SigningIn,
    Ready,
    Failed,
}

public delegate IAcpClient AgentFactory(string executable, string workspace, IAcpClientHandler handler, Action<string> standardError);

/// <summary>Where one session's traffic goes: a thread. Everything here is called on the UI thread.</summary>
public interface ISessionSink
{
    void ApplyUpdate(SessionUpdate update);

    Task<RequestPermissionResponse> RequestPermissionAsync(RequestPermissionRequest request, CancellationToken cancellationToken);

    void OnConnectionLost(string reason);
}

/// <summary>
/// One <c>vibe-acp</c> process for one project. Threads open sessions on it and register as sinks;
/// the connection routes updates and permission prompts to the right thread by session id.
/// </summary>
public sealed partial class AgentConnection : ObservableObject, IAcpClientHandler, IAsyncDisposable
{
    public const string ClientName = "vibe-ui";
    public const string DelegatedBrowserAuth = "browser-auth-delegated";
    public const string TrustDecideMethod = "_trust/decision";
    public const string SignOutMethod = "_auth/signOut";
    public const string SetTitleMethod = "_session/set_title";
    public const string DeleteSessionMethod = "_session/delete";
    private const int MaxLogLines = 300;

    private readonly Func<string?> _locateAgent;
    private readonly AgentFactory _startAgent;
    private readonly Action<string> _openUrl;
    private readonly Action<Action> _dispatch;
    private readonly string _clientVersion;
    private readonly Dictionary<string, ISessionSink> _sessions = new();

    private IAcpClient? _client;
    private Task<bool>? _connecting;
    private IReadOnlyList<AuthMethod> _authMethods = [];
    private AgentCapabilities _capabilities = new();
    private int _generation;

    public AgentConnection(string workspace, Func<string?> locateAgent, AgentFactory startAgent, Action<string> openUrl, Action<Action> dispatch, string clientVersion)
    {
        Workspace = workspace;
        _locateAgent = locateAgent;
        _startAgent = startAgent;
        _openUrl = openUrl;
        _dispatch = dispatch;
        _clientVersion = clientVersion;
    }

    public string Workspace { get; }
    public ObservableCollection<string> AgentLog { get; } = [];
    public string InstallCommand => VibeAcpLauncher.InstallCommand;
    public IAcpClient? Client => _client;
    public bool CanLoadSessions => _capabilities.LoadSession;

    /// <summary>Raised on the UI thread after a successful sign-in, so waiting threads can open their sessions.</summary>
    public event EventHandler? SignedIn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInstalled), nameof(NeedsSignIn), nameof(IsSigningIn), nameof(IsStarting), nameof(IsReady), nameof(HasFailed), nameof(CanSignIn), nameof(CanReconnect))]
    private ConnectionState _state = ConnectionState.Disconnected;

    [ObservableProperty] private string _status = "Not connected.";
    [ObservableProperty] private string _agentDescription = "Mistral Vibe";

    /// <summary>The version the agent announced in <c>initialize</c>, once connected.</summary>
    [ObservableProperty] private string? _agentVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSignInUrl))]
    private string? _signInUrl;

    public bool IsInstalled => State != ConnectionState.NotInstalled;
    public bool NeedsSignIn => State is ConnectionState.SignInRequired or ConnectionState.SigningIn;
    public bool IsSigningIn => State == ConnectionState.SigningIn;
    public bool IsStarting => State == ConnectionState.Starting;
    public bool IsReady => State == ConnectionState.Ready;
    public bool HasFailed => State is ConnectionState.Failed or ConnectionState.Disconnected;
    public bool CanSignIn => State == ConnectionState.SignInRequired;
    public bool CanReconnect => State is ConnectionState.Disconnected or ConnectionState.Failed or ConnectionState.NotInstalled;
    public bool HasSignInUrl => !string.IsNullOrEmpty(SignInUrl);

    /// <summary>Starts and initializes the agent if needed. True when sessions can be opened (sign-in may still be required).</summary>
    public Task<bool> EnsureConnectedAsync()
    {
        if (_client is not null && State is ConnectionState.Ready or ConnectionState.SignInRequired or ConnectionState.SigningIn)
            return Task.FromResult(true);
        if (_connecting is { IsCompleted: false } pending)
            return pending;
        return _connecting = ConnectAsync();
    }

    private async Task<bool> ConnectAsync()
    {
        {
            await CloseClientAsync("Reconnecting.");
            var executable = _locateAgent();
            if (executable is null)
            {
                State = ConnectionState.NotInstalled;
                Status = "Mistral Vibe is not installed, or vibe-acp is not on your PATH.";
                return false;
            }
            State = ConnectionState.Starting;
            Status = "Starting Mistral Vibe…";
            var generation = ++_generation;
            try
            {
                var client = _startAgent(executable, Workspace, this, line => _dispatch(() => AppendLog(line)));
                _client = client;
                _ = client.Closed.ContinueWith(task => _dispatch(() => OnAgentClosed(generation, task.IsCompletedSuccessfully ? task.Result : task.Exception)),
                    TaskScheduler.Default);
                var capabilities = new ClientCapabilities(
                    new FileSystemCapabilities(ReadTextFile: true, WriteTextFile: true),
                    Terminal: false,
                    Meta: new Dictionary<string, object?> { [DelegatedBrowserAuth] = true });
                var initialized = await client.InitializeAsync(new InitializeRequest(AcpClient.ProtocolVersion, capabilities, new Implementation(ClientName, _clientVersion, "Vibe UI")));
                if (generation != _generation)
                    return false;
                _authMethods = initialized.AuthMethods ?? [];
                _capabilities = initialized.AgentCapabilities ?? new AgentCapabilities();
                var info = initialized.AgentInfo;
                AgentDescription = info is null ? "Agent" : $"{info.Title ?? info.Name} {info.Version}".Trim();
                AgentVersion = info?.Version;
                State = ConnectionState.Ready;
                Status = $"{AgentDescription} is ready.";
                return true;
            }
            catch (Exception exception) when (generation == _generation && exception is not OperationCanceledException)
            {
                State = ConnectionState.Failed;
                Status = $"Could not start Mistral Vibe: {exception.Message}";
                return false;
            }
        }
    }

    public Task ReconnectAsync() => EnsureConnectedAsync();

    public void Register(string sessionId, ISessionSink sink) => _sessions[sessionId] = sink;

    public void Unregister(string sessionId) => _sessions.Remove(sessionId);

    /// <summary>A thread hit ACP error -32000 opening a session: credentials are missing.</summary>
    public void MarkSignInRequired()
    {
        if (State is ConnectionState.SigningIn)
            return;
        State = ConnectionState.SignInRequired;
        Status = "Sign in to Mistral to start.";
    }

    /// <summary>Another connection completed the sign-in; the stored key now serves this one as well.</summary>
    public void MarkSignedIn()
    {
        if (State != ConnectionState.SignInRequired)
            return;
        State = ConnectionState.Ready;
        Status = $"{AgentDescription} is ready.";
    }

    public async Task SignInAsync()
    {
        if (_client is null || State != ConnectionState.SignInRequired)
            return;
        var client = _client;
        State = ConnectionState.SigningIn;
        SignInUrl = null;
        try
        {
            if (_authMethods.Any(method => method.Id == DelegatedBrowserAuth))
            {
                Status = "Opening the Mistral sign-in page in your browser…";
                var started = await client.AuthenticateAsync(DelegatedBrowserAuth, new Dictionary<string, object?> { ["action"] = "start" });
                var attempt = started.Meta is { ValueKind: JsonValueKind.Object } meta && meta.TryGetProperty(DelegatedBrowserAuth, out var details) ? details : default;
                var url = attempt.ValueKind == JsonValueKind.Object && attempt.TryGetProperty("signInUrl", out var urlElement) ? urlElement.GetString() : null;
                var attemptId = attempt.ValueKind == JsonValueKind.Object && attempt.TryGetProperty("attemptId", out var idElement) ? idElement.GetString() : null;
                SignInUrl = url;
                if (url is not null)
                    _openUrl(url);
                Status = "Finish signing in in your browser. This window continues automatically.";
                await client.AuthenticateAsync(DelegatedBrowserAuth, new Dictionary<string, object?> { ["action"] = "complete", ["attemptId"] = attemptId });
            }
            else if (_authMethods.FirstOrDefault() is { } method)
            {
                Status = $"{method.Name}…";
                await client.AuthenticateAsync(method.Id);
            }
            else
            {
                State = ConnectionState.SignInRequired;
                Status = "Mistral Vibe offers no sign-in method here. Run `vibe --setup` in a terminal, then connect again.";
                return;
            }
            if (client != _client)
                return;
            SignInUrl = null;
            State = ConnectionState.Ready;
            Status = "Signed in.";
            SignedIn?.Invoke(this, EventArgs.Empty);
        }
        catch (JsonRpcException exception) when (client == _client)
        {
            State = ConnectionState.SignInRequired;
            Status = $"Sign-in failed: {exception.Message}";
            SignInUrl = null;
        }
    }

    public void OpenSignInUrl()
    {
        if (SignInUrl is { } url)
            _openUrl(url);
    }

    /// <summary>Removes the stored Mistral credentials. Open sessions on this agent stop working.</summary>
    public async Task<bool> SignOutAsync()
    {
        if (_client is null)
            return false;
        var client = _client;
        try
        {
            await client.ExtensionRequestAsync(SignOutMethod, new { });
        }
        catch (JsonRpcException exception)
        {
            Status = $"Could not sign out: {exception.Message}";
            return false;
        }
        if (client != _client)
            return false;
        foreach (var sink in _sessions.Values.ToList())
            sink.OnConnectionLost("Signed out of Mistral.");
        _sessions.Clear();
        State = ConnectionState.SignInRequired;
        Status = "Signed out of Mistral. Sign in to start again.";
        return true;
    }

    private async Task CloseClientAsync(string reason)
    {
        _generation++;
        var client = _client;
        _client = null;
        foreach (var sink in _sessions.Values.ToList())
            sink.OnConnectionLost(reason);
        _sessions.Clear();
        if (client is not null)
            await client.DisposeAsync();
    }

    public async ValueTask DisposeAsync() => await CloseClientAsync("Vibe UI closed the agent.");

    private void OnAgentClosed(int generation, object? failure)
    {
        if (generation != _generation)
            return;
        _client = null;
        var reason = failure switch
        {
            Exception exception => $"Mistral Vibe stopped: {exception.Message}",
            _ => "Mistral Vibe exited.",
        };
        foreach (var sink in _sessions.Values.ToList())
            sink.OnConnectionLost(reason);
        _sessions.Clear();
        State = ConnectionState.Disconnected;
        Status = reason + " Reconnect to continue.";
    }

    private void AppendLog(string line)
    {
        AgentLog.Add(line);
        while (AgentLog.Count > MaxLogLines)
            AgentLog.RemoveAt(0);
    }

    // IAcpClientHandler: these run on connection threads and marshal onto the UI thread.

    public Task OnSessionUpdateAsync(string sessionId, SessionUpdate update)
    {
        _dispatch(() =>
        {
            if (_sessions.TryGetValue(sessionId, out var sink))
                sink.ApplyUpdate(update);
        });
        return Task.CompletedTask;
    }

    public async Task<RequestPermissionResponse> RequestPermissionAsync(RequestPermissionRequest request, CancellationToken cancellationToken)
    {
        var routed = new TaskCompletionSource<Task<RequestPermissionResponse>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatch(() =>
        {
            routed.SetResult(_sessions.TryGetValue(request.SessionId, out var sink)
                ? sink.RequestPermissionAsync(request, cancellationToken)
                : Task.FromResult(new RequestPermissionResponse(PermissionOutcome.Cancelled)));
        });
        return await await routed.Task;
    }

    public Task<ReadTextFileResponse> ReadTextFileAsync(ReadTextFileRequest request, CancellationToken cancellationToken)
        => LocalTextFiles.ReadAsync(request, cancellationToken);

    public Task WriteTextFileAsync(WriteTextFileRequest request, CancellationToken cancellationToken)
        => LocalTextFiles.WriteAsync(request, cancellationToken);
}
