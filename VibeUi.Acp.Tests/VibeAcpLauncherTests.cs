namespace VibeUi.Acp.Tests;

public sealed class VibeAcpLauncherTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vibe-ui-launcher").FullName;

    [Fact]
    public void PrefersTheExplicitOverride()
    {
        var configured = Touch("custom", "vibe-acp");
        Touch("onpath", "vibe-acp");

        var located = VibeAcpLauncher.Locate(Env(("VIBE_ACP_EXECUTABLE", configured), ("PATH", Path.Combine(_root, "onpath"))), _root);

        Assert.Equal(configured, located);
    }

    [Fact]
    public void IgnoresAMissingOverrideAndSearchesPath()
    {
        var onPath = Touch("onpath", "vibe-acp");

        var located = VibeAcpLauncher.Locate(Env(("VIBE_ACP_EXECUTABLE", Path.Combine(_root, "gone")), ("PATH", Path.Combine(_root, "empty") + Path.PathSeparator + Path.Combine(_root, "onpath"))), _root);

        Assert.Equal(onPath, located);
    }

    [Fact]
    public void FallsBackToTheUserLocalBin()
    {
        var local = Touch(Path.Combine(".local", "bin"), "vibe-acp");

        Assert.Equal(local, VibeAcpLauncher.Locate(Env(("PATH", "/nonexistent")), _root));
    }

    [Fact]
    public void ReturnsNullWhenAbsent()
    {
        Assert.Null(VibeAcpLauncher.Locate(Env(("PATH", _root)), _root));
    }

    [Fact]
    public async Task StartRunsTheExecutableInTheWorkspace()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("Uses a POSIX shell script as the agent.");
        var script = Path.Combine(_root, "agent.sh");
        await File.WriteAllTextAsync(script, "#!/bin/sh\npwd >&2\nread line\nprintf '{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"protocolVersion\":1}}\\n'\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var workspace = Directory.CreateDirectory(Path.Combine(_root, "ws")).FullName;
        var stderr = new TaskCompletionSource<string>();

        await using var client = VibeAcpLauncher.Start(script, workspace, new RecordingHandler(), line => stderr.TrySetResult(line));
        var response = await client.InitializeAsync(new InitializeRequest(1, new ClientCapabilities(new FileSystemCapabilities(true, true)), new Implementation("test", "0"))).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, response.ProtocolVersion);
        Assert.Equal(Path.GetFullPath(workspace).TrimEnd('/'), (await stderr.Task.WaitAsync(TimeSpan.FromSeconds(10))).TrimEnd('/').Replace("/private/", "/"));
    }

    private string Touch(string directory, string name)
    {
        var path = Path.Combine(_root, directory, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    private static Func<string, string?> Env(params (string Key, string Value)[] values)
        => key => values.FirstOrDefault(pair => pair.Key == key).Value;

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
