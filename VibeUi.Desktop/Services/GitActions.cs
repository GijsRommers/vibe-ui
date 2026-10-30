using System.Text.Json;
using System.Text.Json.Serialization;

namespace VibeUi.Desktop.Services;

public sealed record PullRequest(int Number, string Title, string State, string Url, string HeadRefName, bool IsDraft, DateTimeOffset UpdatedAt)
{
    public string Label => IsDraft ? $"#{Number} · draft" : $"#{Number}";
}

public sealed record ActionResult(bool Ok, string Message)
{
    public static ActionResult Success(string message) => new(true, message);
    public static ActionResult Failure(string message) => new(false, message);
}

/// <summary>Commit, push and pull-request actions for a project. Pull requests go through the GitHub CLI (<c>gh</c>).</summary>
public static class GitActions
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    /// <summary>Stages everything and commits. Returns the short hash on success.</summary>
    public static async Task<ActionResult> CommitAsync(string directory, string message, CancellationToken cancellationToken = default)
    {
        message = message.Trim();
        if (message.Length == 0)
            return ActionResult.Failure("Write a commit message first.");
        var add = await GitChanges.RunAsync(directory, ["add", "-A"], cancellationToken);
        if (add.ExitCode != 0)
            return ActionResult.Failure(add.Error ?? "git add failed.");
        var commit = await GitChanges.RunAsync(directory, ["commit", "-q", "-m", message], cancellationToken);
        if (commit.ExitCode != 0)
            return ActionResult.Failure(FirstLine(commit.Error ?? commit.Output, "git commit failed."));
        var hash = (await GitChanges.RunAsync(directory, ["rev-parse", "--short", "HEAD"], cancellationToken)).Output.Trim();
        return ActionResult.Success($"Committed {hash}.");
    }

    /// <summary>Pushes the current branch, setting its upstream on first push.</summary>
    public static async Task<ActionResult> PushAsync(string directory, CancellationToken cancellationToken = default)
    {
        var push = await GitChanges.RunAsync(directory, ["push", "-u", "origin", "HEAD"], cancellationToken);
        return push.ExitCode == 0 ? ActionResult.Success("Pushed.") : ActionResult.Failure(FirstLine(push.Error ?? push.Output, "git push failed."));
    }

    /// <summary>Ahead/behind summary of the branch against its upstream, or null without one.</summary>
    public static async Task<string?> UpstreamStatusAsync(string directory, CancellationToken cancellationToken = default)
    {
        var result = await GitChanges.RunAsync(directory, ["rev-list", "--left-right", "--count", "@{upstream}...HEAD"], cancellationToken);
        if (result.ExitCode != 0)
            return null;
        var parts = result.Output.Trim().Split('\t', ' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !int.TryParse(parts[0], out var behind) || !int.TryParse(parts[1], out var ahead))
            return null;
        return (ahead, behind) switch
        {
            (0, 0) => "in sync with upstream",
            (_, 0) => $"{ahead} to push",
            (0, _) => $"{behind} behind",
            _ => $"{ahead} to push, {behind} behind",
        };
    }

    public static async Task<(IReadOnlyList<PullRequest> Pulls, string? Error)> ListPullRequestsAsync(string directory, CancellationToken cancellationToken = default)
    {
        var result = await RunGhAsync(directory, ["pr", "list", "--state", "open", "--limit", "20", "--json", "number,title,state,url,headRefName,isDraft,updatedAt"], cancellationToken);
        if (result.ExitCode != 0)
            return ([], FirstLine(result.Error ?? result.Output, "gh pr list failed."));
        try
        {
            return (JsonSerializer.Deserialize<List<PullRequest>>(result.Output, Json) ?? [], null);
        }
        catch (JsonException exception)
        {
            return ([], exception.Message);
        }
    }

    /// <summary>Raw JSON from <c>gh pr view</c> with checks and review state, or an error.</summary>
    public static async Task<(string? Json, string? Error)> PullRequestChecksAsync(string directory, string number, CancellationToken cancellationToken = default)
    {
        var result = await RunGhAsync(directory, ["pr", "view", number, "--json", "number,title,state,isDraft,mergeable,reviewDecision,statusCheckRollup,url,headRefName"], cancellationToken);
        return result.ExitCode == 0 ? (result.Output, null) : (null, FirstLine(result.Error ?? result.Output, "gh pr view failed."));
    }

    /// <summary>Opens a pull request for the current branch. Pushes first when the branch has no upstream.</summary>
    public static async Task<ActionResult> CreatePullRequestAsync(string directory, string title, string body, bool draft, CancellationToken cancellationToken = default)
    {
        title = title.Trim();
        if (title.Length == 0)
            return ActionResult.Failure("Give the pull request a title.");
        if (await UpstreamStatusAsync(directory, cancellationToken) is null)
        {
            var push = await PushAsync(directory, cancellationToken);
            if (!push.Ok)
                return push;
        }
        var arguments = new List<string> { "pr", "create", "--title", title, "--body", body };
        if (draft)
            arguments.Add("--draft");
        var result = await RunGhAsync(directory, [.. arguments], cancellationToken);
        return result.ExitCode == 0
            ? ActionResult.Success(result.Output.Trim().Split('\n').LastOrDefault(line => line.StartsWith("http", StringComparison.Ordinal)) ?? "Pull request created.")
            : ActionResult.Failure(FirstLine(result.Error ?? result.Output, "gh pr create failed."));
    }

    private static string FirstLine(string text, string fallback)
    {
        var line = text.Split('\n').Select(part => part.Trim()).FirstOrDefault(part => part.Length > 0);
        return line ?? fallback;
    }

    private static async Task<GitChanges.GitResult> RunGhAsync(string directory, string[] arguments, CancellationToken cancellationToken)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("gh")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.Environment["GH_PROMPT_DISABLED"] = "1";
        startInfo.Environment["NO_COLOR"] = "1";
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        try
        {
            using var process = System.Diagnostics.Process.Start(startInfo) ?? throw new InvalidOperationException("gh did not start.");
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return new GitChanges.GitResult(process.ExitCode, await output, (await error).Trim() is { Length: > 0 } message ? message : null);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new GitChanges.GitResult(-1, "", "The GitHub CLI (gh) is not installed or not on PATH.");
        }
    }
}
