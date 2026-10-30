using System.Text.Json;
using System.Text.Json.Serialization;

namespace VibeUi.Desktop.Services;

/// <summary>
/// Keeps a readable copy of each thread's transcript on disk, so a thread opens instantly after a
/// restart and still shows its history when Vibe can no longer load the session.
/// </summary>
public sealed class ThreadStore
{
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly string? _directory;

    public ThreadStore(string? directory) => _directory = directory is null ? null : Path.Combine(directory, "threads");

    public TranscriptSnapshot? Load(string threadId)
    {
        if (_directory is null)
            return null;
        var path = PathFor(threadId);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<TranscriptSnapshot>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(string threadId, TranscriptSnapshot snapshot)
    {
        if (_directory is null)
            return;
        try
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(PathFor(threadId), JsonSerializer.Serialize(snapshot, Json));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Delete(string threadId)
    {
        if (_directory is null)
            return;
        try
        {
            File.Delete(PathFor(threadId));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string PathFor(string threadId) => Path.Combine(_directory!, threadId + ".json");
}

public sealed record TranscriptSnapshot(IReadOnlyList<TranscriptRecord> Entries);

/// <summary>One stored transcript entry. <see cref="Type"/> is message, tool, plan or notice.</summary>
public sealed record TranscriptRecord(
    string Type,
    string? Role = null,
    string? Text = null,
    string? Title = null,
    string? Kind = null,
    string? Status = null,
    string? Output = null,
    string? Locations = null);
