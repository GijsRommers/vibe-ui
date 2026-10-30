using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace VibeUi.Desktop.Services;

public sealed record SearchHit(string Path, double Score, IReadOnlyList<SearchLine> Lines);

public sealed record SearchLine(int Number, string Text);

/// <summary>
/// A local, offline code index per project: tokens from identifiers (camelCase and snake_case split),
/// ranked with BM25. Nothing leaves the machine. Rebuilt when it is older than a minute.
/// </summary>
public static partial class CodeSearch
{
    private const long MaxFileBytes = 1_000_000;
    private const int MaxFiles = 20_000;
    private static readonly TimeSpan Freshness = TimeSpan.FromSeconds(60);
    private static readonly ConcurrentDictionary<string, Index> Indexes = new(StringComparer.Ordinal);
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
        { ".git", "node_modules", "bin", "obj", "dist", "build", "target", ".venv", "venv", "__pycache__", ".next", ".idea", ".vs", "vendor", "Pods", "DerivedData" };

    public static async Task<IReadOnlyList<SearchHit>> SearchAsync(string root, string query, int maxResults = 10, CancellationToken cancellationToken = default)
    {
        var terms = Tokenize(query).Distinct().ToList();
        if (terms.Count == 0)
            return [];
        var index = await GetIndexAsync(root, cancellationToken);
        var scores = new Dictionary<int, double>();
        foreach (var term in terms)
        {
            if (!index.Postings.TryGetValue(term, out var postings))
                continue;
            var idf = Math.Log(1 + (index.Documents.Count - postings.Count + 0.5) / (postings.Count + 0.5));
            foreach (var (document, frequency) in postings)
            {
                var length = index.Documents[document].TokenCount;
                var tf = frequency * 2.2 / (frequency + 1.2 * (0.25 + 0.75 * length / index.AverageLength));
                scores[document] = scores.GetValueOrDefault(document) + idf * tf;
            }
        }
        var hits = new List<SearchHit>();
        foreach (var (document, score) in scores.OrderByDescending(pair => pair.Value).Take(maxResults))
        {
            var entry = index.Documents[document];
            hits.Add(new SearchHit(entry.RelativePath, Math.Round(score, 2), await MatchingLinesAsync(Path.Combine(root, entry.RelativePath), terms, cancellationToken)));
        }
        return hits;
    }

    public static void Forget(string root) => Indexes.TryRemove(root, out _);

    private static async Task<Index> GetIndexAsync(string root, CancellationToken cancellationToken)
    {
        if (Indexes.TryGetValue(root, out var cached) && DateTimeOffset.UtcNow - cached.BuiltAt < Freshness)
            return cached;
        var built = await Task.Run(() => Build(root, cancellationToken), cancellationToken);
        Indexes[root] = built;
        return built;
    }

    private static Index Build(string root, CancellationToken cancellationToken)
    {
        var files = ListFiles(root).Take(MaxFiles).ToList();
        var documents = new List<Document>();
        var postings = new Dictionary<string, Dictionary<int, int>>(StringComparer.Ordinal);
        long totalTokens = 0;
        foreach (var relative in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var full = Path.Combine(root, relative);
            string text;
            try
            {
                var info = new FileInfo(full);
                if (!info.Exists || info.Length > MaxFileBytes)
                    continue;
                text = File.ReadAllText(full);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            if (text.Contains('\0'))
                continue; // binary
            var id = documents.Count;
            var count = 0;
            foreach (var token in Tokenize(text).Concat(Tokenize(relative)))
            {
                count++;
                if (!postings.TryGetValue(token, out var perDocument))
                    postings[token] = perDocument = new Dictionary<int, int>();
                perDocument[id] = perDocument.GetValueOrDefault(id) + 1;
            }
            documents.Add(new Document(relative, count));
            totalTokens += count;
        }
        return new Index(documents, postings.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<(int, int)>)pair.Value.Select(entry => (entry.Key, entry.Value)).ToList(), StringComparer.Ordinal),
            documents.Count == 0 ? 1 : (double)totalTokens / documents.Count, DateTimeOffset.UtcNow);
    }

    /// <summary>Files git tracks or has not ignored; a plain directory walk for folders that are not repositories.</summary>
    private static IEnumerable<string> ListFiles(string root)
    {
        try
        {
            var startInfo = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "ls-files", "--cached", "--others", "--exclude-standard", "-z" })
                startInfo.ArgumentList.Add(argument);
            using var process = Process.Start(startInfo);
            if (process is not null)
            {
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode == 0)
                    return output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(path => !path.Split('/').Any(SkippedDirectories.Contains)).ToList();
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
        return Walk(root);
    }

    private static IEnumerable<string> Walk(string root)
    {
        var pending = new Stack<string>([root]);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                if (Directory.Exists(entry))
                {
                    if (!SkippedDirectories.Contains(name) && !name.StartsWith('.'))
                        pending.Push(entry);
                }
                else
                {
                    yield return Path.GetRelativePath(root, entry).Replace('\\', '/');
                }
            }
        }
    }

    private static async Task<IReadOnlyList<SearchLine>> MatchingLinesAsync(string path, IReadOnlyList<string> terms, CancellationToken cancellationToken)
    {
        var lines = new List<SearchLine>();
        try
        {
            var number = 0;
            foreach (var line in await File.ReadAllLinesAsync(path, cancellationToken))
            {
                number++;
                var tokens = Tokenize(line).ToHashSet(StringComparer.Ordinal);
                if (terms.Any(tokens.Contains))
                {
                    lines.Add(new SearchLine(number, line.Length > 200 ? line[..200] + "…" : line.Trim()));
                    if (lines.Count == 6)
                        break;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
        return lines;
    }

    /// <summary>Lower-cased word tokens; identifiers also split on camelCase and snake_case.</summary>
    public static IEnumerable<string> Tokenize(string text)
    {
        foreach (Match match in Word().Matches(text))
        {
            var word = match.Value;
            var parts = CamelBoundary().Split(word).SelectMany(part => part.Split('_', StringSplitOptions.RemoveEmptyEntries)).ToList();
            if (parts.Count > 1)
                yield return word.ToLowerInvariant();
            foreach (var part in parts)
                if (part.Length > 1)
                    yield return part.ToLowerInvariant();
        }
    }

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*")]
    private static partial Regex Word();

    [GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])")]
    private static partial Regex CamelBoundary();

    private sealed record Document(string RelativePath, int TokenCount);

    private sealed record Index(IReadOnlyList<Document> Documents, IReadOnlyDictionary<string, IReadOnlyList<(int Document, int Frequency)>> Postings, double AverageLength, DateTimeOffset BuiltAt);
}
