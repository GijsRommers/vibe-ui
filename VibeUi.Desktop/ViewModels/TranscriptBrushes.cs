using Avalonia.Data.Converters;
using Avalonia.Media;
using VibeUi.Desktop.Services;

namespace VibeUi.Desktop.ViewModels;

public static class TranscriptBrushes
{
    private static readonly IBrush NormalText = new SolidColorBrush(Color.Parse("#EDEDEF"));
    private static readonly IBrush ThoughtText = new SolidColorBrush(Color.Parse("#8A8A93"));
    private static readonly IBrush MutedText = new SolidColorBrush(Color.Parse("#8A8A93"));
    private static readonly IBrush FailedText = new SolidColorBrush(Color.Parse("#F87171"));
    private static readonly IBrush Transparent = Brushes.Transparent;
    private static readonly IBrush AddedLine = new SolidColorBrush(Color.Parse("#122B1C"));
    private static readonly IBrush RemovedLine = new SolidColorBrush(Color.Parse("#331518"));
    private static readonly IBrush HunkLine = new SolidColorBrush(Color.Parse("#161B2A"));
    private static readonly IBrush AddedText = new SolidColorBrush(Color.Parse("#86EFAC"));
    private static readonly IBrush RemovedText = new SolidColorBrush(Color.Parse("#FCA5A5"));
    private static readonly IBrush HunkText = new SolidColorBrush(Color.Parse("#7DB2FF"));
    private static readonly IBrush MetaText = new SolidColorBrush(Color.Parse("#5C5C66"));
    private static readonly IBrush Green = new SolidColorBrush(Color.Parse("#4ADE80"));
    private static readonly IBrush Amber = new SolidColorBrush(Color.Parse("#FBBF24"));
    private static readonly IBrush Blue = new SolidColorBrush(Color.Parse("#7DB2FF"));

    /// <summary>Message text colour: true (thought) or false (normal).</summary>
    public static FuncValueConverter<bool, IBrush> MessageText { get; } = new(isThought => isThought ? ThoughtText : NormalText);

    /// <summary>Tool status colour: true (failed) or false.</summary>
    public static FuncValueConverter<bool, IBrush> Status { get; } = new(isFailed => isFailed ? FailedText : MutedText);

    public static FuncValueConverter<DiffLineKind, IBrush> DiffBackground { get; } = new(kind => kind switch
    {
        DiffLineKind.Added => AddedLine,
        DiffLineKind.Removed => RemovedLine,
        DiffLineKind.Hunk => HunkLine,
        _ => Transparent,
    });

    public static FuncValueConverter<DiffLineKind, IBrush> DiffForeground { get; } = new(kind => kind switch
    {
        DiffLineKind.Added => AddedText,
        DiffLineKind.Removed => RemovedText,
        DiffLineKind.Hunk => HunkText,
        DiffLineKind.Meta => MetaText,
        _ => NormalText,
    });

    public static FuncValueConverter<DiffLineKind, string> DiffMarker { get; } = new(kind => kind switch
    {
        DiffLineKind.Added => "+",
        DiffLineKind.Removed => "−",
        _ => " ",
    });

    /// <summary>Colour for a git short status code.</summary>
    public static FuncValueConverter<string, IBrush> FileStatus { get; } = new(status => status switch
    {
        "A" or "?" => Green,
        "D" => RemovedText,
        "R" => Blue,
        _ => Amber,
    });
}
