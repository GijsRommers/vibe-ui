using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VibeUi.Acp;
using VibeUi.Desktop.Services;
using VibeUi.Desktop.ViewModels;
using VibeUi.Desktop.Views;

[assembly: AvaloniaTestApplication(typeof(VibeUi.Desktop.Tests.TestApplication))]

namespace VibeUi.Desktop.Tests;

public sealed class TestApplication : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://vibe-ui/")) { Source = new Uri("avares://vibe-ui/Views/Theme.axaml") });
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApplication>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public static class TestShell
{
    public static (ShellViewModel Shell, FakeAcpClient Client, string Workspace) Build(Action<FakeAcpClient>? configure = null)
    {
        var client = new FakeAcpClient();
        configure?.Invoke(client);
        var directory = Directory.CreateTempSubdirectory("vibe-ui-window").FullName;
        var workspace = Directory.CreateTempSubdirectory("vibe-ui-window-ws").FullName;
        var state = new AppState(directory);
        state.Projects.Add(ProjectRecord.ForPath(workspace));
        state.Projects[0].Threads.Add(new ThreadRecord { Id = "t1" });
        state.SelectedThreadId = "t1";
        var shell = new ShellViewModel(state, new ThreadStore(directory),
            () => "/bin/vibe-acp",
            (_, cwd, handler, _) =>
            {
                client.Handler = handler;
                client.Workspace = cwd;
                return client;
            },
            _ => { },
            action => action(),
            latestVibeVersion: _ => Task.FromResult<string?>(null));
        return (shell, client, workspace);
    }
}

public sealed class MainWindowTests
{
    [AvaloniaFact]
    public async Task WindowOpensTheSelectedThreadAndShowsPermissions()
    {
        var (shell, client, _) = TestShell.Build();
        var window = new MainWindow { DataContext = shell };
        window.Show();
        try
        {
            await Task.Yield();
            Dispatcher.UIThread.RunJobs();
            var thread = shell.SelectedThread!;
            Assert.Equal(ThreadStatus.Ready, thread.State);
            Assert.True(shell.ShowThread);
            var threadView = Assert.IsType<ThreadView>(window.FindControl<ContentControl>("ThreadHost")!.GetVisualDescendants().OfType<ThreadView>().Single());
            Assert.False(window.FindControl<Border>("Gate")!.IsVisible);
            Assert.False(window.FindControl<StackPanel>("EmptyState")!.IsVisible);
            shell.IsChangesVisible = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.FindControl<ChangesView>("Changes")!.IsVisible);
            Assert.Equal(4, threadView.FindControl<ComboBox>("ModeBox")!.ItemCount);
            Assert.Equal(3, threadView.FindControl<ComboBox>("ThinkingBox")!.ItemCount);
            Assert.False(threadView.FindControl<Border>("PermissionCard")!.IsVisible);

            await client.SendUpdateAsync("""{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Hello from Vibe"}}""");
            var permission = thread.RequestPermissionAsync(new RequestPermissionRequest("s-1", new ToolCallUpdate("c1", "Run tests"),
                [new PermissionOption("allow_once", "Allow once", "allow_once"), new PermissionOption("reject_once", "Deny", "reject_once")]), CancellationToken.None);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(1, threadView.FindControl<ItemsControl>("TranscriptList")!.ItemCount);
            Assert.True(threadView.FindControl<Border>("PermissionCard")!.IsVisible);
            thread.PendingPermission!.Options[1].ChooseCommand.Execute(null);
            Assert.Equal("reject_once", (await permission).Outcome.OptionId);
            Dispatcher.UIThread.RunJobs();
            Assert.False(threadView.FindControl<Border>("PermissionCard")!.IsVisible);

            // The sidebar lists the project and its thread.
            var sidebar = window.FindControl<SidebarView>("Sidebar")!;
            Assert.Equal(1, sidebar.FindControl<ItemsControl>("ProjectList")!.ItemCount);

            shell.IsChangesVisible = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(window.FindControl<ChangesView>("Changes")!.IsVisible);

            shell.ToggleSettingsCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.FindControl<Border>("SettingsPanel")!.IsVisible);
        }
        finally
        {
            window.Close();
            await shell.ShutdownAsync();
        }
    }

    [AvaloniaFact]
    public async Task SignInPanelAppearsWhenTheAgentRequiresAuthentication()
    {
        var (shell, _, _) = TestShell.Build(client => client.RequireAuthentication = true);
        var window = new MainWindow { DataContext = shell };
        window.Show();
        try
        {
            await Task.Yield();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(ConnectionState.SignInRequired, shell.ActiveConnection!.State);
            Assert.True(window.FindControl<Border>("Gate")!.IsVisible);
            Assert.True(window.FindControl<StackPanel>("SignInPanel")!.IsVisible);
            Assert.False(window.FindControl<StackPanel>("InstallPanel")!.IsVisible);
            Assert.False(window.FindControl<Grid>("Columns")!.IsVisible);
        }
        finally
        {
            window.Close();
            await shell.ShutdownAsync();
        }
    }

    [AvaloniaFact]
    public async Task GateShowsTheInstallHintWhenVibeIsMissing()
    {
        var directory = Directory.CreateTempSubdirectory("vibe-ui-empty").FullName;
        var shell = new ShellViewModel(new AppState(directory), new ThreadStore(directory), () => null, (_, _, _, _) => throw new InvalidOperationException(), _ => { }, action => action());
        var window = new MainWindow { DataContext = shell };
        window.Show();
        try
        {
            await Task.Yield();
            Dispatcher.UIThread.RunJobs();
            Assert.Same(shell.Chief, shell.SelectedThread);
            Assert.True(window.FindControl<Border>("Gate")!.IsVisible);
            Assert.True(window.FindControl<StackPanel>("InstallPanel")!.IsVisible);
            Assert.False(window.FindControl<StackPanel>("SignInPanel")!.IsVisible);
            Assert.False(window.FindControl<Grid>("Columns")!.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }
}
