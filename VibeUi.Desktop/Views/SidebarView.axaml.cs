using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using VibeUi.Desktop.ViewModels;

namespace VibeUi.Desktop.Views;

public partial class SidebarView : UserControl
{
    public SidebarView() => InitializeComponent();

    private async void AddProject(object? sender, RoutedEventArgs args)
    {
        if (TopLevel.GetTopLevel(this) is MainWindow window)
            await window.AddProjectAsync();
    }
}
