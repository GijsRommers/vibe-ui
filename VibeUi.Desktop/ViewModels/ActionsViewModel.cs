using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VibeUi.Desktop.Services;

namespace VibeUi.Desktop.ViewModels;

/// <summary>One execution of a project action, with its live output.</summary>
public sealed partial class ActionRun : ObservableObject
{
    private readonly StringBuilder _buffer = new();
    private readonly CancellationTokenSource _stop = new();

    public ActionRun(ProjectAction action, string workingDirectory)
    {
        Action = action;
        WorkingDirectory = workingDirectory;
    }

    public ProjectAction Action { get; }
    public string WorkingDirectory { get; }
    public string Name => Action.Name;
    public CancellationToken StopToken => _stop.Token;

    [ObservableProperty] private string _output = "";
    [ObservableProperty] private bool _isRunning = true;
    [ObservableProperty] private int? _exitCode;
    [ObservableProperty] private string _summary = "running…";

    public bool Failed => ExitCode is not null and not 0;

    public void Append(string line)
    {
        _buffer.Append(line).Append('\n');
        Output = _buffer.ToString();
    }

    public void Finish(ActionOutcome outcome)
    {
        IsRunning = false;
        ExitCode = outcome.ExitCode;
        Summary = outcome.TimedOut ? "timed out" : outcome.ExitCode == 0 ? "done" : $"exit {outcome.ExitCode}";
        OnPropertyChanged(nameof(Failed));
    }

    [RelayCommand]
    private void Stop() => _stop.Cancel();
}

/// <summary>A project's actions (from vibe.re) and the drawer showing the latest run.</summary>
public sealed partial class ActionsViewModel : ObservableObject
{
    private readonly string _projectPath;
    private readonly Action<Action> _dispatch;

    public ActionsViewModel(string projectPath, Action<Action> dispatch)
    {
        _projectPath = projectPath;
        _dispatch = dispatch;
        Reload();
    }

    public ObservableCollection<ProjectAction> Actions { get; } = [];
    public bool HasActions => Actions.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRun))]
    private ActionRun? _currentRun;

    [ObservableProperty] private bool _isDrawerOpen;

    public bool HasRun => CurrentRun is not null;

    public void Reload()
    {
        Actions.Clear();
        foreach (var action in ProjectActions.Load(_projectPath))
            Actions.Add(action);
        OnPropertyChanged(nameof(HasActions));
    }

    public ProjectAction? Find(string name) => Actions.FirstOrDefault(action => string.Equals(action.Name, name, StringComparison.OrdinalIgnoreCase));

    [RelayCommand]
    private Task RunAsync(ProjectAction? action) => action is null ? Task.CompletedTask : RunAsync(action, _projectPath);

    /// <summary>Runs an action (in the project, or a worktree) and shows it in the drawer. Returns the outcome.</summary>
    public async Task<ActionOutcome> RunAsync(ProjectAction action, string workingDirectory, bool show = true)
    {
        var run = new ActionRun(action, workingDirectory);
        if (show)
        {
            CurrentRun?.StopCommand.Execute(null);
            CurrentRun = run;
            IsDrawerOpen = true;
        }
        var outcome = await Task.Run(() => ProjectActions.RunAsync(action, workingDirectory, line => _dispatch(() => run.Append(line)), run.StopToken));
        _dispatch(() => run.Finish(outcome));
        return outcome;
    }

    [RelayCommand]
    private void CloseDrawer() => IsDrawerOpen = false;
}
