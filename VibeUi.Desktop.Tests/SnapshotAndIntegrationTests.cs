using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using VibeUi.Acp;
using VibeUi.Desktop.Services;
using VibeUi.Desktop.ViewModels;
using VibeUi.Desktop.Views;

namespace VibeUi.Desktop.Tests;

/// <summary>Opt-in checks: rendered PNG snapshots (VIBE_UI_SNAPSHOT_DIR) and a real vibe-acp round trip (VIBE_UI_INTEGRATION=1).</summary>
public sealed class SnapshotAndIntegrationTests
{
    [AvaloniaFact]
    public async Task RendersSessionAndSignInSnapshots()
    {
        var directory = Environment.GetEnvironmentVariable("VIBE_UI_SNAPSHOT_DIR");
        Assert.SkipWhen(string.IsNullOrEmpty(directory), "Set VIBE_UI_SNAPSHOT_DIR to write PNG snapshots.");
        Directory.CreateDirectory(directory!);

        var client = new FakeAcpClient { TrustStatus = "untrusted" };
        var model = NewModel(client);
        var window = new MainWindow { DataContext = model, Width = 980, Height = 720 };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            model.PromptText = "What does note.txt say?";
            await model.SendCommand.ExecuteAsync(null);
            await client.SendUpdateAsync("""{"sessionUpdate":"agent_thought_chunk","content":{"type":"text","text":"The user wants the file contents. Let me read it."}}""");
            await client.SendUpdateAsync("""{"sessionUpdate":"tool_call","toolCallId":"c1","title":"Reading /work/note.txt","kind":"read","status":"completed","locations":[{"path":"/work/note.txt"}],"content":[{"type":"content","content":{"type":"text","text":"hello world"}}]}""");
            await client.SendUpdateAsync("""{"sessionUpdate":"tool_call","toolCallId":"c2","title":"bash: npm test","kind":"execute","status":"in_progress"}""");
            await client.SendUpdateAsync("""{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"note.txt contains the single line \"hello world\"."}}""");
            await client.SendUpdateAsync("""{"sessionUpdate":"usage_update","used":5029,"size":200000,"cost":{"amount":0.0053655,"currency":"USD"}}""");
            model.ShowThoughts = true;
            _ = model.RequestPermissionAsync(new RequestPermissionRequest("s-1", new ToolCallUpdate("c2", "bash: npm test"),
            [
                new PermissionOption("allow_once", "Allow once", "allow_once"),
                new PermissionOption("allow_always", "Allow for remainder of this session", "allow_always"),
                new PermissionOption("reject_once", "Deny", "reject_once"),
            ]), CancellationToken.None);
            Dispatcher.UIThread.RunJobs();
            Save(window, Path.Combine(directory!, "session.png"));

            client.RequireAuthentication = true;
            await model.SignOutCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            Save(window, Path.Combine(directory!, "sign-in.png"));
        }
        finally
        {
            window.Close();
            await model.DisconnectAsync();
        }
    }

    [Fact]
    public async Task RealVibeAnswersAPromptThroughTheViewModel()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("VIBE_UI_INTEGRATION") == "1", "Set VIBE_UI_INTEGRATION=1 to run against vibe-acp.");
        var executable = VibeAcpLauncher.Locate();
        Assert.SkipWhen(executable is null, "vibe-acp is not installed.");
        var workspace = Directory.CreateTempSubdirectory("vibe-ui-e2e").FullName;
        var model = new MainViewModel(
            () => executable,
            (exe, cwd, handler, log) => VibeAcpLauncher.Start(exe, cwd, handler, log),
            _ => { },
            action => action(),
            new DesktopSettings { WorkspacePath = workspace });

        await model.ConnectAsync();
        Assert.SkipWhen(model.State == AgentState.SignInRequired, "Vibe is not signed in on this machine.");
        Assert.Equal(AgentState.Ready, model.State);
        model.PromptText = "Reply with exactly the single word: pong";
        await model.SendCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromMinutes(2));
        await model.DisconnectAsync();

        Assert.Equal("Done.", model.Status);
        var reply = model.Transcript.OfType<MessageEntry>().Last(entry => entry.IsAgent);
        Assert.Contains("pong", reply.Text, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(model.UsageText);
    }

    private static void Save(Window window, string path)
    {
        using var stream = File.Create(path);
        window.CaptureRenderedFrame()!.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    private static MainViewModel NewModel(FakeAcpClient client) => new(
        () => "/bin/vibe-acp",
        (_, _, handler, _) =>
        {
            client.Handler = handler;
            return client;
        },
        _ => { },
        action => action(),
        new DesktopSettings { WorkspacePath = "/work/my-project" });
}
