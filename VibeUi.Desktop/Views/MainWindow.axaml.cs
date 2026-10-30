using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using VibeUi.Desktop.ViewModels;

namespace VibeUi.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ShellViewModel model)
            {
                InstallShortcuts(model);
                model.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName is nameof(ShellViewModel.ShowBrowserPane) or nameof(ShellViewModel.ShowChangesPane) or nameof(ShellViewModel.IsBrowserWide) or null)
                        LayoutColumns(model);
                };
                LayoutColumns(model);
            }
        };
        Opened += async (_, _) =>
        {
            if (DataContext is ShellViewModel model)
                await model.InitializeAsync();
        };
    }

    /// <summary>
    /// Sidebar | chat | browser | changes. Hidden panes collapse to zero so star columns do not reserve
    /// space for them; the browser takes a 2:3 share next to the chat, or 3:1 when widened.
    /// </summary>
    public void LayoutColumns(ShellViewModel model)
    {
        var columns = Columns.ColumnDefinitions;
        var browser = model.ShowBrowserPane;
        var changes = model.ShowChangesPane;
        columns[2].Width = new GridLength(browser && model.IsBrowserWide ? 1 : 1, GridUnitType.Star);
        columns[2].MinWidth = browser ? (model.IsBrowserWide ? 320 : 420) : 480;
        columns[3].Width = new GridLength(browser ? 1 : 0);
        columns[4].Width = browser ? new GridLength(model.IsBrowserWide ? 4 : 1, GridUnitType.Star) : new GridLength(0);
        columns[4].MinWidth = browser ? 380 : 0;
        columns[5].Width = new GridLength(changes ? 1 : 0);
        columns[6].Width = new GridLength(changes ? 380 : 0);
        columns[6].MinWidth = changes ? 300 : 0;
    }

    /// <summary>⌘/Ctrl shortcuts: N new thread, ⇧N new orchestrator, ⇧O add project, D changes, ⇧T thoughts, Esc stop.</summary>
    private void InstallShortcuts(ShellViewModel model)
    {
        KeyBindings.Clear();
        var command = TryGetCommandModifier();
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.N, command), Command = model.NewThreadCommand });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.N, command | KeyModifiers.Shift), Command = model.NewOrchestratorCommand });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.N, command | KeyModifiers.Alt), Command = model.NewWorktreeThreadCommand });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.O, command | KeyModifiers.Shift), Command = new RelayAction(() => _ = AddProjectAsync()) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.D, command), Command = model.ToggleChangesCommand });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.OemComma, command), Command = model.ToggleSettingsCommand });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.B, command), Command = model.ToggleBrowserCommand });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.T, command | KeyModifiers.Shift), Command = new RelayAction(() => model.ShowThoughts = !model.ShowThoughts) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Escape), Command = new RelayAction(() =>
        {
            if (model.SelectedThread is { IsWorking: true } thread)
                _ = thread.StopAsync();
        }) });
    }

    private static KeyModifiers TryGetCommandModifier()
    {
        try
        {
            return Avalonia.Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? (OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control);
        }
        catch (Exception exception) when (exception is InvalidOperationException or NullReferenceException)
        {
            return OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        }
    }

    private async void AddProjectClick(object? sender, RoutedEventArgs args) => await AddProjectAsync();

    public async Task AddProjectAsync()
    {
        if (DataContext is not ShellViewModel model)
            return;
        var start = model.SelectedProject is { } project ? await StorageProvider.TryGetFolderFromPathAsync(project.Path) : null;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a project folder for Vibe",
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });
        if (folders.Count == 1 && folders[0].TryGetLocalPath() is { } path)
            await model.AddProjectAsync(path);
    }

    private sealed class RelayAction(Action action) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => action();
    }
}
