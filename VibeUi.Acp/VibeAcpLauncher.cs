using System.Diagnostics;

namespace VibeUi.Acp;

/// <summary>Finds and starts Mistral Vibe's ACP entry point, <c>vibe-acp</c>.</summary>
public static class VibeAcpLauncher
{
    public const string ExecutableOverride = "VIBE_ACP_EXECUTABLE";
    public const string ExecutableName = "vibe-acp";
    public const string InstallCommand = "curl -LsSf https://mistral.ai/vibe/install.sh | bash";

    /// <summary>
    /// Resolves the executable: <c>VIBE_ACP_EXECUTABLE</c>, then <c>PATH</c>, then <c>~/.local/bin</c>
    /// (where the Vibe installer and <c>uv tool install</c> put it; GUI apps often start without that on PATH).
    /// </summary>
    public static string? Locate(Func<string, string?>? environment = null, string? homeDirectory = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        homeDirectory ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configured = environment(ExecutableOverride);
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return Path.GetFullPath(configured);
        var directories = (environment("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Append(Path.Combine(homeDirectory, ".local", "bin"));
        foreach (var directory in directories)
        foreach (var name in CandidateNames())
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }
        return null;
    }

    private static IEnumerable<string> CandidateNames()
        => OperatingSystem.IsWindows() ? [ExecutableName + ".exe", ExecutableName + ".cmd", ExecutableName] : [ExecutableName];

    /// <summary>Starts the agent in <paramref name="workingDirectory"/> and returns a client that owns the process.</summary>
    public static AcpClient Start(string executable, string workingDirectory, IAcpClientHandler handler, Action<string>? standardError = null)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {executable}.");
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
                standardError?.Invoke(args.Data);
        };
        process.BeginErrorReadLine();
        // The agent must not outlive the window, however the window goes away (SIGTERM, unhandled crash).
        EventHandler onExit = (_, _) => Kill(process);
        AppDomain.CurrentDomain.ProcessExit += onExit;
        return new AcpClient(process.StandardOutput.BaseStream, process.StandardInput.BaseStream, handler, () =>
        {
            AppDomain.CurrentDomain.ProcessExit -= onExit;
            Kill(process);
            process.Dispose();
            return ValueTask.CompletedTask;
        });
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }
}
