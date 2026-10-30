using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using VibeUi.Desktop.ViewModels;

namespace VibeUi.Desktop.Views;

public partial class ThreadView : UserControl
{
    private ThreadViewModel? _thread;

    public ThreadView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_thread is not null)
                _thread.TranscriptChanged -= OnTranscriptChanged;
            _thread = DataContext as ThreadViewModel;
            if (_thread is not null)
            {
                _thread.TranscriptChanged += OnTranscriptChanged;
                Dispatcher.UIThread.Post(() =>
                {
                    TranscriptScroller.ScrollToEnd();
                    PromptBox.Focus();
                }, DispatcherPriority.Background);
            }
        };
    }

    private void OnTranscriptChanged(object? sender, EventArgs args)
        => Dispatcher.UIThread.Post(TranscriptScroller.ScrollToEnd, DispatcherPriority.Background);

    public void FocusPrompt() => PromptBox.Focus();

    private void PromptKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter || args.KeyModifiers.HasFlag(KeyModifiers.Shift))
            return;
        if (_thread is { } thread && thread.SendCommand.CanExecute(null))
        {
            thread.SendCommand.Execute(null);
            args.Handled = true;
        }
    }

    private void TitleKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.Enter)
        {
            CommitTitle();
            PromptBox.Focus();
            args.Handled = true;
        }
        else if (args.Key == Key.Escape && _thread is not null)
        {
            TitleBox.Text = _thread.Title;
            PromptBox.Focus();
            args.Handled = true;
        }
    }

    private void TitleLostFocus(object? sender, RoutedEventArgs args) => CommitTitle();

    private void CommitTitle()
    {
        if (_thread is { } thread && TitleBox.Text is { } text && text.Trim() != thread.Title)
            _ = thread.RenameAsync(text);
    }
}
