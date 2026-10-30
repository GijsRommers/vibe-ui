using System.Diagnostics;
using System.Text.Json;
using VibeUi.Acp;

namespace VibeUi.Desktop.Services;

/// <summary>Checks PyPI for a newer Mistral Vibe and upgrades the installed CLI in place.</summary>
public static class VibeUpdater
{
    public const string PackageName = "mistral-vibe";
    private static readonly Uri ReleaseFeed = new("https://pypi.org/pypi/mistral-vibe/json");
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static async Task<string?> LatestVersionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await Http.GetAsync(ReleaseFeed, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return document.RootElement.GetProperty("info").GetProperty("version").GetString();
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException or KeyNotFoundException)
        {
            return null;
        }
    }

    /// <summary>True when <paramref name="latest"/> is a higher release than <paramref name="installed"/>. Pre-release tags are ignored.</summary>
    public static bool IsNewer(string? installed, string? latest)
    {
        if (string.IsNullOrWhiteSpace(installed) || string.IsNullOrWhiteSpace(latest))
            return false;
        var current = Parse(installed);
        var candidate = Parse(latest);
        if (current is null || candidate is null)
            return false;
        return candidate > current;
    }

    private static Version? Parse(string text)
    {
        var digits = new string(text.Trim().TakeWhile(character => char.IsDigit(character) || character == '.').ToArray()).Trim('.');
        var parts = digits.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return null;
        while (parts.Length < 2)
            parts = [.. parts, "0"];
        return Version.TryParse(string.Join('.', parts.Take(4)), out var version) ? version : null;
    }

    public sealed record UpgradePlan(string Description, string FileName, string[] Arguments);

    /// <summary>How this machine's Vibe gets upgraded: through uv when uv installed it, otherwise Mistral's install script.</summary>
    public static UpgradePlan? Plan(string? homeDirectory = null)
    {
        homeDirectory ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var uvTool = Path.Combine(homeDirectory, ".local", "share", "uv", "tools", PackageName);
        var uv = Locate("uv", homeDirectory);
        if (Directory.Exists(uvTool) && uv is not null)
            return new UpgradePlan("uv tool upgrade mistral-vibe", uv, ["tool", "upgrade", PackageName]);
        if (OperatingSystem.IsWindows())
            return uv is not null ? new UpgradePlan("uv tool install --upgrade mistral-vibe", uv, ["tool", "install", "--upgrade", PackageName]) : null;
        return new UpgradePlan(VibeAcpLauncher.InstallCommand, "/bin/bash", ["-lc", VibeAcpLauncher.InstallCommand]);
    }

    /// <summary>Runs the upgrade, reporting output lines. Returns null on success or the failure reason.</summary>
    public static async Task<string?> UpgradeAsync(UpgradePlan plan, Action<string> progress, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(plan.FileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in plan.Arguments)
            startInfo.ArgumentList.Add(argument);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        startInfo.Environment["PATH"] = string.Join(Path.PathSeparator, Path.Combine(home, ".local", "bin"), Path.Combine(home, ".cargo", "bin"), Environment.GetEnvironmentVariable("PATH") ?? "");
        try
        {
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {plan.FileName}.");
            var lastError = "";
            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data is { Length: > 0 } line)
                    progress(line);
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data is { Length: > 0 } line)
                {
                    lastError = line;
                    progress(line);
                }
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0 ? null : (lastError.Length > 0 ? lastError : $"{plan.Description} exited with code {process.ExitCode}.");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return exception.Message;
        }
    }

    private static string? Locate(string name, string homeDirectory)
    {
        var names = OperatingSystem.IsWindows() ? new[] { name + ".exe", name } : [name];
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Append(Path.Combine(homeDirectory, ".local", "bin"))
            .Append(Path.Combine(homeDirectory, ".cargo", "bin"));
        foreach (var directory in directories)
        foreach (var candidate in names)
        {
            var path = Path.Combine(directory, candidate);
            if (File.Exists(path))
                return path;
        }
        return null;
    }
}
