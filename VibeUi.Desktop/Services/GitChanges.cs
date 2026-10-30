using System.Diagnostics;
using System.Text;

namespace VibeUi.Desktop.Services;

public enum DiffLineKind
{
    Meta,
    Hunk,
    Context,
    Added,
    Removed,
}

public sealed record DiffLine(DiffLineKind Kind, string Text);

/// <summary>A file the working tree changed relative to HEAD. <see cref="Status"/> is git's short code (M, A, D, R, ?).</summary>
public sealed record ChangedFile(string Path, string Status, int Added, int Removed)
{
    public bool IsUntracked => Status == "?";
    public bool IsDeleted => Status == "D";
    public string Name => System.IO.Path.GetFileName(Path) is { Length: > 0 } name ? name : Path;
    public string Directory => System.IO.Path.GetDirectoryName(Path) is { Length: > 0 } directory ? directory : "";
}

public sealed record GitSnapshot(bool IsRepository, string? Branch, IReadOnlyList<ChangedFile> Files, string? Error = null)
{
    public static GitSnapshot None(string? error = null) => new(false, null, [], error);
}

/// <summary>Reads what the agent changed in a project by shelling out to git. Read-only: it never alters the tree.</summary>
public static class GitChanges
{
    public static async Task<GitSnapshot> LoadAsync(string directory, CancellationToken cancellationToken = default)
    {
        var inside = await RunAsync(directory, ["rev-parse", "--is-inside-work-tree"], cancellationToken);
        if (inside.ExitCode != 0 || inside.Output.Trim() != "true")
            return GitSnapshot.None(inside.Error);
        var branch = (await RunAsync(directory, ["rev-parse", "--abbrev-ref", "HEAD"], cancellationToken)).Output.Trim();
        if (branch.Length == 0 || branch == "HEAD")
            branch = (await RunAsync(directory, ["rev-parse", "--short", "HEAD"], cancellationToken)).Output.Trim() is { Length: > 0 } sha ? sha : "no commits";
        var status = await RunAsync(directory, ["status", "--porcelain", "--untracked-files=all"], cancellationToken);
        var numstat = await RunAsync(directory, ["diff", "--numstat", "HEAD"], cancellationToken);
        var counts = ParseNumstat(numstat.Output);
        var files = new List<ChangedFile>();
        foreach (var line in status.Output.Split('\n'))
        {
            if (line.Length < 4)
                continue;
            var code = line[..2];
            var path = line[3..];
            if (path.Contains(" -> ", StringComparison.Ordinal))
                path = path[(path.IndexOf(" -> ", StringComparison.Ordinal) + 4)..];
            path = Unquote(path);
            var shortCode = code switch
            {
                "??" => "?",
                _ when code[0] != ' ' && code[0] != '?' => code[0].ToString(),
                _ => code[1].ToString(),
            };
            var (added, removed) = counts.GetValueOrDefault(path);
            if (shortCode == "?" && added == 0)
                added = await CountLinesAsync(Path.Combine(directory, path), cancellationToken);
            files.Add(new ChangedFile(path, shortCode, added, removed));
        }
        files.Sort((left, right) => string.CompareOrdinal(left.Path, right.Path));
        return new GitSnapshot(true, branch, files);
    }

    public static async Task<IReadOnlyList<DiffLine>> DiffAsync(string directory, ChangedFile file, CancellationToken cancellationToken = default)
    {
        var result = file.IsUntracked
            ? await RunAsync(directory, ["diff", "--no-color", "--no-index", "--", NullDevice, file.Path], cancellationToken)
            : await RunAsync(directory, ["diff", "--no-color", "HEAD", "--", file.Path], cancellationToken);
        return ParseDiff(result.Output);
    }

    public static IReadOnlyList<DiffLine> ParseDiff(string text)
    {
        var lines = new List<DiffLine>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 && lines.Count == 0)
                continue;
            var kind = line switch
            {
                _ when line.StartsWith("@@", StringComparison.Ordinal) => DiffLineKind.Hunk,
                _ when line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal) => DiffLineKind.Meta,
                _ when line.StartsWith("diff ", StringComparison.Ordinal) || line.StartsWith("index ", StringComparison.Ordinal)
                       || line.StartsWith("new file", StringComparison.Ordinal) || line.StartsWith("deleted file", StringComparison.Ordinal)
                       || line.StartsWith("similarity", StringComparison.Ordinal) || line.StartsWith("rename ", StringComparison.Ordinal)
                       || line.StartsWith("\\ No newline", StringComparison.Ordinal) => DiffLineKind.Meta,
                _ when line.StartsWith('+') => DiffLineKind.Added,
                _ when line.StartsWith('-') => DiffLineKind.Removed,
                _ => DiffLineKind.Context,
            };
            var shown = kind is DiffLineKind.Added or DiffLineKind.Removed or DiffLineKind.Context && line.Length > 0 ? line[1..] : line;
            lines.Add(new DiffLine(kind, shown));
        }
        while (lines.Count > 0 && lines[^1].Kind == DiffLineKind.Context && lines[^1].Text.Length == 0)
            lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private static string NullDevice => OperatingSystem.IsWindows() ? "NUL" : "/dev/null";

    private static Dictionary<string, (int Added, int Removed)> ParseNumstat(string output)
    {
        var counts = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Split('\t');
            if (parts.Length < 3)
                continue;
            var path = parts[2];
            if (path.Contains(" => ", StringComparison.Ordinal))
                path = path.Contains('{') ? ResolveRename(path) : path[(path.IndexOf(" => ", StringComparison.Ordinal) + 4)..];
            counts[Unquote(path)] = (int.TryParse(parts[0], out var added) ? added : 0, int.TryParse(parts[1], out var removed) ? removed : 0);
        }
        return counts;
    }

    /// <summary>Turns git's <c>dir/{old => new}/file</c> rename shorthand into the new path.</summary>
    private static string ResolveRename(string path)
    {
        var open = path.IndexOf('{');
        var close = path.IndexOf('}', open + 1);
        if (open < 0 || close < 0)
            return path;
        var inner = path[(open + 1)..close];
        var target = inner[(inner.IndexOf(" => ", StringComparison.Ordinal) + 4)..];
        return (path[..open] + target + path[(close + 1)..]).Replace("//", "/");
    }

    private static string Unquote(string path)
        => path.Length >= 2 && path[0] == '"' && path[^1] == '"' ? path[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\") : path;

    private static async Task<int> CountLinesAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 4 * 1024 * 1024)
                return 0;
            var text = await File.ReadAllTextAsync(path, cancellationToken);
            return text.Length == 0 ? 0 : text.Count(character => character == '\n') + (text.EndsWith('\n') ? 0 : 1);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    internal sealed record GitResult(int ExitCode, string Output, string? Error);

    internal static async Task<GitResult> RunAsync(string directory, string[] arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.quotepath=false");
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("git did not start.");
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return new GitResult(process.ExitCode, await output, (await error).Trim() is { Length: > 0 } message ? message : null);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new GitResult(-1, "", "git is not installed or not on PATH.");
        }
    }
}
