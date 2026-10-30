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

        var (shell, client, workspace) = TestShell.Build(client => client.TrustStatus = "untrusted");
        File.WriteAllText(Path.Combine(workspace, "vibe.re"), """{"actions":[{"name":"build","command":"echo built"},{"name":"test","command":"echo 12 passed"}]}""");
        shell.Projects[0].Actions.Reload();
        var window = new MainWindow { DataContext = shell, Width = 1280, Height = 820 };
        window.Show();
        try
        {
            await Task.Yield();
            Dispatcher.UIThread.RunJobs();
            var thread = shell.SelectedThread!;
            await thread.SendAsync("What does note.txt say?");
            await client.SendUpdateAsync("""{"sessionUpdate":"agent_thought_chunk","content":{"type":"text","text":"The user wants the file contents. Let me read it."}}""");
            await client.SendUpdateAsync("""{"sessionUpdate":"tool_call","toolCallId":"c1","title":"Reading /work/note.txt","kind":"read","status":"completed","locations":[{"path":"/work/note.txt"}],"content":[{"type":"content","content":{"type":"text","text":"hello world"}}]}""");
            await client.SendUpdateAsync("""{"sessionUpdate":"tool_call","toolCallId":"c2","title":"bash: npm test","kind":"execute","status":"in_progress"}""");
            await client.SendUpdateAsync("""{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"note.txt contains one line:\n\n```text\nhello world\n```\n\nNothing else is in there."}}""");
            await client.SendUpdateAsync("""{"sessionUpdate":"usage_update","used":5029,"size":200000,"cost":{"amount":0.0053655,"currency":"USD"}}""");
            shell.ShowThoughts = true;
            if (!OperatingSystem.IsWindows())
                await shell.Projects[0].Actions.RunAsync(shell.Projects[0].Actions.Actions[1], workspace);
            await shell.NewOrchestratorAsync(shell.SelectedProject);
            await shell.SelectThreadAsync(thread);
            _ = thread.RequestPermissionAsync(new RequestPermissionRequest("s-1", new ToolCallUpdate("c2", "bash: npm test"),
            [
                new PermissionOption("allow_once", "Allow once", "allow_once"),
                new PermissionOption("allow_always", "Allow for remainder of this session", "allow_always"),
                new PermissionOption("reject_once", "Deny", "reject_once"),
            ]), CancellationToken.None);
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(300);
            Dispatcher.UIThread.RunJobs();
            Save(window, Path.Combine(directory!, "session.png"));

            await shell.SelectThreadAsync(shell.Chief);
            await shell.Chief.SendAsync("Get the parser fixed in vibe-ui-window and add tests.");
            await client.SendUpdateAsync("""{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"On it. I have started one task in that project and will report back when it briefs me."}}""", shell.Chief.SessionId);
            await shell.Chief.SendAsync("[Briefing]\nTask progress: \"Fix parser\" in vibe-ui-window (task_id abc, status running). Tool calls: 3, latest: edit parser.cs. Files: src/parser.cs.\nLatest reply: Parser fixed; writing tests now.", MessageRole.Briefing);
            Dispatcher.UIThread.RunJobs();
            Save(window, Path.Combine(directory!, "chief.png"));

            client.RequireAuthentication = true;
            await shell.SignOutCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            Save(window, Path.Combine(directory!, "sign-in.png"));
        }
        finally
        {
            window.Close();
            await shell.ShutdownAsync();
        }
    }

    [Fact]
    public async Task RealVibeAnswersAPromptThroughTheShell()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("VIBE_UI_INTEGRATION") == "1", "Set VIBE_UI_INTEGRATION=1 to run against vibe-acp.");
        var executable = VibeAcpLauncher.Locate();
        Assert.SkipWhen(executable is null, "vibe-acp is not installed.");
        var directory = Directory.CreateTempSubdirectory("vibe-ui-e2e-state").FullName;
        var workspace = Directory.CreateTempSubdirectory("vibe-ui-e2e").FullName;
        var shell = new ShellViewModel(new AppState(directory), new ThreadStore(directory),
            () => executable,
            (exe, cwd, handler, log) => VibeAcpLauncher.Start(exe, cwd, handler, log),
            _ => { },
            action => action());

        await shell.AddProjectAsync(workspace);
        Assert.SkipWhen(shell.ShowSignIn, "Vibe is not signed in on this machine.");
        var thread = shell.SelectedThread!;
        Assert.Equal(ThreadStatus.Ready, thread.State);
        var result = await thread.SendAsync("Reply with exactly the single word: pong").WaitAsync(TimeSpan.FromMinutes(2));
        await shell.ShutdownAsync();

        Assert.Equal(StopReasons.EndTurn, result.StopReason);
        Assert.Contains("pong", result.Reply, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(thread.UsageText);
    }

    private static void Save(Window window, string path)
    {
        using var stream = File.Create(path);
        window.CaptureRenderedFrame()!.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
