using System.Text;
using System.Text.RegularExpressions;

namespace VibeUi.Desktop.Services;

/// <summary>Creates and removes git worktrees for delegated tasks, so parallel tasks do not trample one another.</summary>
public static partial class GitWorktrees
{
    public sealed record Worktree(string Path, string Branch);

    /// <summary>Adds a worktree on a new <c>vibe/&lt;slug&gt;</c> branch under <paramref name="rootDirectory"/>.</summary>
    public static async Task<Worktree> CreateAsync(string projectPath, string rootDirectory, string title, CancellationToken cancellationToken = default)
    {
        var slug = Slug(title);
        var projectName = Slug(Path.GetFileName(projectPath.TrimEnd(Path.DirectorySeparatorChar)));
        var branch = $"vibe/{slug}";
        var path = Path.Combine(rootDirectory, projectName, slug);
        var attempt = 1;
        while (Directory.Exists(path) || await BranchExistsAsync(projectPath, branch, cancellationToken))
        {
            attempt++;
            branch = $"vibe/{slug}-{attempt}";
            path = Path.Combine(rootDirectory, projectName, $"{slug}-{attempt}");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var result = await GitChanges.RunAsync(projectPath, ["worktree", "add", "-b", branch, path], cancellationToken);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(result.Error ?? "git worktree add failed.");
        return new Worktree(path, branch);
    }

    /// <summary>Removes a clean worktree. Returns false (and keeps it) when it has uncommitted work or git refuses.</summary>
    public static async Task<bool> RemoveAsync(string projectPath, string worktreePath, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(worktreePath))
        {
            await GitChanges.RunAsync(projectPath, ["worktree", "prune"], cancellationToken);
            return true;
        }
        var result = await GitChanges.RunAsync(projectPath, ["worktree", "remove", worktreePath], cancellationToken);
        return result.ExitCode == 0;
    }

    private static async Task<bool> BranchExistsAsync(string projectPath, string branch, CancellationToken cancellationToken)
        => (await GitChanges.RunAsync(projectPath, ["rev-parse", "--verify", "--quiet", "refs/heads/" + branch], cancellationToken)).ExitCode == 0;

    public static string Slug(string text)
    {
        var slug = NonSlug().Replace(text.ToLowerInvariant().Normalize(NormalizationForm.FormD), "-").Trim('-');
        if (slug.Length > 40)
            slug = slug[..40].TrimEnd('-');
        return slug.Length == 0 ? "task" : slug;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlug();
}
