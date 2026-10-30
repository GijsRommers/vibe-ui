using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VibeUi.Acp;
using VibeUi.Acp.JsonRpc;
using VibeUi.Desktop.Services;

namespace VibeUi.Desktop.ViewModels;

public enum ThreadKind
{
    Chat,
    Orchestrator,
    Task,
    Chief,
}

public enum ThreadStatus
{
    New,
    Opening,
    Ready,
    Working,
    Failed,
}

public sealed record TurnResult(string StopReason, string Reply);

/// <summary>
/// One conversation with Vibe: a session on the project's agent. Streams the transcript, answers
/// permission prompts, and survives restarts by resuming the session (or showing the saved copy).
/// </summary>
public sealed partial class ThreadViewModel : ObservableObject, ISessionSink
{
    public const string OrchestratorServerName = "vibe-ui-orchestrator";
    private const int TitleLength = 60;

    private readonly Dictionary<string, ToolCallEntry> _toolCalls = new();
    private readonly Queue<PermissionRequestViewModel> _permissionQueue = new();
    private readonly Func<ThreadViewModel, IReadOnlyList<object>> _mcpServers;

    private string? _sessionId;
    private CancellationTokenSource? _turn;
    private TaskCompletionSource<TurnResult>? _turnDone;
    private Task<bool>? _opening;
    private MessageEntry? _currentAgentMessage;
    private MessageEntry? _currentThought;
    private PlanEntryItem? _plan;
    private bool _applyingOptions;
    private bool _replaying;
    private bool _resumeFailed;
    private bool _showThoughts;

    public ThreadViewModel(ProjectViewModel project, ThreadRecord record, bool showThoughts, Func<ThreadViewModel, IReadOnlyList<object>>? mcpServers = null)
    {
        Project = project;
        Record = record;
        _showThoughts = showThoughts;
        _mcpServers = mcpServers ?? (_ => []);
        Kind = Enum.TryParse<ThreadKind>(record.Kind, ignoreCase: true, out var kind) ? kind : ThreadKind.Chat;
        _title = record.Title ?? DefaultTitle;
        _updatedAt = record.UpdatedAt;
    }

    public ProjectViewModel Project { get; }
    public ThreadRecord Record { get; }
    public string Id => Record.Id;
    public ThreadKind Kind { get; }
    /// <summary>Orchestrators and the Chief of Staff both get the delegation tools.</summary>
    public bool IsOrchestrator => Kind is ThreadKind.Orchestrator or ThreadKind.Chief;
    public bool IsChief => Kind == ThreadKind.Chief;
    public bool IsTask => Kind == ThreadKind.Task;
    public bool IsChiefTask => IsTask && ParentId == ShellViewModel.ChiefId;
    public string? ParentId => Record.ParentId;
    public string WorkspacePath => Record.WorktreePath ?? Project.Path;
    public bool HasWorktree => Record.WorktreePath is not null;
    public string? SessionId => _sessionId;
    public string DefaultTitle => Kind switch { ThreadKind.Orchestrator => "New orchestrator", ThreadKind.Task => "Task", ThreadKind.Chief => "Chief of Staff", _ => "New thread" };
    public string KindLabel => Kind switch
    {
        ThreadKind.Orchestrator => "Orchestrator",
        ThreadKind.Chief => "Chief of Staff",
        ThreadKind.Task => IsChiefTask ? "Task from the Chief of Staff" : "Delegated task",
        _ => "Thread",
    };

    public ObservableCollection<TranscriptEntry> Transcript { get; } = [];
    public ObservableCollection<SessionMode> Modes { get; } = [];
    public ObservableCollection<ConfigOptionChoice> Models { get; } = [];
    public ObservableCollection<ConfigOptionChoice> ThinkingLevels { get; } = [];

    /// <summary>Raised on the UI thread whenever transcript content grows, for scroll-to-end behaviour.</summary>
    public event EventHandler? TranscriptChanged;

    /// <summary>Raised when something worth persisting changed: title, session id, a finished turn.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised after each prompt turn ends, however it ended.</summary>
    public event EventHandler<TurnResult>? TurnCompleted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady), nameof(IsWorking), nameof(IsBusy), nameof(CanSend), nameof(HasSession), nameof(IsOpening))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(StopCommand))]
    private ThreadStatus _state = ThreadStatus.New;

    [ObservableProperty] private string _title;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private DateTimeOffset _updatedAt;
    [ObservableProperty] private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _promptText = "";

    [ObservableProperty] private string _usageText = "";
    [ObservableProperty] private SessionMode? _selectedMode;
    [ObservableProperty] private ConfigOptionChoice? _selectedModel;
    [ObservableProperty] private ConfigOptionChoice? _selectedThinking;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingPermission), nameof(NeedsAttention))]
    private PermissionRequestViewModel? _pendingPermission;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWorkspaceUntrusted))]
    [NotifyCanExecuteChangedFor(nameof(TrustWorkspaceCommand))]
    private string? _workspaceTrust;

    /// <summary>The last agent reply, kept for orchestrators asking what a task produced.</summary>
    [ObservableProperty] private string _lastReply = "";

    /// <summary>Hidden instructions sent ahead of the first prompt of a fresh session (orchestrators).</summary>
    public string? Preamble { get; set; }

    public bool IsReady => State == ThreadStatus.Ready;
    public bool IsWorking => State == ThreadStatus.Working;
    public bool IsOpening => State == ThreadStatus.Opening;
    public bool IsBusy => State is ThreadStatus.Opening or ThreadStatus.Working;
    public bool HasSession => State is ThreadStatus.Ready or ThreadStatus.Working;
    public bool CanSend => !IsBusy && !string.IsNullOrWhiteSpace(PromptText);
    public bool HasPendingPermission => PendingPermission is not null;
    public bool NeedsAttention => PendingPermission is not null;
    public bool IsWorkspaceUntrusted => WorkspaceTrust == "untrusted";
    public bool HasThinkingLevels => ThinkingLevels.Count > 0;

    /// <summary>Opens the thread's session: resumes the saved one when Vibe can, otherwise starts a new one.</summary>
    public Task<bool> EnsureSessionAsync()
    {
        if (HasSession && _sessionId is not null)
            return Task.FromResult(true);
        if (_opening is { IsCompleted: false } pending)
            return pending;
        // Assigned before anything can observe it: with a synchronous dispatcher the open may finish before this line otherwise.
        return _opening = OpenSessionAsync();
    }

    private async Task<bool> OpenSessionAsync()
    {
        {
            var connection = Project.Connection;
            if (!await connection.EnsureConnectedAsync())
            {
                State = ThreadStatus.Failed;
                Status = connection.Status;
                return false;
            }
            var client = connection.Client;
            if (client is null)
                return false;
            State = ThreadStatus.Opening;
            Status = "Opening the session…";
            var servers = _mcpServers(this);
            try
            {
                if (Record.SessionId is { } saved && connection.CanLoadSessions && !_resumeFailed)
                {
                    var keep = Transcript.ToList();
                    ResetTranscript();
                    _replaying = true;
                    connection.Register(saved, this);
                    try
                    {
                        var loaded = await client.LoadSessionAsync(new LoadSessionRequest(saved, WorkspacePath, servers));
                        _sessionId = saved;
                        if (Transcript.Count == 0)
                            foreach (var entry in keep)
                                Transcript.Add(entry);
                        ApplyModes(loaded.Modes);
                        ApplyConfigOptions(loaded.ConfigOptions);
                        WorkspaceTrust = loaded.Meta is { } meta ? new NewSessionResponse(saved, Meta: meta).WorkspaceTrustStatus : null;
                    }
                    catch (JsonRpcException exception) when (exception.Code != JsonRpcException.AuthenticationRequired)
                    {
                        connection.Unregister(saved);
                        foreach (var entry in keep)
                            Transcript.Add(entry);
                        _resumeFailed = true;
                        if (keep.Count > 0)
                            Transcript.Add(new NoticeEntry("Vibe could not resume this session, so the thread continues with fresh context. The messages above are a saved copy."));
                        TranscriptChanged?.Invoke(this, EventArgs.Empty);
                        throw new ResumeFailedException(exception);
                    }
                    finally
                    {
                        _replaying = false;
                    }
                }
                else
                {
                    var session = await client.NewSessionAsync(new NewSessionRequest(WorkspacePath, servers));
                    _sessionId = session.SessionId;
                    connection.Register(session.SessionId, this);
                    ApplyModes(session.Modes);
                    ApplyConfigOptions(session.ConfigOptions);
                    WorkspaceTrust = session.WorkspaceTrustStatus;
                    Record.SessionId = session.SessionId;
                    await RestoreSavedSelectionsAsync(client);
                }
            }
            catch (ResumeFailedException)
            {
                return await OpenSessionAsync();
            }
            catch (JsonRpcException exception) when (exception.Code == JsonRpcException.AuthenticationRequired)
            {
                State = ThreadStatus.New;
                Status = "Sign in to Mistral to start.";
                connection.MarkSignInRequired();
                return false;
            }
            catch (Exception exception) when (exception is JsonRpcException or IOException or ObjectDisposedException)
            {
                State = ThreadStatus.Failed;
                Status = $"Could not open the session: {exception.Message}";
                return false;
            }
            State = ThreadStatus.Ready;
            Status = "Ready.";
            connection.MarkSignedIn();
            if ((IsTask || IsChief) && IsWorkspaceUntrusted)
                await TrustWorkspaceAsync();
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }
    }

    private sealed class ResumeFailedException(Exception inner) : Exception("resume failed", inner);

    private async Task RestoreSavedSelectionsAsync(IAcpClient client)
    {
        if (_sessionId is null)
            return;
        try
        {
            if (Record.ModeId is { } modeId && Modes.FirstOrDefault(mode => mode.Id == modeId) is { } mode && SelectedMode?.Id != modeId)
            {
                await client.SetModeAsync(_sessionId, modeId);
                SetSilently(() => SelectedMode = mode);
            }
            if (Record.ModelId is { } modelId && Models.Any(choice => choice.Value == modelId) && SelectedModel?.Value != modelId)
                ApplyConfigOptions((await client.SetConfigOptionAsync(_sessionId, "model", modelId)).ConfigOptions);
        }
        catch (JsonRpcException)
        {
            // The session keeps Vibe's defaults.
        }
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private Task SendAsync() => SendAsync(PromptText);

    /// <summary>Sends one prompt and runs the turn to completion. Opens the session first when needed.</summary>
    /// <param name="shownAs">How the prompt appears in the transcript: the user, or a briefing injected by Vibe UI.</param>
    public async Task<TurnResult> SendAsync(string text, MessageRole shownAs = MessageRole.User)
    {
        text = text.Trim();
        if (text.Length == 0 || IsBusy)
            return new TurnResult("not_sent", "");
        if (!await EnsureSessionAsync() || Project.Connection.Client is not { } client || _sessionId is not { } sessionId)
            return new TurnResult("not_sent", Status);
        var hiddenPreamble = Preamble is not null && !Transcript.OfType<MessageEntry>().Any(entry => entry.Role is MessageRole.User or MessageRole.Briefing) ? Preamble : null;
        if (shownAs == MessageRole.User)
            PromptText = "";
        Transcript.Add(new MessageEntry(shownAs, text));
        TranscriptChanged?.Invoke(this, EventArgs.Empty);
        if (Title == DefaultTitle && shownAs == MessageRole.User && !IsChief)
            Title = TitleFrom(text);
        _currentAgentMessage = null;
        _currentThought = null;
        State = ThreadStatus.Working;
        Status = "Vibe is working…";
        var turn = new CancellationTokenSource();
        var done = new TaskCompletionSource<TurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _turn = turn;
        _turnDone = done;
        var replyStart = Transcript.Count;
        string stopReason;
        try
        {
            var prompt = hiddenPreamble is null ? text : hiddenPreamble + "\n\n" + text;
            var response = await client.PromptAsync(new PromptRequest(sessionId, [ContentBlock.FromText(prompt)]), turn.Token);
            stopReason = response.StopReason;
            Status = stopReason switch
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
            stopReason = StopReasons.Cancelled;
            Status = "Stopped.";
        }
        catch (Exception exception) when (exception is JsonRpcException or IOException or ObjectDisposedException)
        {
            stopReason = "error";
            Transcript.Add(new NoticeEntry(exception.Message));
            TranscriptChanged?.Invoke(this, EventArgs.Empty);
            Status = $"Vibe reported an error: {exception.Message}";
            if (exception is JsonRpcException { Code: JsonRpcException.AuthenticationRequired })
            {
                Project.Connection.MarkSignInRequired();
                _sessionId = null;
                State = ThreadStatus.New;
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
        if (State == ThreadStatus.Working)
            State = ThreadStatus.Ready;
        LastReply = string.Join("\n\n", Transcript.Skip(replyStart).OfType<MessageEntry>().Where(entry => entry.IsAgent).Select(entry => entry.Text));
        UpdatedAt = DateTimeOffset.UtcNow;
        var result = new TurnResult(stopReason, LastReply);
        done.TrySetResult(result);
        if (_turnDone == done)
            _turnDone = null;
        Changed?.Invoke(this, EventArgs.Empty);
        TurnCompleted?.Invoke(this, result);
        return result;
    }

    /// <summary>Completes when the current turn ends; immediately when none is running.</summary>
    public Task<TurnResult> WaitForTurnAsync(CancellationToken cancellationToken)
        => _turnDone is { } pending ? pending.Task.WaitAsync(cancellationToken) : Task.FromResult(new TurnResult(IsWorking ? "running" : "idle", LastReply));

    [RelayCommand(CanExecute = nameof(IsWorking))]
    public async Task StopAsync()
    {
        if (Project.Connection.Client is not { } client || _sessionId is null)
            return;
        Status = "Stopping…";
        CancelPermissions();
        try
        {
            await client.CancelAsync(_sessionId);
        }
        catch (Exception exception) when (exception is JsonRpcException or IOException or ObjectDisposedException)
        {
            _turn?.Cancel();
        }
    }

    [RelayCommand(CanExecute = nameof(IsWorkspaceUntrusted))]
    private async Task TrustWorkspaceAsync()
    {
        if (Project.Connection.Client is not { } client || _sessionId is null)
            return;
        try
        {
            var result = await client.ExtensionRequestAsync(AgentConnection.TrustDecideMethod, new { sessionId = _sessionId, decision = "trust_cwd", cwd = WorkspacePath });
            WorkspaceTrust = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("trust_status", out var status) ? status.GetString() : "trusted";
            if (!IsWorkspaceUntrusted)
                Status = "Folder trusted. Project instructions load on the next session.";
        }
        catch (JsonRpcException exception)
        {
            Status = $"Could not trust the folder: {exception.Message}";
        }
    }

    public async Task RenameAsync(string title)
    {
        title = title.Trim();
        if (title.Length == 0 || title == Title)
            return;
        Title = title;
        if (Project.Connection.Client is { } client && _sessionId is not null)
        {
            try
            {
                await client.ExtensionRequestAsync(AgentConnection.SetTitleMethod, new { sessionId = _sessionId, title });
            }
            catch (JsonRpcException)
            {
                // The local title still applies.
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Closes the session on the agent (if open) and optionally deletes it from Vibe's history.</summary>
    public async Task CloseAsync(bool deleteFromAgent)
    {
        if (IsWorking)
        {
            await StopAsync();
            _turn?.Cancel();
        }
        CancelPermissions();
        var client = Project.Connection.Client;
        var sessionId = _sessionId ?? Record.SessionId;
        _sessionId = null;
        State = ThreadStatus.New;
        if (client is null || sessionId is null)
            return;
        Project.Connection.Unregister(sessionId);
        try
        {
            if (deleteFromAgent)
                await client.ExtensionRequestAsync(AgentConnection.DeleteSessionMethod, new { sessionId });
            else
                await client.CloseSessionAsync(sessionId);
        }
        catch (Exception exception) when (exception is JsonRpcException or IOException or ObjectDisposedException)
        {
        }
    }

    public void SetThoughtsVisible(bool visible)
    {
        _showThoughts = visible;
        foreach (var entry in Transcript.OfType<MessageEntry>().Where(message => message.IsThought))
            entry.IsHidden = !visible;
    }

    public TranscriptSnapshot Snapshot() => new(Transcript.Select(entry => entry.ToRecord()).ToList());

    public void Restore(TranscriptSnapshot snapshot)
    {
        if (Transcript.Count > 0)
            return;
        foreach (var record in snapshot.Entries)
        {
            if (TranscriptEntry.FromRecord(record) is not { } entry)
                continue;
            if (entry is MessageEntry { IsThought: true } thought)
                thought.IsHidden = !_showThoughts;
            Transcript.Add(entry);
        }
        LastReply = Transcript.OfType<MessageEntry>().LastOrDefault(entry => entry.IsAgent)?.Text ?? "";
    }

    public ThreadRecord ToRecord()
    {
        Record.Title = Title == DefaultTitle ? null : Title;
        Record.UpdatedAt = UpdatedAt;
        Record.ModeId = SelectedMode?.Id ?? Record.ModeId;
        Record.ModelId = SelectedModel?.Value ?? Record.ModelId;
        Record.Kind = Kind.ToString();
        return Record;
    }

    public static string TitleFrom(string prompt)
    {
        var line = prompt.Split('\n').Select(part => part.Trim()).FirstOrDefault(part => part.Length > 0) ?? prompt.Trim();
        return line.Length <= TitleLength ? line : line[..(TitleLength - 1)].TrimEnd() + "…";
    }

    partial void OnTitleChanged(string value)
    {
        Record.Title = value == DefaultTitle ? null : value;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSelectedModeChanged(SessionMode? value)
    {
        if (_applyingOptions || value is null)
            return;
        Record.ModeId = value.Id;
        _ = ApplySelectionAsync(client => client.SetModeAsync(_sessionId!, value.Id), $"Mode: {value.Name}.");
    }

    partial void OnSelectedModelChanged(ConfigOptionChoice? value)
    {
        if (_applyingOptions || value is null)
            return;
        Record.ModelId = value.Value;
        _ = ApplySelectionAsync(async client => ApplyConfigOptions((await client.SetConfigOptionAsync(_sessionId!, "model", value.Value)).ConfigOptions), $"Model: {value.Name}.");
    }

    partial void OnSelectedThinkingChanged(ConfigOptionChoice? value)
    {
        if (_applyingOptions || value is null)
            return;
        _ = ApplySelectionAsync(async client => ApplyConfigOptions((await client.SetConfigOptionAsync(_sessionId!, "thinking", value.Value)).ConfigOptions), $"Thinking: {value.Name}.");
    }

    private async Task ApplySelectionAsync(Func<IAcpClient, Task> change, string success)
    {
        if (Project.Connection.Client is not { } client || _sessionId is null)
            return;
        try
        {
            await change(client);
            Status = success;
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception) when (exception is JsonRpcException or IOException or ObjectDisposedException)
        {
            Status = $"Could not apply the change: {exception.Message}";
        }
    }

    private void SetSilently(Action change)
    {
        _applyingOptions = true;
        try
        {
            change();
        }
        finally
        {
            _applyingOptions = false;
        }
    }

    private void ApplyModes(SessionModeState? modes)
    {
        SetSilently(() =>
        {
            Modes.Clear();
            if (modes is null)
                return;
            foreach (var mode in modes.AvailableModes)
                Modes.Add(mode);
            SelectedMode = Modes.FirstOrDefault(mode => mode.Id == modes.CurrentModeId);
        });
    }

    private void ApplyConfigOptions(IReadOnlyList<SessionConfigOption>? options)
    {
        if (options is null)
            return;
        SetSilently(() =>
        {
            var model = options.FirstOrDefault(option => option.Category == "model") ?? options.FirstOrDefault(option => option.Id == "model");
            if (model is not null)
            {
                Replace(Models, model.Options ?? []);
                SelectedModel = Models.FirstOrDefault(choice => choice.Value == model.CurrentValueText);
            }
            var thinking = options.FirstOrDefault(option => option.Id == "thinking") ?? options.FirstOrDefault(option => option.Category is "thought_level" or "thinking");
            if (thinking is not null)
            {
                Replace(ThinkingLevels, thinking.Options ?? []);
                SelectedThinking = ThinkingLevels.FirstOrDefault(choice => choice.Value == thinking.CurrentValueText);
                OnPropertyChanged(nameof(HasThinkingLevels));
            }
            var mode = options.FirstOrDefault(option => option.Category == "mode");
            if (mode?.CurrentValueText is { } modeId && Modes.FirstOrDefault(candidate => candidate.Id == modeId) is { } selected)
                SelectedMode = selected;
        });

        static void Replace(ObservableCollection<ConfigOptionChoice> target, IReadOnlyList<ConfigOptionChoice> choices)
        {
            if (target.Select(choice => choice.Value).SequenceEqual(choices.Select(choice => choice.Value)))
                return;
            target.Clear();
            foreach (var choice in choices)
                target.Add(choice);
        }
    }

    private void ResetTranscript()
    {
        Transcript.Clear();
        _toolCalls.Clear();
        _currentAgentMessage = null;
        _currentThought = null;
        _plan = null;
        UsageText = "";
        CancelPermissions();
    }

    // ISessionSink: called on the UI thread by the connection.

    public void OnConnectionLost(string reason)
    {
        _sessionId = null;
        _turn?.Cancel();
        CancelPermissions();
        if (State != ThreadStatus.New)
        {
            State = ThreadStatus.New;
            Status = reason;
        }
    }

    public async Task<RequestPermissionResponse> RequestPermissionAsync(RequestPermissionRequest request, CancellationToken cancellationToken)
    {
        _toolCalls.TryGetValue(request.ToolCall.ToolCallId, out var entry);
        entry?.Apply(request.ToolCall);
        var prompt = new PermissionRequestViewModel(request, entry);
        _permissionQueue.Enqueue(prompt);
        ShowNextPermission();
        using var registration = cancellationToken.Register(prompt.Cancel);
        var answer = await prompt.Answer;
        if (PendingPermission == prompt)
            PendingPermission = null;
        ShowNextPermission();
        return answer;
    }

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

    public void ApplyUpdate(SessionUpdate update)
    {
        switch (update)
        {
            case MessageChunkUpdate { Kind: SessionUpdateKinds.UserMessageChunk } chunk:
                if (!_replaying)
                    return; // Already shown when the prompt was sent.
                _currentAgentMessage = null;
                _currentThought = null;
                Transcript.Add(new MessageEntry(MessageRole.User, chunk.Content.Text ?? "", chunk.MessageId));
                break;
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
                SetSilently(() => SelectedMode = Modes.FirstOrDefault(candidate => candidate.Id == mode.CurrentModeId) ?? SelectedMode);
                return;
            case ConfigOptionsSessionUpdate options:
                ApplyConfigOptions(options.ConfigOptions);
                return;
            case SessionInfoSessionUpdate info:
                if (info.Title is { Length: > 0 } title)
                    Title = title;
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
            current = new MessageEntry(role, text, chunk.MessageId) { IsHidden = role == MessageRole.Thought && !_showThoughts };
            Transcript.Add(current);
        }
        else
        {
            current.Append(text);
        }
    }
}
