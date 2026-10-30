using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VibeUi.Acp;
using VibeUi.Desktop.Services;

namespace VibeUi.Desktop.ViewModels;

/// <summary>
/// The window: projects in the sidebar, each with threads; one selected thread in the middle; the
/// selected project's changes on the right. Also the host for orchestrator tools.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject, IOrchestratorHost
{
    public const string ChiefId = "chief-of-staff";

    public const string ChiefPreamble =
        "You are the Chief of Staff in Vibe UI: a standing thread pinned above every project. You have MCP tools from vibe-ui-orchestrator: " +
        "delegate_task starts a child thread in a named project (worktree=true gives it its own git worktree and branch), check_task and list_tasks " +
        "report status, send_to_task continues a task's thread, cancel_task stops one. Do not call wait_for_task: it blocks this thread. Instead, " +
        "every task you start briefs you back automatically, as messages starting with [Briefing], while it works and when it finishes. " +
        "When a briefing arrives, read it, decide whether to follow up, delegate more or stop, and keep your own reply short: a status line for the " +
        "person you work for, then any actions. Children do not see this conversation, so each prompt you delegate must be self-contained. " +
        "Projects you can delegate into:";

    public const string OrchestratorPreamble =
        "You are running as an orchestrator thread in Vibe UI. You have MCP tools from vibe-ui-orchestrator: delegate_task starts a child thread " +
        "for one self-contained task (worktree=true gives it its own git worktree and branch), wait_for_task collects a result, check_task and " +
        "list_tasks report status, send_to_task continues a task's thread, cancel_task stops one. Children do not see this conversation. " +
        "Plan the work, split it into independent tasks, delegate them in parallel, wait for them, review what came back and summarise the outcome. " +
        "search_code finds the relevant files before you delegate; list_actions/run_action run the project's declared build and test commands; " +
        "list_pull_requests, pull_request_checks and create_pull_request talk to GitHub. delegate_task takes a model id, so give routine tasks a cheaper model and hard ones a stronger one. " +
        "Do the small glue work yourself.";

    private readonly AppState _state;
    private readonly ThreadStore _store;
    private readonly Func<string?> _locateAgent;
    private readonly AgentFactory _startAgent;
    private readonly Action<string> _openUrl;
    private readonly Action<Action> _dispatch;
    private readonly string _clientVersion;
    private readonly Func<IOrchestratorHost, OrchestratorServer> _serverFactory;
    private readonly Dictionary<string, TurnResult> _lastTurns = new();
    private readonly Dictionary<string, string> _pendingBriefs = new();
    private OrchestratorServer? _server;
    private CancellationTokenSource? _briefingTimer;
    private readonly Func<CancellationToken, Task<string?>> _latestVibeVersion;
    private DateTimeOffset _lastUpdateCheck = DateTimeOffset.MinValue;

    public ShellViewModel(
        AppState state,
        ThreadStore store,
        Func<string?> locateAgent,
        AgentFactory startAgent,
        Action<string> openUrl,
        Action<Action> dispatch,
        string clientVersion = "0.0.0",
        Func<IOrchestratorHost, OrchestratorServer>? serverFactory = null,
        Func<CancellationToken, Task<string?>>? latestVibeVersion = null)
    {
        _latestVibeVersion = latestVibeVersion ?? VibeUpdater.LatestVersionAsync;
        _state = state;
        _store = store;
        _locateAgent = locateAgent;
        _startAgent = startAgent;
        _openUrl = openUrl;
        _dispatch = dispatch;
        _clientVersion = clientVersion;
        _serverFactory = serverFactory ?? (host => new OrchestratorServer(host));
        _showThoughts = state.ShowThoughts;
        _isChangesVisible = state.ShowChanges;
        _isBrowserVisible = state.ShowBrowser;
        _vibeExecutable = state.VibeExecutable ?? "";
        _briefingMinutes = Math.Max(1, state.BriefingMinutes);
        _defaultModeId = state.DefaultModeId;
        BriefingInterval = TimeSpan.FromMinutes(_briefingMinutes);
        foreach (var record in state.Projects.Where(record => Directory.Exists(record.Path)))
        {
            var project = NewProject(record);
            foreach (var threadRecord in record.Threads)
                Attach(project, NewThread(project, threadRecord));
            Projects.Add(project);
        }
        Chief = NewChief(state.Chief);
        var selected = state.SelectedThreadId == ChiefId
            ? Chief
            : Projects.SelectMany(project => project.Threads).FirstOrDefault(thread => thread.Id == state.SelectedThreadId)
              ?? Projects.SelectMany(project => project.Threads).OrderByDescending(thread => thread.UpdatedAt).FirstOrDefault();
        selected ??= Chief;
        SelectedProject = selected.Project;
        SelectedThread = selected;
        selected.IsSelected = true;
        if (Projects.SelectMany(project => project.Threads).Any(thread => thread.IsChiefTask))
            StartBriefingTimer();
    }

    /// <summary>The pinned Chief of Staff: one app-wide thread that delegates into any project and gets briefed back.</summary>
    public ThreadViewModel Chief { get; }

    /// <summary>How often a running task started by the Chief of Staff sends an interim briefing.</summary>
    public TimeSpan BriefingInterval { get; set; }

    private ThreadViewModel NewChief(ThreadRecord? saved)
    {
        var workspace = Path.Combine(_state.Directory ?? Path.Combine(Path.GetTempPath(), "vibe-ui"), "chief-of-staff");
        try
        {
            Directory.CreateDirectory(workspace);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            workspace = Path.GetTempPath();
        }
        var record = new ProjectRecord { Id = ChiefId, Path = workspace, Name = "Chief of Staff" };
        var connection = new AgentConnection(workspace, _locateAgent, _startAgent, _openUrl, _dispatch, _clientVersion);
        var project = new ProjectViewModel(record, connection, new ChangesViewModel(workspace, _dispatch), new ActionsViewModel(workspace, _dispatch), new BrowserViewModel(_openUrl), isChief: true);
        var chief = new ThreadViewModel(project, saved ?? new ThreadRecord { Id = ChiefId, Kind = ThreadKind.Chief.ToString(), ModeId = _state.DefaultModeId }, ShowThoughts, McpServersFor);
        project.Threads.Add(chief);
        connection.SignedIn += async (_, _) =>
        {
            if (SelectedThread == chief)
                await OpenAsync(chief);
        };
        chief.Changed += (_, _) =>
        {
            _store.Save(chief.Id, chief.Snapshot());
            Save();
        };
        chief.TurnCompleted += (_, result) =>
        {
            _lastTurns[chief.Id] = result;
            FlushBriefs();
        };
        return chief;
    }

    /// <summary>Forgets the Chief of Staff's conversation and starts a fresh session next time it is used.</summary>
    [RelayCommand]
    public async Task ResetChiefAsync()
    {
        await Chief.CloseAsync(deleteFromAgent: true);
        Chief.Transcript.Clear();
        Chief.Record.SessionId = null;
        Chief.LastReply = "";
        _store.Delete(Chief.Id);
        _pendingBriefs.Clear();
        Save();
        if (SelectedThread == Chief)
            await OpenAsync(Chief);
    }

    public ObservableCollection<ProjectViewModel> Projects { get; } = [];
    public string InstallCommand => VibeAcpLauncher.InstallCommand;
    public string WorktreeRoot => Path.Combine(_state.Directory ?? Path.GetTempPath(), "worktrees");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(HasProjects), nameof(ShowEmptyState), nameof(ShowThread), nameof(ShowNoThread), nameof(ActiveChanges))]
    private ThreadViewModel? _selectedThread;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveConnection), nameof(ActiveChanges), nameof(ActiveBrowser), nameof(ShowBrowserPane), nameof(HasProjects), nameof(ShowEmptyState), nameof(ShowChangesPane),
        nameof(ShowGate), nameof(ShowProgress), nameof(ShowInstall), nameof(ShowSignIn), nameof(ShowConnect), nameof(ShowThread), nameof(ShowNoThread))]
    private ProjectViewModel? _selectedProject;

    [ObservableProperty] private bool _showThoughts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowChangesPane))]
    private bool _isChangesVisible;

    [ObservableProperty] private bool _isSettingsVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBrowserPane))]
    private bool _isBrowserVisible;

    public BrowserViewModel? ActiveBrowser => SelectedProject?.Browser;

    /// <summary>Browser takes most of the window; the chat shrinks to its minimum.</summary>
    [ObservableProperty] private bool _isBrowserWide;

    [RelayCommand]
    private void ToggleBrowserWide() => IsBrowserWide = !IsBrowserWide;
    public bool ShowBrowserPane => IsBrowserVisible && SelectedProject is { IsChief: false };

    [RelayCommand]
    private void ToggleBrowser() => IsBrowserVisible = !IsBrowserVisible;

    // One right-hand pane at a time, like T3 Code: opening the browser hides the diff and the other way round.
    partial void OnIsBrowserVisibleChanged(bool value)
    {
        if (value)
            IsChangesVisible = false;
        _state.ShowBrowser = value;
        _state.Save();
    }

    // Vibe CLI updates. Checked when an agent first reports its version, then every six hours.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVibeUpdate))]
    private string? _availableVibeVersion;

    [ObservableProperty] private string _installedVibeVersion = "";
    [ObservableProperty] private bool _isUpdatingVibe;
    [ObservableProperty] private string _vibeUpdateStatus = "";

    public bool HasVibeUpdate => AvailableVibeVersion is not null && !IsUpdatingVibe;
    public string UpgradeDescription => VibeUpdater.Plan()?.Description ?? "No upgrade method found; reinstall Vibe by hand.";

    [RelayCommand]
    public async Task CheckForVibeUpdateAsync()
    {
        var installed = Projects.Select(project => project.Connection).Append(Chief.Project.Connection)
            .Select(connection => connection.AgentVersion).FirstOrDefault(version => version is not null);
        if (installed is null)
            return;
        InstalledVibeVersion = installed;
        _lastUpdateCheck = DateTimeOffset.UtcNow;
        var latest = await _latestVibeVersion(CancellationToken.None);
        AvailableVibeVersion = VibeUpdater.IsNewer(installed, latest) ? latest : null;
        VibeUpdateStatus = AvailableVibeVersion is { } newer ? $"Mistral Vibe {newer} is available (installed {installed})." : $"Mistral Vibe {installed} is up to date.";
    }

    private void MaybeCheckForVibeUpdate()
    {
        if (DateTimeOffset.UtcNow - _lastUpdateCheck > TimeSpan.FromHours(6))
            _ = CheckForVibeUpdateAsync();
    }

    /// <summary>Upgrades the CLI, then restarts every agent so the new version is in use.</summary>
    [RelayCommand]
    public async Task UpdateVibeAsync()
    {
        if (IsUpdatingVibe || VibeUpdater.Plan() is not { } plan)
            return;
        IsUpdatingVibe = true;
        OnPropertyChanged(nameof(HasVibeUpdate));
        VibeUpdateStatus = $"Running {plan.Description}…";
        try
        {
            var failure = await Task.Run(() => VibeUpdater.UpgradeAsync(plan, line => _dispatch(() => VibeUpdateStatus = line.Length > 120 ? line[..120] : line)));
            if (failure is not null)
            {
                VibeUpdateStatus = $"Update failed: {failure}";
                return;
            }
            VibeUpdateStatus = "Updated. Restarting Vibe…";
            foreach (var connection in Projects.Select(project => project.Connection).Append(Chief.Project.Connection))
                await connection.DisposeAsync();
            AvailableVibeVersion = null;
            if (SelectedThread is { } thread)
                await OpenAsync(thread);
            _lastUpdateCheck = DateTimeOffset.MinValue;
            await CheckForVibeUpdateAsync();
        }
        finally
        {
            IsUpdatingVibe = false;
            OnPropertyChanged(nameof(HasVibeUpdate));
        }
    }

    /// <summary>Settings. Saved on every change; the executable applies to agents started afterwards.</summary>
    [ObservableProperty] private string _vibeExecutable = "";
    [ObservableProperty] private int _briefingMinutes = 3;
    [ObservableProperty] private string _defaultModeId = "smart-approve";

    public IReadOnlyList<string> ModeChoices { get; } = ["smart-approve", "auto-approve", "accept-edits", "ask", "plan"];

    partial void OnDefaultModeIdChanged(string value)
    {
        _state.DefaultModeId = value;
        _state.Save();
    }

    public string DataDirectory => _state.Directory ?? "";
    public string ResolvedExecutable => _locateAgent() ?? "not found";

    [RelayCommand]
    private void ToggleSettings() => IsSettingsVisible = !IsSettingsVisible;

    partial void OnVibeExecutableChanged(string value)
    {
        _state.VibeExecutable = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        _state.Save();
        OnPropertyChanged(nameof(ResolvedExecutable));
    }

    partial void OnBriefingMinutesChanged(int value)
    {
        if (value < 1)
        {
            BriefingMinutes = 1;
            return;
        }
        _state.BriefingMinutes = value;
        BriefingInterval = TimeSpan.FromMinutes(value);
        _state.Save();
    }

    public bool HasProjects => Projects.Count > 0;
    public bool HasSelection => SelectedThread is not null;
    public bool ShowEmptyState => !HasProjects && !HasSelection;
    public AgentConnection? ActiveConnection => SelectedProject?.Connection;
    public ChangesViewModel? ActiveChanges => SelectedThread is { } thread ? thread.Project.ChangesFor(thread.WorkspacePath) : SelectedProject?.Changes;

    // Which centre panel is up. The connection's state wins over the thread while Vibe is missing, signed out or down.
    // Until the active agent is ready, a full-window gate (logo, progress, sign-in) covers everything.
    public bool ShowGate => ActiveConnection is not { State: ConnectionState.Ready };
    public bool ShowProgress => ActiveConnection is { State: ConnectionState.Starting or ConnectionState.SigningIn };
    public bool ShowInstall => ActiveConnection is { State: ConnectionState.NotInstalled };
    public bool ShowSignIn => ActiveConnection is { NeedsSignIn: true };
    public bool ShowConnect => ActiveConnection is { State: ConnectionState.Failed or ConnectionState.Disconnected };
    public bool ShowThread => HasSelection && !ShowGate;
    public bool ShowNoThread => HasProjects && !HasSelection && !ShowGate;
    public bool ShowChangesPane => IsChangesVisible && SelectedProject is { IsChief: false };

    partial void OnSelectedProjectChanged(ProjectViewModel? oldValue, ProjectViewModel? newValue)
    {
        if (oldValue is not null)
            oldValue.Connection.PropertyChanged -= OnConnectionChanged;
        if (newValue is not null)
            newValue.Connection.PropertyChanged += OnConnectionChanged;
    }

    private void OnConnectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(AgentConnection.State) or null))
            return;
        if (sender is AgentConnection { State: ConnectionState.Ready })
            MaybeCheckForVibeUpdate();
        OnPropertyChanged(nameof(ShowGate));
        OnPropertyChanged(nameof(ShowProgress));
        OnPropertyChanged(nameof(ShowInstall));
        OnPropertyChanged(nameof(ShowSignIn));
        OnPropertyChanged(nameof(ShowConnect));
        OnPropertyChanged(nameof(ShowThread));
        OnPropertyChanged(nameof(ShowNoThread));
    }

    /// <summary>Opens the selected thread's session on startup.</summary>
    public async Task InitializeAsync()
    {
        if (SelectedThread is { } thread)
            await OpenAsync(thread);
        else if (SelectedProject is { } project)
            await project.Connection.EnsureConnectedAsync();
    }

    public async Task<ProjectViewModel?> AddProjectAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return null;
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var existing = Projects.FirstOrDefault(project => string.Equals(project.Path, full, StringComparison.Ordinal));
        if (existing is null)
        {
            existing = NewProject(ProjectRecord.ForPath(full));
            Projects.Add(existing);
            OnPropertyChanged(nameof(HasProjects));
            OnPropertyChanged(nameof(ShowEmptyState));
        }
        SelectedProject = existing;
        if (existing.Threads.Count == 0)
            await NewThreadAsync(existing);
        else
            await SelectThreadAsync(existing.Threads.OrderByDescending(thread => thread.UpdatedAt).First());
        Save();
        return existing;
    }

    [RelayCommand]
    public async Task RemoveProjectAsync(ProjectViewModel project)
    {
        foreach (var thread in project.Threads.ToList())
            await thread.CloseAsync(deleteFromAgent: false);
        Projects.Remove(project);
        await project.Connection.DisposeAsync();
        foreach (var task in project.Threads.Where(thread => thread.IsChiefTask))
            _pendingBriefs.Remove(task.Id);
        if (SelectedProject == project)
        {
            SelectedProject = Projects.FirstOrDefault();
            SelectedThread = SelectedProject?.Threads.OrderByDescending(thread => thread.UpdatedAt).FirstOrDefault();
            if (SelectedThread is { } next)
                await OpenAsync(next);
        }
        OnPropertyChanged(nameof(HasProjects));
        OnPropertyChanged(nameof(ShowEmptyState));
        Save();
    }

    [RelayCommand]
    public Task NewThreadAsync(ProjectViewModel? project) => CreateThreadAsync(project ?? SelectedProject, ThreadKind.Chat);

    [RelayCommand]
    public Task NewOrchestratorAsync(ProjectViewModel? project) => CreateThreadAsync(project ?? SelectedProject, ThreadKind.Orchestrator);

    private async Task CreateThreadAsync(ProjectViewModel? project, ThreadKind kind)
    {
        if (project is null)
            return;
        var record = new ThreadRecord { Id = Guid.NewGuid().ToString("N"), Kind = kind.ToString(), ModeId = _state.DefaultModeId };
        var thread = NewThread(project, record);
        Attach(project, thread);
        project.IsExpanded = true;
        await SelectThreadAsync(thread);
        Save();
    }

    [RelayCommand]
    public async Task SelectThreadAsync(ThreadViewModel thread)
    {
        if (SelectedThread == thread)
        {
            await OpenAsync(thread);
            return;
        }
        if (SelectedThread is { } previous)
            previous.IsSelected = false;
        SelectedProject = thread.Project;
        SelectedThread = thread;
        thread.IsSelected = true;
        _state.SelectedThreadId = thread.Id;
        if (thread.Project.ChangesFor(thread.WorkspacePath) is { Loaded: false } changes)
            _ = changes.RefreshAsync();
        await OpenAsync(thread);
    }

    [RelayCommand]
    public async Task DeleteThreadAsync(ThreadViewModel thread)
    {
        var project = thread.Project;
        var doomed = new List<ThreadViewModel> { thread };
        doomed.AddRange(project.Threads.Where(candidate => candidate.ParentId == thread.Id));
        foreach (var victim in doomed)
        {
            await victim.CloseAsync(deleteFromAgent: true);
            project.Threads.Remove(victim);
            _store.Delete(victim.Id);
            _lastTurns.Remove(victim.Id);
            _pendingBriefs.Remove(victim.Id);
            if (victim.Record.WorktreePath is { } worktree)
                _ = Task.Run(() => GitWorktrees.RemoveAsync(project.Path, worktree));
        }
        if (SelectedThread is { } selected && doomed.Contains(selected))
        {
            SelectedThread = null;
            var next = project.Threads.OrderByDescending(candidate => candidate.UpdatedAt).FirstOrDefault();
            if (next is not null)
                await SelectThreadAsync(next);
        }
        Save();
    }

    [RelayCommand]
    private void ToggleChanges()
    {
        IsChangesVisible = !IsChangesVisible;
        if (IsChangesVisible && ActiveChanges is { } changes)
            _ = changes.RefreshAsync();
    }

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (ActiveConnection is not { } connection)
            return;
        await connection.SignInAsync();
        if (connection.State != ConnectionState.Ready)
            return;
        // Vibe stores the key once for every process, so the other agents are signed in too.
        foreach (var other in Projects.Select(project => project.Connection).Append(Chief.Project.Connection).Where(other => other != connection))
            other.MarkSignedIn();
        if (SelectedThread is { } thread)
            await OpenAsync(thread);
    }

    [RelayCommand]
    private void OpenSignInUrl() => ActiveConnection?.OpenSignInUrl();

    [RelayCommand]
    private async Task SignOutAsync()
    {
        if (ActiveConnection is not { } connection)
            return;
        if (await connection.SignOutAsync())
            foreach (var project in Projects.Where(project => project.Connection != connection))
                await project.Connection.DisposeAsync();
    }

    [RelayCommand]
    private async Task ReconnectAsync()
    {
        if (ActiveConnection is not { } connection)
            return;
        await connection.ReconnectAsync();
        if (SelectedThread is { } thread)
            await OpenAsync(thread);
    }

    private async Task OpenAsync(ThreadViewModel thread)
    {
        if (thread.Transcript.Count == 0 && _store.Load(thread.Id) is { } snapshot)
            thread.Restore(snapshot);
        if (thread.IsChief)
            thread.Preamble = ChiefPreambleNow();
        await thread.EnsureSessionAsync();
    }

    private string ChiefPreambleNow()
        => ChiefPreamble + (Projects.Count == 0 ? " (none yet; ask the person to add a project)" : "\n" + string.Join("\n", Projects.Select(project => $"- {project.Name}: {project.Path}")));

    private ProjectViewModel NewProject(ProjectRecord record)
    {
        var connection = new AgentConnection(record.Path, _locateAgent, _startAgent, _openUrl, _dispatch, _clientVersion);
        var project = new ProjectViewModel(record, connection, NewChanges(record.Path), new ActionsViewModel(record.Path, _dispatch), new BrowserViewModel(_openUrl, record.BrowserUrl)) { ChangesFactory = NewChanges };
        connection.SignedIn += async (_, _) =>
        {
            if (SelectedThread is { } thread && thread.Project == project)
                await OpenAsync(thread);
        };
        return project;
    }

    private ChangesViewModel NewChanges(string path)
    {
        var changes = new ChangesViewModel(path, _dispatch);
        changes.OpenUrlRequested += (_, url) => _openUrl(url);
        return changes;
    }

    /// <summary>A thread that works in its own git worktree and branch, so it can run alongside others.</summary>
    [RelayCommand]
    public async Task NewWorktreeThreadAsync(ProjectViewModel? project)
    {
        project ??= SelectedProject;
        if (project is null || project.IsChief)
            return;
        var id = Guid.NewGuid().ToString("N");
        GitWorktrees.Worktree created;
        try
        {
            created = await Task.Run(() => GitWorktrees.CreateAsync(project.Path, WorktreeRoot, "thread-" + id[..6]));
        }
        catch (InvalidOperationException exception)
        {
            if (SelectedThread is { } current)
                current.Status = $"Could not create a worktree: {exception.Message}";
            return;
        }
        foreach (var setup in project.Actions.Actions.Where(action => action.RunOnWorktreeCreate))
            await project.Actions.RunAsync(setup, created.Path, show: false);
        var record = new ThreadRecord { Id = id, Kind = ThreadKind.Chat.ToString(), ModeId = _state.DefaultModeId, WorktreePath = created.Path, Branch = created.Branch };
        var thread = NewThread(project, record);
        Attach(project, thread);
        project.IsExpanded = true;
        await SelectThreadAsync(thread);
        Save();
    }

    private ThreadViewModel NewThread(ProjectViewModel project, ThreadRecord record)
    {
        var thread = new ThreadViewModel(project, record, ShowThoughts, McpServersFor);
        if (thread.IsOrchestrator)
            thread.Preamble = OrchestratorPreamble;
        return thread;
    }

    private void Attach(ProjectViewModel project, ThreadViewModel thread)
    {
        if (thread.ParentId is { } parentId && project.Threads.Select((candidate, index) => (candidate, index)).LastOrDefault(pair => pair.candidate.Id == parentId || pair.candidate.ParentId == parentId) is { candidate: not null } anchor)
            project.Threads.Insert(anchor.index + 1, thread);
        else
            project.Threads.Add(thread);
        thread.Changed += (_, _) =>
        {
            _store.Save(thread.Id, thread.Snapshot());
            Save();
        };
        thread.TurnCompleted += (_, result) =>
        {
            _lastTurns[thread.Id] = result;
            if (IsChangesVisible && project == SelectedProject)
                _ = project.ChangesFor(thread.WorkspacePath).RefreshAsync();
            if (thread.IsChiefTask)
                EnqueueBrief(thread, final: true);
        };
        if (thread.IsChiefTask)
            StartBriefingTimer();
    }

    // Briefings: tasks the Chief of Staff started report back to it, on a timer while they run and once when they finish.

    private void StartBriefingTimer()
    {
        if (_briefingTimer is not null)
            return;
        var timer = new CancellationTokenSource();
        _briefingTimer = timer;
        _ = Task.Run(async () =>
        {
            while (!timer.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(BriefingInterval, timer.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                _dispatch(() =>
                {
                    try
                    {
                        foreach (var task in Projects.SelectMany(project => project.Threads).Where(thread => thread.IsChiefTask && thread.IsWorking).ToList())
                            EnqueueBrief(task, final: false);
                    }
                    catch (InvalidOperationException)
                    {
                        // A tick raced a transcript change; the next tick reports it. Never let one tick end the loop.
                    }
                });
            }
        });
    }

    private void EnqueueBrief(ThreadViewModel task, bool final)
    {
        _pendingBriefs[task.Id] = ComposeBrief(task, final);
        FlushBriefs();
    }

    /// <summary>Delivers queued briefings as one prompt when the Chief of Staff is idle; otherwise they wait for its turn to end.</summary>
    private void FlushBriefs()
    {
        if (_pendingBriefs.Count == 0 || Chief.IsBusy)
            return;
        var text = "[Briefing]\n" + string.Join("\n\n", _pendingBriefs.Values);
        _pendingBriefs.Clear();
        _ = Chief.SendAsync(text, MessageRole.Briefing);
    }

    public string ComposeBrief(ThreadViewModel task, bool final)
    {
        var info = Describe(task);
        var tools = task.Transcript.OfType<ToolCallEntry>().ToList();
        var files = tools.SelectMany(tool => tool.Locations.Split('\n', StringSplitOptions.RemoveEmptyEntries)).Distinct().Take(8).ToList();
        var lastText = task.Transcript.OfType<MessageEntry>().LastOrDefault(entry => entry.IsAgent)?.Text ?? "";
        var brief = new System.Text.StringBuilder();
        brief.Append(final ? "Task finished: " : "Task progress: ").Append('"').Append(task.Title).Append("\" in ").Append(task.Project.Name)
            .Append(" (task_id ").Append(task.Id).Append(", status ").Append(info.Status).Append(')');
        if (task.Record.Branch is { } branch)
            brief.Append(", branch ").Append(branch);
        brief.Append('.');
        if (tools.Count > 0)
        {
            brief.Append(" Tool calls: ").Append(tools.Count);
            if (tools.Count(tool => tool.IsRunning) is > 0 and var running)
                brief.Append(" (").Append(running).Append(" running)");
            brief.Append(", latest: ").Append(tools[^1].Title).Append('.');
        }
        if (files.Count > 0)
            brief.Append(" Files: ").Append(string.Join(", ", files)).Append('.');
        brief.Append('\n').Append(final ? "Final reply: " : "Latest reply: ").Append(lastText.Length == 0 ? "(nothing yet)" : Trim(lastText, 1200));
        return brief.ToString();

        static string Trim(string text, int max) => text.Length <= max ? text : text[..max].TrimEnd() + " …";
    }

    private IReadOnlyList<object> McpServersFor(ThreadViewModel thread)
    {
        if (!thread.IsOrchestrator)
            return [];
        _server ??= _serverFactory(this);
        return [_server.McpServerFor(thread.Id)];
    }

    partial void OnShowThoughtsChanged(bool value)
    {
        foreach (var thread in Projects.SelectMany(project => project.Threads).Append(Chief))
            thread.SetThoughtsVisible(value);
        _state.ShowThoughts = value;
        _state.Save();
    }

    partial void OnIsChangesVisibleChanged(bool value)
    {
        if (value)
            IsBrowserVisible = false;
        _state.ShowChanges = value;
        _state.Save();
    }

    public void Save()
    {
        _state.Projects = Projects.Select(project => project.ToRecord()).ToList();
        _state.Chief = Chief.ToRecord();
        _state.SelectedThreadId = SelectedThread?.Id;
        _state.ShowThoughts = ShowThoughts;
        _state.ShowChanges = IsChangesVisible;
        _state.Save();
    }

    public async ValueTask ShutdownAsync()
    {
        _briefingTimer?.Cancel();
        foreach (var thread in Projects.SelectMany(project => project.Threads).Append(Chief))
            if (thread.Transcript.Count > 0)
                _store.Save(thread.Id, thread.Snapshot());
        Save();
        foreach (var project in Projects)
            await project.Connection.DisposeAsync();
        await Chief.Project.Connection.DisposeAsync();
        if (_server is not null)
            await _server.DisposeAsync();
    }

    // IOrchestratorHost: called from the MCP server's worker threads.

    public Task<TaskInfo> DelegateAsync(string orchestratorId, string title, string prompt, bool worktree, string? model, string? projectName, CancellationToken cancellationToken)
        => OnUiAsync(async () =>
        {
            var parent = FindThread(orchestratorId) ?? throw new InvalidOperationException("The orchestrator thread no longer exists.");
            var project = ResolveProject(parent, projectName);
            var record = new ThreadRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                Kind = ThreadKind.Task.ToString(),
                ParentId = parent.Id,
                Title = title,
                ModeId = parent.SelectedMode?.Id ?? _state.DefaultModeId,
                ModelId = model ?? parent.SelectedModel?.Value,
            };
            if (worktree)
            {
                var created = await Task.Run(() => GitWorktrees.CreateAsync(project.Path, WorktreeRoot, title, cancellationToken), cancellationToken);
                record.WorktreePath = created.Path;
                record.Branch = created.Branch;
                // vibe.re actions marked runOnWorktreeCreate set the checkout up (restore packages, copy .env…).
                foreach (var setup in project.Actions.Actions.Where(action => action.RunOnWorktreeCreate))
                    await project.Actions.RunAsync(setup, created.Path, show: false);
            }
            var child = NewThread(project, record);
            Attach(project, child);
            project.IsExpanded = true;
            Save();
            _ = child.SendAsync(prompt);
            return Describe(child);
        });

    public async Task<TaskInfo?> WaitAsync(string orchestratorId, string taskId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var child = await OnUiAsync(() => Task.FromResult(FindTask(orchestratorId, taskId)));
        if (child is null)
            return null;
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer.CancelAfter(timeout);
        try
        {
            var waiting = await OnUiAsync(() => Task.FromResult(child.WaitForTurnAsync(timer.Token)));
            if (!waiting.IsCompleted && !child.IsWorking && !child.IsOpening)
                return await OnUiAsync(() => Task.FromResult(Describe(child)));
            await waiting;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timed out: report the current state.
        }
        return await OnUiAsync(() => Task.FromResult(Describe(child)));
    }

    public Task<TaskInfo?> GetAsync(string orchestratorId, string taskId)
        => OnUiAsync(() => Task.FromResult(FindTask(orchestratorId, taskId) is { } child ? Describe(child) : null));

    public Task<IReadOnlyList<TaskInfo>> ListAsync(string orchestratorId)
        => OnUiAsync(() => Task.FromResult<IReadOnlyList<TaskInfo>>(
            FindThread(orchestratorId) is { } parent ? Projects.SelectMany(project => project.Threads).Where(thread => thread.ParentId == parent.Id).Select(Describe).ToList() : []));

    public Task<TaskInfo?> SendAsync(string orchestratorId, string taskId, string prompt, CancellationToken cancellationToken)
        => OnUiAsync(() =>
        {
            if (FindTask(orchestratorId, taskId) is not { } child)
                return Task.FromResult<TaskInfo?>(null);
            if (child.IsBusy)
                throw new InvalidOperationException("The task is still running; wait for it first.");
            _ = child.SendAsync(prompt);
            return Task.FromResult<TaskInfo?>(Describe(child) with { Status = "running" });
        });

    public Task<TaskInfo?> CancelAsync(string orchestratorId, string taskId)
        => OnUiAsync(async () =>
        {
            if (FindTask(orchestratorId, taskId) is not { } child)
                return null;
            if (child.IsWorking)
                await child.StopAsync();
            return Describe(child);
        });

    public Task<ActionOutcome> RunActionAsync(string orchestratorId, string actionName, string? projectName, CancellationToken cancellationToken)
        => OnUiAsync(async () =>
        {
            var parent = FindThread(orchestratorId) ?? throw new InvalidOperationException("The orchestrator thread no longer exists.");
            var project = ResolveProject(parent, projectName);
            var action = project.Actions.Find(actionName)
                         ?? throw new InvalidOperationException($"No action called \"{actionName}\" in {project.Name}. Available: {string.Join(", ", project.Actions.Actions.Select(action => action.Name))}.");
            if (!action.Tool)
                throw new InvalidOperationException($"The action \"{actionName}\" is not available to agents.");
            var outcome = await project.Actions.RunAsync(action, parent.WorkspacePath == project.Path ? project.Path : parent.WorkspacePath, show: project == SelectedProject);
            return outcome.Output.Length > 6000 ? outcome with { Output = "…" + outcome.Output[^6000..] } : outcome;
        });

    public Task<IReadOnlyList<ProjectAction>> ListActionsAsync(string orchestratorId, string? projectName)
        => OnUiAsync(() =>
        {
            var parent = FindThread(orchestratorId) ?? throw new InvalidOperationException("The orchestrator thread no longer exists.");
            return Task.FromResult<IReadOnlyList<ProjectAction>>(ResolveProject(parent, projectName).Actions.Actions.Where(action => action.Tool).ToList());
        });

    public Task<object> OpenInBrowserAsync(string orchestratorId, string url, string? projectName)
        => OnUiAsync(() =>
        {
            var parent = FindThread(orchestratorId) ?? throw new InvalidOperationException("The orchestrator thread no longer exists.");
            var project = ResolveProject(parent, projectName);
            project.Browser.Navigate(url);
            if (project.Browser.Error is { } error)
                throw new InvalidOperationException(error);
            if (project == SelectedProject)
                IsBrowserVisible = true;
            Save();
            return Task.FromResult<object>(new { opened = project.Browser.Source.ToString(), project = project.Name });
        });

    public async Task<IReadOnlyList<SearchHit>> SearchCodeAsync(string orchestratorId, string query, string? projectName, int maxResults, CancellationToken cancellationToken)
    {
        var root = await OnUiAsync(() =>
        {
            var parent = FindThread(orchestratorId) ?? throw new InvalidOperationException("The orchestrator thread no longer exists.");
            return Task.FromResult(parent.IsTask ? parent.WorkspacePath : ResolveProject(parent, projectName).Path);
        });
        return await CodeSearch.SearchAsync(root, query, maxResults, cancellationToken);
    }

    public async Task<object> GitHubAsync(string orchestratorId, string operation, string? projectName, IReadOnlyDictionary<string, string?> arguments, CancellationToken cancellationToken)
    {
        var (path, parent) = await OnUiAsync(() =>
        {
            var parent = FindThread(orchestratorId) ?? throw new InvalidOperationException("The orchestrator thread no longer exists.");
            var project = ResolveProject(parent, projectName);
            return Task.FromResult((parent.IsTask ? parent.WorkspacePath : project.Path, parent));
        });
        switch (operation)
        {
            case "list":
            {
                var (pulls, error) = await GitActions.ListPullRequestsAsync(path, cancellationToken);
                if (error is not null)
                    throw new InvalidOperationException(error);
                return pulls;
            }
            case "checks":
            {
                var (json, error) = await GitActions.PullRequestChecksAsync(path, arguments.GetValueOrDefault("number") ?? throw new InvalidOperationException("Missing number."), cancellationToken);
                return json is null ? throw new InvalidOperationException(error ?? "gh failed.") : System.Text.Json.JsonDocument.Parse(json).RootElement.Clone();
            }
            case "create":
            {
                var result = await GitActions.CreatePullRequestAsync(path, arguments.GetValueOrDefault("title") ?? "", arguments.GetValueOrDefault("body") ?? "",
                    string.Equals(arguments.GetValueOrDefault("draft"), "true", StringComparison.OrdinalIgnoreCase), cancellationToken);
                if (!result.Ok)
                    throw new InvalidOperationException(result.Message);
                _ = OnUiAsync(async () =>
                {
                    await parent.Project.Changes.RefreshAsync();
                    return true;
                });
                return new { url = result.Message };
            }
            default:
                throw new InvalidOperationException($"Unknown GitHub operation {operation}.");
        }
    }

    private ThreadViewModel? FindThread(string id)
        => id == ChiefId ? Chief : Projects.SelectMany(project => project.Threads).FirstOrDefault(thread => thread.Id == id);

    private ProjectViewModel ResolveProject(ThreadViewModel parent, string? name)
    {
        if (!parent.IsChief)
            return parent.Project;
        if (Projects.Count == 0)
            throw new InvalidOperationException("No projects are open. Ask the person to add a project folder first.");
        var available = "Available: " + string.Join(", ", Projects.Select(project => project.Name)) + ".";
        if (name is { Length: > 0 })
        {
            return Projects.FirstOrDefault(project => string.Equals(project.Name, name, StringComparison.OrdinalIgnoreCase))
                   ?? Projects.FirstOrDefault(project => string.Equals(project.Path.TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(name).TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal))
                   ?? throw new InvalidOperationException($"No project called \"{name}\". {available}");
        }
        if (Projects.Count == 1)
            return Projects[0];
        throw new InvalidOperationException("Name the project to run this task in. " + available);
    }

    private ThreadViewModel? FindTask(string orchestratorId, string taskId)
        => FindThread(taskId) is { } child && child.ParentId == orchestratorId ? child : null;

    private TaskInfo Describe(ThreadViewModel child)
    {
        var last = _lastTurns.GetValueOrDefault(child.Id);
        var status = child.State switch
        {
            ThreadStatus.Working or ThreadStatus.Opening => "running",
            ThreadStatus.Failed => "failed",
            _ when last is null => child.Transcript.Count == 0 ? "starting" : "idle",
            _ => last.StopReason switch
            {
                "end_turn" => "done",
                "cancelled" => "stopped",
                "error" or "not_sent" => "failed",
                var other => other,
            },
        };
        return new TaskInfo(child.Id, child.Title, status, child.Project.Name, child.WorkspacePath, child.Record.Branch, child.LastReply, last?.StopReason);
    }

    private Task<T> OnUiAsync<T>(Func<Task<T>> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatch(async () =>
        {
            try
            {
                completion.SetResult(await work());
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });
        return completion.Task;
    }
}
