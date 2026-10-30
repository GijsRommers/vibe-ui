using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using VibeUi.Acp;
using VibeUi.Desktop.Services;
using VibeUi.Desktop.ViewModels;
using VibeUi.Desktop.Views;

[assembly: AvaloniaTestApplication(typeof(VibeUi.Desktop.Tests.TestApplication))]

namespace VibeUi.Desktop.Tests;

public sealed class TestApplication : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApplication>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class MainWindowTests
{
    [AvaloniaFact]
    public async Task WindowConnectsOnOpenAndRendersTheSessionPanels()
    {
        var client = new FakeAcpClient();
        var model = new MainViewModel(
            () => "/bin/vibe-acp",
            (_, _, handler, _) =>
            {
                client.Handler = handler;
                return client;
            },
            _ => { },
            action => action(),
            new DesktopSettings());
        var window = new MainWindow { DataContext = model };
        window.Show();
        try
        {
            await Task.Yield();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(AgentState.Ready, model.State);
            Assert.True(window.FindControl<Grid>("SessionPanel")!.IsVisible);
            Assert.False(window.FindControl<StackPanel>("SignInPanel")!.IsVisible);
            Assert.False(window.FindControl<Border>("PermissionCard")!.IsVisible);
            Assert.Equal(3, window.FindControl<ComboBox>("ModeBox")!.ItemCount);

            await client.SendUpdateAsync("""{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Hello from Vibe"}}""");
            var permission = model.RequestPermissionAsync(new RequestPermissionRequest("s-1", new ToolCallUpdate("c1", "Run tests"),
                [new PermissionOption("allow_once", "Allow once", "allow_once"), new PermissionOption("reject_once", "Deny", "reject_once")]), CancellationToken.None);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(1, window.FindControl<ItemsControl>("TranscriptList")!.ItemCount);
            Assert.True(window.FindControl<Border>("PermissionCard")!.IsVisible);
            model.PendingPermission!.Options[1].ChooseCommand.Execute(null);
            Assert.Equal("reject_once", (await permission).Outcome.OptionId);
            Dispatcher.UIThread.RunJobs();
            Assert.False(window.FindControl<Border>("PermissionCard")!.IsVisible);
        }
        finally
        {
            window.Close();
            await model.DisconnectAsync();
        }
    }

    [AvaloniaFact]
    public void SignInPanelAppearsWhenTheAgentRequiresAuthentication()
    {
        var client = new FakeAcpClient { RequireAuthentication = true };
        var model = new MainViewModel(
            () => "/bin/vibe-acp",
            (_, _, handler, _) =>
            {
                client.Handler = handler;
                return client;
            },
            _ => { },
            action => action(),
            new DesktopSettings());
        var window = new MainWindow { DataContext = model };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(AgentState.SignInRequired, model.State);
            Assert.True(window.FindControl<StackPanel>("SignInPanel")!.IsVisible);
            Assert.True(window.FindControl<Button>("SignInButton")!.IsVisible);
            Assert.False(window.FindControl<Grid>("SessionPanel")!.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }
}
