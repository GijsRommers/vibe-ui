using System.Text.Json;
using System.Text.Json.Serialization;

namespace VibeUi.Desktop.Services;

/// <summary>
/// Everything Vibe UI remembers between runs: the projects in the sidebar, their threads (with the
/// Vibe session each one maps to) and a few window preferences. Transcripts live next to it, one
/// file per thread, through <see cref="ThreadStore"/>.
/// </summary>
public sealed class AppState
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "vibe-ui");

    private readonly string? _directory;

    public AppState(string? directory = null) => _directory = directory;

    public string? Directory => _directory;

    public List<ProjectRecord> Projects { get; set; } = [];

    public string? SelectedThreadId { get; set; }

    /// <summary>The pinned Chief of Staff thread; it lives outside the projects.</summary>
    public ThreadRecord? Chief { get; set; }

    public bool ShowThoughts { get; set; }

    public bool ShowChanges { get; set; } = true;

    /// <summary>Overrides where vibe-acp is found; empty means PATH and ~/.local/bin.</summary>
    public string? VibeExecutable { get; set; }

    /// <summary>Minutes between interim briefings from the Chief of Staff's tasks.</summary>
    public int BriefingMinutes { get; set; } = 3;

    public static AppState Load(string? directory = null)
    {
        directory ??= DefaultDirectory;
        var path = Path.Combine(directory, "state.json");
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(path), Json) is { } snapshot)
                return new AppState(directory)
                {
                    Projects = snapshot.Projects ?? [],
                    SelectedThreadId = snapshot.SelectedThreadId,
                    Chief = snapshot.Chief,
                    ShowThoughts = snapshot.ShowThoughts,
                    ShowChanges = snapshot.ShowChanges ?? true,
                    VibeExecutable = snapshot.VibeExecutable,
                    BriefingMinutes = snapshot.BriefingMinutes ?? 3,
                };
            return Migrate(directory);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged state file only loses the sidebar; Vibe still has the sessions.
            return new AppState(directory);
        }
    }

    /// <summary>The single-window version kept one workspace in settings.json; it becomes the first project.</summary>
    private static AppState Migrate(string directory)
    {
        var state = new AppState(directory);
        var legacy = Path.Combine(directory, "settings.json");
        if (!File.Exists(legacy))
            return state;
        var old = JsonSerializer.Deserialize<LegacySettings>(File.ReadAllText(legacy), Json);
        if (old?.WorkspacePath is { Length: > 0 } workspace && System.IO.Directory.Exists(workspace))
            state.Projects.Add(ProjectRecord.ForPath(workspace));
        state.ShowThoughts = old?.ShowThoughts ?? false;
        return state;
    }

    public void Save()
    {
        if (_directory is null)
            return;
        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            var snapshot = new Snapshot(Projects, SelectedThreadId, ShowThoughts, ShowChanges, Chief, VibeExecutable, BriefingMinutes);
            File.WriteAllText(Path.Combine(_directory, "state.json"), JsonSerializer.Serialize(snapshot, Json));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record Snapshot(List<ProjectRecord>? Projects, string? SelectedThreadId, bool ShowThoughts, bool? ShowChanges, ThreadRecord? Chief = null,
        string? VibeExecutable = null, int? BriefingMinutes = null);

    private sealed record LegacySettings(string? WorkspacePath, bool ShowThoughts);
}

public sealed class ProjectRecord
{
    public required string Id { get; set; }
    public required string Path { get; set; }
    public required string Name { get; set; }
    public bool IsExpanded { get; set; } = true;
    public List<ThreadRecord> Threads { get; set; } = [];

    public static ProjectRecord ForPath(string path)
    {
        var full = System.IO.Path.GetFullPath(path).TrimEnd(System.IO.Path.DirectorySeparatorChar);
        return new ProjectRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Path = full,
            Name = System.IO.Path.GetFileName(full) is { Length: > 0 } name ? name : full,
        };
    }
}

public sealed class ThreadRecord
{
    public required string Id { get; set; }
    public string? SessionId { get; set; }
    public string? Title { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? ModeId { get; set; }
    public string? ModelId { get; set; }

    /// <summary>Chat, Orchestrator or Task.</summary>
    public string Kind { get; set; } = "Chat";

    /// <summary>For a delegated task: the orchestrator thread that started it.</summary>
    public string? ParentId { get; set; }

    /// <summary>For a delegated task that runs in its own git worktree: that checkout.</summary>
    public string? WorktreePath { get; set; }

    public string? Branch { get; set; }
}
