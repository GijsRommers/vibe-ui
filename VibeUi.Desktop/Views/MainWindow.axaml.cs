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
                InstallShortcuts(model);
        };
        Opened += async (_, _) =>
        {
            if (DataContext is ShellViewModel model)
                await model.InitializeAsync();
        };
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
