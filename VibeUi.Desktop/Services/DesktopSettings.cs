using System.Text.Json;

namespace VibeUi.Desktop.Services;

/// <summary>Per-user preferences, kept in a small JSON file next to other app data.</summary>
public sealed class DesktopSettings
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "vibe-ui", "settings.json");

    private readonly string? _path;

    public DesktopSettings(string? path = null) => _path = path;

    public string? WorkspacePath { get; set; }

    public bool ShowThoughts { get; set; }

    public static DesktopSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(path)) is { } snapshot)
                return new DesktopSettings(path) { WorkspacePath = snapshot.WorkspacePath, ShowThoughts = snapshot.ShowThoughts };
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged settings file only loses preferences.
        }
        return new DesktopSettings(path);
    }

    public void Save()
    {
        if (_path is null)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(new Snapshot(WorkspacePath, ShowThoughts)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record Snapshot(string? WorkspacePath, bool ShowThoughts);
}
