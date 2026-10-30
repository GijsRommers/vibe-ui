using Avalonia.Data.Converters;

namespace VibeUi.Desktop.ViewModels;

public static class SidebarGlyphs
{
    public static FuncValueConverter<bool, string> Chevron { get; } = new(expanded => expanded ? "▾" : "▸");
}
