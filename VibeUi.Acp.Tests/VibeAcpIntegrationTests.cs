namespace VibeUi.Acp.Tests;

/// <summary>Talks to a real, installed <c>vibe-acp</c>. Runs only when VIBE_UI_INTEGRATION=1.</summary>
public sealed class VibeAcpIntegrationTests
{
    [Fact]
    public async Task InitializeAndOpenASessionAgainstInstalledVibe()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("VIBE_UI_INTEGRATION") == "1", "Set VIBE_UI_INTEGRATION=1 to run against vibe-acp.");
        var executable = VibeAcpLauncher.Locate();
        Assert.SkipWhen(executable is null, "vibe-acp is not installed.");
        var workspace = Directory.CreateTempSubdirectory("vibe-ui-integration").FullName;
        var errors = new List<string>();
        var handler = new RecordingHandler();

        await using var client = VibeAcpLauncher.Start(executable!, workspace, handler, errors.Add);
        var initialized = await client.InitializeAsync(new InitializeRequest(AcpClient.ProtocolVersion,
            new ClientCapabilities(new FileSystemCapabilities(true, true), Meta: new Dictionary<string, object?> { ["browser-auth-delegated"] = true }),
            new Implementation("vibe-ui-tests", "0.1.0"))).WaitAsync(TimeSpan.FromSeconds(60));
        var session = await client.NewSessionAsync(new NewSessionRequest(workspace, [])).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(AcpClient.ProtocolVersion, initialized.ProtocolVersion);
        Assert.Equal("Mistral Vibe", initialized.AgentInfo?.Title);
        Assert.Contains(initialized.AuthMethods!, method => method.Id == "browser-auth-delegated");
        Assert.NotEmpty(session.SessionId);
        Assert.Contains(session.Modes!.AvailableModes, mode => mode.Id == "ask");
        Assert.Contains(session.ConfigOptions!, option => option.Category == "model");
        await client.CloseSessionAsync(session.SessionId).WaitAsync(TimeSpan.FromSeconds(30));
    }
}
