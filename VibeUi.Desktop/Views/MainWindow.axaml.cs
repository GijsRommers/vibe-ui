using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using VibeUi.Desktop.ViewModels;

namespace VibeUi.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel model)
                model.TranscriptChanged += (_, _) => Dispatcher.UIThread.Post(TranscriptScroller.ScrollToEnd, DispatcherPriority.Background);
        };
        Opened += async (_, _) =>
        {
            if (DataContext is MainViewModel model && model.State == AgentState.Disconnected)
                await model.ConnectAsync();
        };
    }

    private async void ChooseWorkspace(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not MainViewModel model)
            return;
        var start = await StorageProvider.TryGetFolderFromPathAsync(model.WorkspacePath);
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose the folder Vibe should work in",
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });
        if (folders.Count == 1 && folders[0].TryGetLocalPath() is { } path)
            await model.ChangeWorkspaceAsync(path);
    }

    private void PromptKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter || args.KeyModifiers.HasFlag(KeyModifiers.Shift))
            return;
        if (DataContext is MainViewModel model && model.SendCommand.CanExecute(null))
        {
            model.SendCommand.Execute(null);
            args.Handled = true;
        }
    }
}
