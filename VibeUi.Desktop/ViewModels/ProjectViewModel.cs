using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using VibeUi.Desktop.Services;

namespace VibeUi.Desktop.ViewModels;

/// <summary>A folder in the sidebar: its threads, its agent process and its git changes.</summary>
public sealed partial class ProjectViewModel : ObservableObject
{
    public ProjectViewModel(ProjectRecord record, AgentConnection connection, ChangesViewModel changes, ActionsViewModel actions, BrowserViewModel browser, bool isChief = false)
    {
        Record = record;
        Connection = connection;
        Changes = changes;
        Actions = actions;
        Browser = browser;
        IsChief = isChief;
        _isExpanded = record.IsExpanded;
        browser.Navigated += (_, url) => Record.BrowserUrl = url;
    }

    /// <summary>The built-in browser pane for this project (dev servers, docs).</summary>
    public BrowserViewModel Browser { get; }

    /// <summary>The project's vibe.re actions and their output drawer.</summary>
    public ActionsViewModel Actions { get; }

    private readonly Dictionary<string, ChangesViewModel> _worktreeChanges = new(StringComparer.Ordinal);

    /// <summary>The Changes pane for a checkout: the project itself, or one of its worktrees.</summary>
    public ChangesViewModel ChangesFor(string workspacePath)
    {
        if (string.Equals(workspacePath, Path, StringComparison.Ordinal))
            return Changes;
        if (!_worktreeChanges.TryGetValue(workspacePath, out var changes))
            _worktreeChanges[workspacePath] = changes = ChangesFactory(workspacePath);
        return changes;
    }

    /// <summary>Set by the shell so worktree panes share its dispatcher and URL opener.</summary>
    public Func<string, ChangesViewModel> ChangesFactory { get; set; } = path => throw new InvalidOperationException("No changes factory.");

    /// <summary>The hidden workspace that hosts the Chief of Staff; not listed in the sidebar.</summary>
    public bool IsChief { get; }

    public ProjectRecord Record { get; }
    public string Id => Record.Id;
    public string Path => Record.Path;
    public string Name => Record.Name;
    public AgentConnection Connection { get; }
    public ChangesViewModel Changes { get; }
    public ObservableCollection<ThreadViewModel> Threads { get; } = [];

    [ObservableProperty] private bool _isExpanded;

    public ProjectRecord ToRecord()
    {
        Record.IsExpanded = IsExpanded;
        Record.Threads = Threads.Select(thread => thread.ToRecord()).ToList();
        return Record;
    }
}
