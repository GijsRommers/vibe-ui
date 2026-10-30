using Avalonia.Data.Converters;
using Avalonia.Media;

namespace VibeUi.Desktop.ViewModels;

public static class PullRequestChips
{
    private static readonly IBrush Green = new SolidColorBrush(Color.Parse("#4ADE80"));
    private static readonly IBrush Red = new SolidColorBrush(Color.Parse("#F87171"));
    private static readonly IBrush Amber = new SolidColorBrush(Color.Parse("#FBBF24"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#8A8A93"));

    public static FuncValueConverter<string?, bool> IsFailing { get; } = new(checks => checks == "failing");
    public static FuncValueConverter<string?, bool> IsPassing { get; } = new(checks => checks == "passing");

    public static FuncValueConverter<string?, IBrush> Brush { get; } = new(checks => checks switch
    {
        "passing" => Green,
        "failing" => Red,
        "pending" => Amber,
        _ => Muted,
    });
}
