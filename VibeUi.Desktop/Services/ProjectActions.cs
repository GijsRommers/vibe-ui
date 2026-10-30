using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace VibeUi.Desktop.Services;

/// <summary>A command a project declares in <c>vibe.re</c> (build, test, dev server…).</summary>
public sealed record ProjectAction(string Name, string Command, string? Description = null, bool RunOnWorktreeCreate = false, bool Tool = true);

public sealed record ActionOutcome(int ExitCode, string Output, bool TimedOut = false)
{
    public bool Ok => ExitCode == 0 && !TimedOut;
}

/// <summary>Reads a project's actions file and runs actions through the shell, streaming their output.</summary>
public static partial class ProjectActions
{
    public const string FileName = "vibe.re";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static IReadOnlyList<ProjectAction> Load(string projectPath)
    {
        var path = Path.Combine(projectPath, FileName);
        if (!File.Exists(path))
            return [];
        try
        {
            var file = JsonSerializer.Deserialize<ActionsFile>(File.ReadAllText(path), Json);
            return (file?.Actions ?? [])
                .Where(action => !string.IsNullOrWhiteSpace(action.Name) && !string.IsNullOrWhiteSpace(action.Command))
                .Select(action => new ProjectAction(action.Name!.Trim(), action.Command!.Trim(), action.Description, action.RunOnWorktreeCreate ?? false, action.Tool ?? true))
                .ToList();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Runs one action in <paramref name="workingDirectory"/>. Output lines go to <paramref name="progress"/> as they arrive.</summary>
    public static async Task<ActionOutcome> RunAsync(ProjectAction action, string workingDirectory, Action<string>? progress, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", action.Command } }
            : new ProcessStartInfo("/bin/bash") { ArgumentList = { "-lc", action.Command } };
        startInfo.WorkingDirectory = workingDirectory;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.Environment["NO_COLOR"] = "1";
        if (!startInfo.Environment.ContainsKey("CI"))
            startInfo.Environment["CI"] = "1";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        startInfo.Environment["PATH"] = string.Join(Path.PathSeparator, Path.Combine(home, ".local", "bin"), Environment.GetEnvironmentVariable("PATH") ?? "");

        var output = new StringBuilder();
        var gate = new object();
        void Collect(string? line)
        {
            if (line is null)
                return;
            var clean = Ansi().Replace(line, "");
            lock (gate)
            {
                if (output.Length < 200_000)
                    output.Append(clean).Append('\n');
            }
            progress?.Invoke(clean);
        }

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("The shell did not start.");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new ActionOutcome(-1, exception.Message);
        }
        using (process)
        {
            process.OutputDataReceived += (_, args) => Collect(args.Data);
            process.ErrorDataReceived += (_, args) => Collect(args.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout ?? TimeSpan.FromMinutes(30));
            try
            {
                await process.WaitForExitAsync(limit.Token);
                process.WaitForExit(); // drains the redirected streams, which WaitForExitAsync does not promise
                return new ActionOutcome(process.ExitCode, Snapshot());
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
                return new ActionOutcome(-1, Snapshot(), TimedOut: !cancellationToken.IsCancellationRequested);
            }
        }

        string Snapshot()
        {
            lock (gate)
                return output.ToString().TrimEnd();
        }
    }

    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]")]
    private static partial Regex Ansi();

    private sealed record ActionsFile(List<ActionEntry>? Actions);

    private sealed record ActionEntry(string? Name, string? Command, string? Description, bool? RunOnWorktreeCreate, bool? Tool);
}
