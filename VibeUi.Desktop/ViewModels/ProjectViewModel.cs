using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using VibeUi.Desktop.Services;

namespace VibeUi.Desktop.ViewModels;

/// <summary>A folder in the sidebar: its threads, its agent process and its git changes.</summary>
public sealed partial class ProjectViewModel : ObservableObject
{
    public ProjectViewModel(ProjectRecord record, AgentConnection connection, ChangesViewModel changes, bool isChief = false)
    {
        Record = record;
        Connection = connection;
        Changes = changes;
        IsChief = isChief;
        _isExpanded = record.IsExpanded;
    }

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
