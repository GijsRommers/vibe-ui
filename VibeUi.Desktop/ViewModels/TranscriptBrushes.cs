using Avalonia.Data.Converters;
using Avalonia.Media;

namespace VibeUi.Desktop.ViewModels;

public static class TranscriptBrushes
{
    private static readonly IBrush UserBackground = new SolidColorBrush(Color.Parse("#1E2430"));
    private static readonly IBrush AgentBackground = new SolidColorBrush(Color.Parse("#181818"));
    private static readonly IBrush NormalText = new SolidColorBrush(Color.Parse("#F5F5F5"));
    private static readonly IBrush ThoughtText = new SolidColorBrush(Color.Parse("#9A9A9A"));
    private static readonly IBrush MutedText = new SolidColorBrush(Color.Parse("#8A8A8A"));
    private static readonly IBrush FailedText = new SolidColorBrush(Color.Parse("#FF6B5C"));

    /// <summary>Message background: true (user) or false (agent/thought).</summary>
    public static FuncValueConverter<bool, IBrush> Message { get; } = new(isUser => isUser ? UserBackground : AgentBackground);

    /// <summary>Message text colour: true (thought) or false (normal).</summary>
    public static FuncValueConverter<bool, IBrush> MessageText { get; } = new(isThought => isThought ? ThoughtText : NormalText);

    /// <summary>Tool status colour: true (failed) or false.</summary>
    public static FuncValueConverter<bool, IBrush> Status { get; } = new(isFailed => isFailed ? FailedText : MutedText);
}
