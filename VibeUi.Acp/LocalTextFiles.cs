namespace VibeUi.Acp;

/// <summary>Default <c>fs/read_text_file</c> and <c>fs/write_text_file</c> behaviour against the local disk.</summary>
public static class LocalTextFiles
{
    public static async Task<ReadTextFileResponse> ReadAsync(ReadTextFileRequest request, CancellationToken cancellationToken)
    {
        var text = await File.ReadAllTextAsync(request.Path, cancellationToken);
        if (request.Line is null && request.Limit is null)
            return new ReadTextFileResponse(text);
        var lines = text.Split('\n');
        var start = Math.Clamp((request.Line ?? 1) - 1, 0, lines.Length);
        var count = Math.Clamp(request.Limit ?? lines.Length - start, 0, lines.Length - start);
        return new ReadTextFileResponse(string.Join('\n', lines, start, count));
    }

    public static async Task WriteAsync(WriteTextFileRequest request, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(request.Path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(request.Path, request.Content, cancellationToken);
    }
}
