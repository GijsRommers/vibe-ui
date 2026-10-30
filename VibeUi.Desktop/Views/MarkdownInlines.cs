using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace VibeUi.Desktop.Views;

/// <summary>
/// Attached property that renders light markdown into a text block's inlines: **bold**, *italic*,
/// `code`, headings, bullet and numbered lists, links as plain text. Fenced code blocks are split
/// out earlier by <see cref="ViewModels.MessageEntry"/>, so this only sees prose.
/// </summary>
public static partial class MarkdownInlines
{
    public static readonly AttachedProperty<string?> TextProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, string?>("Text", typeof(MarkdownInlines));

    private static readonly FontFamily Mono = new("SF Mono,Menlo,Consolas,DejaVu Sans Mono,monospace");
    private static readonly IBrush CodeBackground = new SolidColorBrush(Color.Parse("#26262D"));
    private static readonly IBrush CodeForeground = new SolidColorBrush(Color.Parse("#FFB366"));
    private static readonly IBrush MutedForeground = new SolidColorBrush(Color.Parse("#8A8A93"));

    static MarkdownInlines() => TextProperty.Changed.AddClassHandler<TextBlock>((block, args) => Render(block, args.NewValue as string ?? ""));

    public static string? GetText(TextBlock block) => block.GetValue(TextProperty);

    public static void SetText(TextBlock block, string? value) => block.SetValue(TextProperty, value);

    public static void Render(TextBlock block, string markdown)
    {
        var inlines = new InlineCollection();
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (index > 0)
                inlines.Add(new LineBreak());
            var heading = Heading().Match(line);
            if (heading.Success)
            {
                var level = heading.Groups[1].Value.Length;
                foreach (var run in Parse(heading.Groups[2].Value))
                {
                    run.FontWeight = FontWeight.SemiBold;
                    run.FontSize = level == 1 ? 18 : level == 2 ? 16 : 15;
                    inlines.Add(run);
                }
                continue;
            }
            var bullet = Bullet().Match(line);
            if (bullet.Success)
            {
                inlines.Add(new Run(bullet.Groups[1].Value + "•  ") { Foreground = MutedForeground });
                foreach (var run in Parse(bullet.Groups[2].Value))
                    inlines.Add(run);
                continue;
            }
            var numbered = Numbered().Match(line);
            if (numbered.Success)
            {
                inlines.Add(new Run(numbered.Groups[1].Value + numbered.Groups[2].Value + ". ") { Foreground = MutedForeground });
                foreach (var run in Parse(numbered.Groups[3].Value))
                    inlines.Add(run);
                continue;
            }
            if (line.StartsWith("> ", StringComparison.Ordinal))
            {
                inlines.Add(new Run("│ ") { Foreground = MutedForeground });
                foreach (var run in Parse(line[2..]))
                {
                    run.Foreground = MutedForeground;
                    inlines.Add(run);
                }
                continue;
            }
            foreach (var run in Parse(line))
                inlines.Add(run);
        }
        block.Inlines = inlines;
    }

    /// <summary>Splits one line into runs: code spans, bold, italic, links shown as their text.</summary>
    public static IEnumerable<Run> Parse(string text)
    {
        var position = 0;
        foreach (Match match in Span().Matches(text))
        {
            if (match.Index > position)
                yield return new Run(text[position..match.Index]);
            if (match.Groups["code"].Success)
                yield return new Run(match.Groups["code"].Value) { FontFamily = Mono, FontSize = 13, Background = CodeBackground, Foreground = CodeForeground };
            else if (match.Groups["bold"].Success)
                yield return new Run(match.Groups["bold"].Value) { FontWeight = FontWeight.SemiBold };
            else if (match.Groups["italic"].Success)
                yield return new Run(match.Groups["italic"].Value) { FontStyle = FontStyle.Italic };
            else if (match.Groups["link"].Success)
                yield return new Run(match.Groups["link"].Value) { TextDecorations = TextDecorations.Underline };
            position = match.Index + match.Length;
        }
        if (position < text.Length)
            yield return new Run(text[position..]);
    }

    [GeneratedRegex(@"`(?<code>[^`]+)`|\*\*(?<bold>[^*]+)\*\*|__(?<bold>[^_]+)__|(?<![\w*])\*(?<italic>[^*\s][^*]*)\*(?!\w)|\[(?<link>[^\]]+)\]\([^)]+\)")]
    private static partial Regex Span();

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^(\s*)[-*+]\s+(.*)$")]
    private static partial Regex Bullet();

    [GeneratedRegex(@"^(\s*)(\d+)[.)]\s+(.*)$")]
    private static partial Regex Numbered();
}
