using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VibeUi.Desktop.Services;

namespace VibeUi.Desktop.ViewModels;

/// <summary>The Changes pane for one project: the working tree's changed files and the diff of the selected one.</summary>
public sealed partial class ChangesViewModel : ObservableObject
{
    private readonly string _path;
    private readonly Action<Action> _dispatch;
    private bool _refreshing;
    private bool _refreshAgain;
    private Task _current = Task.CompletedTask;
    private int _diffGeneration;

    public ChangesViewModel(string path, Action<Action> dispatch)
    {
        _path = path;
        _dispatch = dispatch;
    }

    public ObservableCollection<ChangedFile> Files { get; } = [];
    public ObservableCollection<DiffLine> DiffLines { get; } = [];

    [ObservableProperty] private string? _branch;
    [ObservableProperty] private bool _isRepository;
    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _hasChanges;
    [ObservableProperty] private bool _loaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedFile))]
    private ChangedFile? _selectedFile;

    public bool HasSelectedFile => SelectedFile is not null;

    // Actions: commit, push, pull requests.
    public ObservableCollection<PullRequest> PullRequests { get; } = [];

    [ObservableProperty] private string _commitMessage = "";
    [ObservableProperty] private string? _upstream;
    [ObservableProperty] private string _actionStatus = "";
    [ObservableProperty] private bool _isActing;
    [ObservableProperty] private string? _pullRequestError;
    [ObservableProperty] private bool _hasPullRequests;
    [ObservableProperty] private bool _isPullRequestFormOpen;
    [ObservableProperty] private string _pullRequestTitle = "";
    [ObservableProperty] private string _pullRequestBody = "";
    [ObservableProperty] private bool _pullRequestDraft;

    /// <summary>The open pull request for this checkout's branch, if any.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurrentPullRequest), nameof(CanMarkReady))]
    private PullRequest? _currentPullRequest;

    public bool HasCurrentPullRequest => CurrentPullRequest is not null;
    public bool CanMarkReady => CurrentPullRequest is { IsDraft: true };

    [RelayCommand]
    private Task MarkReadyAsync() => CurrentPullRequest is { } pull ? RunActionAsync(() => GitActions.MarkReadyAsync(_path, pull.Number)) : Task.CompletedTask;

    [RelayCommand]
    private Task MergeAsync() => CurrentPullRequest is { } pull ? RunActionAsync(() => GitActions.MergeAsync(_path, pull.Number)) : Task.CompletedTask;

    [RelayCommand]
    private void OpenCurrentPullRequest()
    {
        if (CurrentPullRequest is { } pull)
            OpenUrlRequested?.Invoke(this, pull.Url);
    }

    /// <summary>Raised with a URL the view should open in the browser.</summary>
    public event EventHandler<string>? OpenUrlRequested;

    [RelayCommand]
    private Task CommitAsync() => RunActionAsync(async () =>
    {
        var result = await GitActions.CommitAsync(_path, CommitMessage);
        if (result.Ok)
            _dispatch(() => CommitMessage = "");
        return result;
    });

    [RelayCommand]
    private Task PushAsync() => RunActionAsync(() => GitActions.PushAsync(_path));

    [RelayCommand]
    private void TogglePullRequestForm()
    {
        IsPullRequestFormOpen = !IsPullRequestFormOpen;
        if (IsPullRequestFormOpen && PullRequestTitle.Length == 0 && Branch is { } branch)
            PullRequestTitle = branch.Replace('-', ' ').Replace('/', ' ').Replace('_', ' ');
    }

    [RelayCommand]
    private Task CreatePullRequestAsync() => RunActionAsync(async () =>
    {
        var result = await GitActions.CreatePullRequestAsync(_path, PullRequestTitle, PullRequestBody, PullRequestDraft);
        if (result.Ok)
            _dispatch(() =>
            {
                IsPullRequestFormOpen = false;
                PullRequestTitle = "";
                PullRequestBody = "";
                if (result.Message.StartsWith("http", StringComparison.Ordinal))
                    OpenUrlRequested?.Invoke(this, result.Message);
            });
        return result;
    });

    [RelayCommand]
    private void OpenPullRequest(PullRequest? pull)
    {
        if (pull is not null)
            OpenUrlRequested?.Invoke(this, pull.Url);
    }

    private async Task RunActionAsync(Func<Task<ActionResult>> action)
    {
        if (IsActing)
            return;
        IsActing = true;
        try
        {
            var result = await Task.Run(action);
            _dispatch(() => ActionStatus = result.Message);
        }
        finally
        {
            _dispatch(() => IsActing = false);
            await RefreshAsync();
        }
    }

    private async Task RefreshActionsAsync()
    {
        var upstream = await GitActions.UpstreamStatusAsync(_path);
        var (pulls, error) = await GitActions.ListPullRequestsAsync(_path);
        _dispatch(() =>
        {
            Upstream = upstream;
            PullRequestError = error;
            PullRequests.Clear();
            foreach (var pull in pulls)
                PullRequests.Add(pull);
            HasPullRequests = PullRequests.Count > 0;
            CurrentPullRequest = pulls.FirstOrDefault(pull => pull.HeadRefName == Branch);
        });
    }

    [RelayCommand]
    public Task RefreshAsync()
    {
        if (_refreshing)
        {
            _refreshAgain = true;
            return _current; // the running refresh loops once more and covers this request
        }
        return _current = RefreshCoreAsync();
    }

    private async Task RefreshCoreAsync()
    {
        _refreshing = true;
        IsRefreshing = true;
        try
        {
            do
            {
                _refreshAgain = false;
                var snapshot = await Task.Run(() => GitChanges.LoadAsync(_path));
                var completed = new TaskCompletionSource();
                _dispatch(() =>
                {
                    Apply(snapshot);
                    completed.SetResult();
                });
                await completed.Task;
                if (snapshot.IsRepository)
                    await Task.Run(RefreshActionsAsync);
            }
            while (_refreshAgain);
        }
        finally
        {
            _refreshing = false;
            _dispatch(() =>
            {
                IsRefreshing = false;
                Loaded = true;
            });
        }
    }

    private void Apply(GitSnapshot snapshot)
    {
        IsRepository = snapshot.IsRepository;
        Branch = snapshot.Branch;
        Error = snapshot.Error;
        var selectedPath = SelectedFile?.Path;
        Files.Clear();
        foreach (var file in snapshot.Files)
            Files.Add(file);
        HasChanges = Files.Count > 0;
        var added = snapshot.Files.Sum(file => file.Added);
        var removed = snapshot.Files.Sum(file => file.Removed);
        Summary = snapshot.Files.Count == 0
            ? (snapshot.IsRepository ? "No changes" : "")
            : string.Create(CultureInfo.InvariantCulture, $"{snapshot.Files.Count} {(snapshot.Files.Count == 1 ? "file" : "files")} · +{added} −{removed}");
        SelectedFile = Files.FirstOrDefault(file => file.Path == selectedPath) ?? Files.FirstOrDefault();
        if (SelectedFile is not null)
            _ = LoadDiffAsync(SelectedFile);
    }

    partial void OnSelectedFileChanged(ChangedFile? value)
    {
        if (value is null)
            DiffLines.Clear();
        else
            _ = LoadDiffAsync(value);
    }

    private async Task LoadDiffAsync(ChangedFile file)
    {
        var generation = ++_diffGeneration;
        var lines = await Task.Run(() => GitChanges.DiffAsync(_path, file));
        _dispatch(() =>
        {
            if (generation != _diffGeneration)
                return;
            DiffLines.Clear();
            foreach (var line in lines)
                DiffLines.Add(line);
        });
    }
}
