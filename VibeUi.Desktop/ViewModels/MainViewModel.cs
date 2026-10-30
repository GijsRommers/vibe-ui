using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VibeUi.Acp;
using VibeUi.Acp.JsonRpc;
using VibeUi.Desktop.Services;

namespace VibeUi.Desktop.ViewModels;

public enum AgentState
{
    NotInstalled,
    Disconnected,
    Starting,
    SignInRequired,
    SigningIn,
    Ready,
    Working,
    Failed,
}

public delegate IAcpClient AgentFactory(string executable, string workspace, IAcpClientHandler handler, Action<string> standardError);

/// <summary>
/// Drives one Mistral Vibe agent process for the chosen workspace: start, sign in, open a session,
/// send prompts, stream the transcript and answer permission prompts.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IAcpClientHandler
{
    public const string ClientName = "vibe-ui";
    public const string DelegatedBrowserAuth = "browser-auth-delegated";
    public const string TrustDecideMethod = "_trust/decision";
    public const string SignOutMethod = "_auth/signOut";
    private const int MaxLogLines = 300;

    private readonly Func<string?> _locateAgent;
    private readonly AgentFactory _startAgent;
    private readonly Action<string> _openUrl;
    private readonly Action<Action> _dispatch;
    private readonly DesktopSettings _settings;
    private readonly string _clientVersion;
    private readonly Dictionary<string, ToolCallEntry> _toolCalls = new();
    private readonly Queue<PermissionRequestViewModel> _permissionQueue = new();

    private IAcpClient? _client;
    private string? _sessionId;
    private IReadOnlyList<AuthMethod> _authMethods = [];
    private CancellationTokenSource? _turn;
    private MessageEntry? _currentAgentMessage;
    private MessageEntry? _currentThought;
    private PlanEntryItem? _plan;
    private bool _applyingOptions;
    private int _generation;

    public MainViewModel(
        Func<string?> locateAgent,
        AgentFactory startAgent,
        Action<string> openUrl,
        Action<Action> dispatch,
        DesktopSettings settings,
        string clientVersion = "0.0.0")
    {
        _locateAgent = locateAgent;
        _startAgent = startAgent;
        _openUrl = openUrl;
        _dispatch = dispatch;
        _settings = settings;
        _clientVersion = clientVersion;
        _workspacePath = settings.WorkspacePath is { Length: > 0 } saved && Directory.Exists(saved)
            ? saved
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _showThoughts = settings.ShowThoughts;
    }

    public ObservableCollection<TranscriptEntry> Transcript { get; } = [];
    public ObservableCollection<SessionMode> Modes { get; } = [];
    public ObservableCollection<ConfigOptionChoice> Models { get; } = [];
    public ObservableCollection<string> AgentLog { get; } = [];
    public string InstallCommand => VibeAcpLauncher.InstallCommand;

    /// <summary>Raised on the UI thread whenever transcript content grows, for scroll-to-end behaviour.</summary>
    public event EventHandler? TranscriptChanged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady), nameof(IsWorking), nameof(IsBusy), nameof(IsInstalled), nameof(NeedsSignIn),
        nameof(IsSigningIn), nameof(CanSend), nameof(ShowSession), nameof(ShowConnectPanel), nameof(CanConnect), nameof(CanSignIn))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(StopCommand), nameof(SignInCommand), nameof(NewSessionCommand),
        nameof(ConnectCommand), nameof(TrustWorkspaceCommand), nameof(SignOutCommand))]
    private AgentState _state = AgentState.Disconnected;

    [ObservableProperty] private string _workspacePath;
    [ObservableProperty] private string _status = "Not connected.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _promptText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSignInUrl))]
    [NotifyCanExecuteChangedFor(nameof(OpenSignInUrlCommand))]
    private string? _signInUrl;

    [ObservableProperty] private string _agentDescription = "Mistral Vibe";
    [ObservableProperty] private string _usageText = "";
    [ObservableProperty] private string? _sessionTitle;
    [ObservableProperty] private SessionMode? _selectedMode;
    [ObservableProperty] private ConfigOptionChoice? _selectedModel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingPermission))]
    private PermissionRequestViewModel? _pendingPermission;

    [ObservableProperty] private bool _showThoughts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWorkspaceUntrusted))]
    [NotifyCanExecuteChangedFor(nameof(TrustWorkspaceCommand))]
    private string? _workspaceTrust;

    public bool IsReady => State == AgentState.Ready;
    public bool IsWorking => State == AgentState.Working;
    public bool IsBusy => State is AgentState.Starting or AgentState.SigningIn or AgentState.Working;
    public bool IsInstalled => State != AgentState.NotInstalled;
    public bool NeedsSignIn => State is AgentState.SignInRequired or AgentState.SigningIn;
    public bool IsSigningIn => State == AgentState.SigningIn;
    public bool CanSend => State == AgentState.Ready && !string.IsNullOrWhiteSpace(PromptText);
    public bool CanConnect => State is not (AgentState.Starting or AgentState.SigningIn);
    public bool CanSignIn => State == AgentState.SignInRequired;
    public bool ShowSession => State is AgentState.Ready or AgentState.Working;
    public bool ShowConnectPanel => State is AgentState.Disconnected or AgentState.Starting or AgentState.Failed;
    public bool HasSignInUrl => !string.IsNullOrEmpty(SignInUrl);
    public bool HasPendingPermission => PendingPermission is not null;
    public bool IsWorkspaceUntrusted => WorkspaceTrust == "untrusted";

    [RelayCommand(CanExecute = nameof(CanConnect))]
    public async Task ConnectAsync()
    {
        await DisconnectAsync();
        var executable = _locateAgent();
        if (executable is null)
        {
            State = AgentState.NotInstalled;
            Status = "Mistral Vibe is not installed, or vibe-acp is not on your PATH.";
            return;
        }
        State = AgentState.Starting;
        Status = "Starting Mistral Vibe…";
        var generation = ++_generation;
        try
        {
            var client = _startAgent(executable, WorkspacePath, this, line => _dispatch(() => AppendLog(line)));
            _client = client;
            _ = client.Closed.ContinueWith(task => _dispatch(() => OnAgentClosed(generation, task.IsCompletedSuccessfully ? task.Result : task.Exception)),
                TaskScheduler.Default);
            var capabilities = new ClientCapabilities(
                new FileSystemCapabilities(ReadTextFile: true, WriteTextFile: true),
                Terminal: false,
                Meta: new Dictionary<string, object?> { [DelegatedBrowserAuth] = true });
            var initialized = await client.InitializeAsync(new InitializeRequest(AcpClient.ProtocolVersion, capabilities, new Implementation(ClientName, _clientVersion, "Vibe UI")));
            if (generation != _generation)
                return;
            _authMethods = initialized.AuthMethods ?? [];
            var info = initialized.AgentInfo;
            AgentDescription = info is null ? "Agent" : $"{info.Title ?? info.Name} {info.Version}".Trim();
            await OpenSessionAsync();
        }
        catch (Exception exception) when (generation == _generation && exception is not OperationCanceledException)
        {
            State = AgentState.Failed;
            Status = $"Could not start Mistral Vibe: {exception.Message}";
        }
    }

    private async Task OpenSessionAsync()
    {
        if (_client is null)
            return;
        try
        {
            var session = await _client.NewSessionAsync(new NewSessionRequest(WorkspacePath, []));
            _sessionId = session.SessionId;
            ResetTranscript();
            ApplyModes(session.Modes);
            ApplyConfigOptions(session.ConfigOptions);
            WorkspaceTrust = session.WorkspaceTrustStatus;
            State = AgentState.Ready;
            Status = $"{AgentDescription} is ready in {WorkspacePath}.";
        }
        catch (JsonRpcException exception) when (exception.Code == JsonRpcException.AuthenticationRequired)
        {
            State = AgentState.SignInRequired;
            Status = "Sign in to Mistral to start.";
        }
    }

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignInAsync()
    {
        if (_client is null)
            return;
        var client = _client;
        State = AgentState.SigningIn;
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
                State = AgentState.SignInRequired;
                Status = "Mistral Vibe offers no sign-in method here. Run `vibe --setup` in a terminal, then connect again.";
                return;
            }
            if (client != _client)
                return;
            SignInUrl = null;
            Status = "Signed in.";
            await OpenSessionAsync();
        }
        catch (JsonRpcException exception) when (client == _client)
        {
            State = AgentState.SignInRequired;
            Status = $"Sign-in failed: {exception.Message}";
            SignInUrl = null;
        }
    }

    [RelayCommand(CanExecute = nameof(HasSignInUrl))]
    private void OpenSignInUrl()
    {
        if (SignInUrl is { } url)
            _openUrl(url);
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        if (_client is null || _sessionId is null || State != AgentState.Ready)
            return;
        var text = PromptText.Trim();
        if (text.Length == 0)
            return;
        var client = _client;
        var sessionId = _sessionId;
        PromptText = "";
        Transcript.Add(new MessageEntry(MessageRole.User, text));
        TranscriptChanged?.Invoke(this, EventArgs.Empty);
        _currentAgentMessage = null;
        _currentThought = null;
        State = AgentState.Working;
        Status = "Vibe is working…";
        var turn = new CancellationTokenSource();
        _turn = turn;
        try
        {
            var response = await client.PromptAsync(new PromptRequest(sessionId, [ContentBlock.FromText(text)]), turn.Token);
            Status = response.StopReason switch
            {
                StopReasons.EndTurn => "Done.",
                StopReasons.Cancelled => "Stopped.",
                StopReasons.Refusal => "Vibe declined to continue.",
                StopReasons.MaxTokens => "Stopped: the token limit was reached.",
                StopReasons.MaxTurnRequests => "Stopped: the turn limit was reached.",
                var other => $"Stopped: {other}.",
            };
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped.";
        }
        catch (JsonRpcException exception)
        {
            Transcript.Add(new NoticeEntry(exception.Message));
            TranscriptChanged?.Invoke(this, EventArgs.Empty);
            Status = $"Vibe reported an error: {exception.Message}";
            if (exception.Code == JsonRpcException.AuthenticationRequired && client == _client)
            {
                State = AgentState.SignInRequired;
                return;
            }
        }
        finally
        {
            if (_turn == turn)
                _turn = null;
            turn.Dispose();
            _currentAgentMessage = null;
            _currentThought = null;
        }
        if (State == AgentState.Working && client == _client)
            State = AgentState.Ready;
    }

    [RelayCommand(CanExecute = nameof(IsWorking))]
    private async Task StopAsync()
    {
        if (_client is null || _sessionId is null)
            return;
        Status = "Stopping…";
        CancelPermissions();
        try
        {
            await _client.CancelAsync(_sessionId);
        }
        catch (Exception exception) when (exception is JsonRpcException or IOException or ObjectDisposedException)
        {
            _turn?.Cancel();
        }
    }

    [RelayCommand(CanExecute = nameof(ShowSession))]
    private async Task NewSessionAsync()
    {
        if (_client is null)
            return;
        if (State == AgentState.Working)
        {
            await StopAsync();
            _turn?.Cancel();
        }
        var previous = _sessionId;
        _sessionId = null;
        if (previous is not null)
        {
            try
            {
                await _client.CloseSessionAsync(previous);
            }
            catch (JsonRpcException)
            {
                // Older agents cannot close sessions; the new one still replaces it.
            }
        }
        State = AgentState.Starting;
        Status = "Starting a new session…";
        await OpenSessionAsync();
    }

    [RelayCommand(CanExecute = nameof(IsWorkspaceUntrusted))]
    private async Task TrustWorkspaceAsync()
    {
        if (_client is null || _sessionId is null)
            return;
        try
        {
            var result = await _client.ExtensionRequestAsync(TrustDecideMethod, new { sessionId = _sessionId, decision = "trust_cwd", cwd = WorkspacePath });
            WorkspaceTrust = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("trust_status", out var status) ? status.GetString() : "trusted";
            Status = IsWorkspaceUntrusted ? Status : "Folder trusted. Start a new session to load its project instructions.";
        }
        catch (JsonRpcException exception)
        {
            Status = $"Could not trust the folder: {exception.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(IsReady))]
    private async Task SignOutAsync()
    {
        if (_client is null)
            return;
        var client = _client;
        try
        {
            await client.ExtensionRequestAsync(SignOutMethod, new { });
        }
        catch (JsonRpcException exception)
        {
            Status = $"Could not sign out: {exception.Message}";
            return;
        }
        if (client != _client)
            return;
        _sessionId = null;
        ResetTranscript();
        State = AgentState.SignInRequired;
        Status = "Signed out of Mistral. Sign in to start again.";
    }

    public async Task ChangeWorkspaceAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return;
        WorkspacePath = Path.GetFullPath(path);
        _settings.WorkspacePath = WorkspacePath;
        _settings.Save();
        await ConnectAsync();
    }

    public async Task DisconnectAsync()
    {
        _generation++;
        var client = _client;
        _client = null;
        _sessionId = null;
        _turn?.Cancel();
        CancelPermissions();
        if (client is not null)
            await client.DisposeAsync();
    }

    public async ValueTask ShutdownAsync()
    {
        _settings.WorkspacePath = WorkspacePath;
        _settings.ShowThoughts = ShowThoughts;
        _settings.Save();
        await DisconnectAsync();
    }

    partial void OnShowThoughtsChanged(bool value)
    {
        foreach (var entry in Transcript.OfType<MessageEntry>().Where(message => message.IsThought))
            entry.IsHidden = !value;
        _settings.ShowThoughts = value;
        _settings.Save();
    }

    partial void OnSelectedModeChanged(SessionMode? value)
    {
        if (_applyingOptions || value is null)
            return;
        _ = ApplySelectionAsync(client => client.SetModeAsync(_sessionId!, value.Id), $"Mode: {value.Name}.");
    }

    partial void OnSelectedModelChanged(ConfigOptionChoice? value)
    {
        if (_applyingOptions || value is null)
            return;
        _ = ApplySelectionAsync(async client =>
        {
            var response = await client.SetConfigOptionAsync(_sessionId!, "model", value.Value);
            ApplyConfigOptions(response.ConfigOptions);
        }, $"Model: {value.Name}.");
    }

    private async Task ApplySelectionAsync(Func<IAcpClient, Task> change, string success)
    {
        if (_client is null || _sessionId is null)
            return;
        try
        {
            await change(_client);
            Status = success;
        }
        catch (Exception exception) when (exception is JsonRpcException or IOException or ObjectDisposedException)
        {
            Status = $"Could not apply the change: {exception.Message}";
        }
    }

    private void ApplyModes(SessionModeState? modes)
    {
        _applyingOptions = true;
        try
        {
            Modes.Clear();
            if (modes is null)
                return;
            foreach (var mode in modes.AvailableModes)
                Modes.Add(mode);
            SelectedMode = Modes.FirstOrDefault(mode => mode.Id == modes.CurrentModeId);
        }
        finally
        {
            _applyingOptions = false;
        }
    }

    private void ApplyConfigOptions(IReadOnlyList<SessionConfigOption>? options)
    {
        if (options is null)
            return;
        _applyingOptions = true;
        try
        {
            var model = options.FirstOrDefault(option => option.Category == "model") ?? options.FirstOrDefault(option => option.Id == "model");
            if (model is not null)
            {
                var current = model.CurrentValueText;
                if (!Models.Select(choice => choice.Value).SequenceEqual((model.Options ?? []).Select(choice => choice.Value)))
                {
                    Models.Clear();
                    foreach (var choice in model.Options ?? [])
                        Models.Add(choice);
                }
                SelectedModel = Models.FirstOrDefault(choice => choice.Value == current);
            }
            var mode = options.FirstOrDefault(option => option.Category == "mode");
            if (mode?.CurrentValueText is { } modeId && Modes.FirstOrDefault(candidate => candidate.Id == modeId) is { } selected)
                SelectedMode = selected;
        }
        finally
        {
            _applyingOptions = false;
        }
    }

    private void ResetTranscript()
    {
        Transcript.Clear();
        _toolCalls.Clear();
        _currentAgentMessage = null;
        _currentThought = null;
        _plan = null;
        SessionTitle = null;
        UsageText = "";
        CancelPermissions();
    }

    private void OnAgentClosed(int generation, object? failure)
    {
        if (generation != _generation)
            return;
        _client = null;
        _sessionId = null;
        _turn?.Cancel();
        CancelPermissions();
        State = AgentState.Disconnected;
        Status = failure switch
        {
            Exception exception => $"Mistral Vibe stopped: {exception.Message}",
            _ => "Mistral Vibe exited. Connect again to continue.",
        };
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
            if (sessionId == _sessionId)
                ApplyUpdate(update);
        });
        return Task.CompletedTask;
    }

    public async Task<RequestPermissionResponse> RequestPermissionAsync(RequestPermissionRequest request, CancellationToken cancellationToken)
    {
        var created = new TaskCompletionSource<PermissionRequestViewModel>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatch(() =>
        {
            _toolCalls.TryGetValue(request.ToolCall.ToolCallId, out var entry);
            if (entry is not null)
                entry.Apply(request.ToolCall);
            var prompt = new PermissionRequestViewModel(request, entry);
            _permissionQueue.Enqueue(prompt);
            ShowNextPermission();
            created.SetResult(prompt);
        });
        var prompt = await created.Task;
        using var registration = cancellationToken.Register(prompt.Cancel);
        var answer = await prompt.Answer;
        _dispatch(() =>
        {
            if (PendingPermission == prompt)
                PendingPermission = null;
            ShowNextPermission();
        });
        return answer;
    }

    public Task<ReadTextFileResponse> ReadTextFileAsync(ReadTextFileRequest request, CancellationToken cancellationToken)
        => LocalTextFiles.ReadAsync(request, cancellationToken);

    public Task WriteTextFileAsync(WriteTextFileRequest request, CancellationToken cancellationToken)
        => LocalTextFiles.WriteAsync(request, cancellationToken);

    private void ShowNextPermission()
    {
        if (PendingPermission is { IsAnswered: false })
            return;
        PendingPermission = _permissionQueue.TryDequeue(out var next) ? next : null;
    }

    private void CancelPermissions()
    {
        PendingPermission?.Cancel();
        PendingPermission = null;
        while (_permissionQueue.TryDequeue(out var queued))
            queued.Cancel();
    }

    private void ApplyUpdate(SessionUpdate update)
    {
        switch (update)
        {
            case MessageChunkUpdate { Kind: SessionUpdateKinds.UserMessageChunk }:
                return; // Already shown when the prompt was sent.
            case MessageChunkUpdate { Kind: SessionUpdateKinds.AgentMessageChunk } chunk:
                _currentThought = null;
                Append(ref _currentAgentMessage, MessageRole.Agent, chunk);
                break;
            case MessageChunkUpdate { Kind: SessionUpdateKinds.AgentThoughtChunk } chunk:
                _currentAgentMessage = null;
                Append(ref _currentThought, MessageRole.Thought, chunk);
                break;
            case ToolCallSessionUpdate tool:
                _currentAgentMessage = null;
                _currentThought = null;
                if (!_toolCalls.TryGetValue(tool.ToolCall.ToolCallId, out var entry))
                {
                    entry = new ToolCallEntry(tool.ToolCall.ToolCallId);
                    _toolCalls[tool.ToolCall.ToolCallId] = entry;
                    Transcript.Add(entry);
                }
                entry.Apply(tool.ToolCall);
                break;
            case PlanSessionUpdate plan:
                if (_plan is null)
                {
                    _plan = new PlanEntryItem();
                    Transcript.Add(_plan);
                }
                _plan.Apply(plan.Entries);
                break;
            case CurrentModeSessionUpdate mode:
                _applyingOptions = true;
                try
                {
                    SelectedMode = Modes.FirstOrDefault(candidate => candidate.Id == mode.CurrentModeId) ?? SelectedMode;
                }
                finally
                {
                    _applyingOptions = false;
                }
                return;
            case ConfigOptionsSessionUpdate options:
                ApplyConfigOptions(options.ConfigOptions);
                return;
            case SessionInfoSessionUpdate info:
                SessionTitle = info.Title;
                return;
            case UsageSessionUpdate usage:
                UsageText = usage.Cost is { } cost
                    ? string.Create(CultureInfo.InvariantCulture, $"{usage.Used:N0} / {usage.Size:N0} tokens · {cost.Amount:0.00##} {cost.Currency}")
                    : string.Create(CultureInfo.InvariantCulture, $"{usage.Used:N0} / {usage.Size:N0} tokens");
                return;
            default:
                return;
        }
        TranscriptChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Append(ref MessageEntry? current, MessageRole role, MessageChunkUpdate chunk)
    {
        var text = chunk.Content.Text ?? "";
        if (current is null || (chunk.MessageId is not null && current.MessageId is not null && current.MessageId != chunk.MessageId))
        {
            current = new MessageEntry(role, text, chunk.MessageId) { IsHidden = role == MessageRole.Thought && !ShowThoughts };
            Transcript.Add(current);
        }
        else
        {
            current.Append(text);
        }
    }
}
