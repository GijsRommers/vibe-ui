using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using VibeUi.Acp;
using VibeUi.Desktop.Services;

namespace VibeUi.Desktop.ViewModels;

public abstract class TranscriptEntry : ObservableObject
{
    public abstract TranscriptRecord ToRecord();

    public static TranscriptEntry? FromRecord(TranscriptRecord record) => record.Type switch
    {
        "message" => new MessageEntry(record.Role switch { "user" => MessageRole.User, "thought" => MessageRole.Thought, "briefing" => MessageRole.Briefing, _ => MessageRole.Agent }, record.Text ?? ""),
        "tool" => ToolCallEntry.FromToolRecord(record),
        "plan" => new PlanEntryItem { Text = record.Text ?? "" },
        "notice" => new NoticeEntry(record.Text ?? ""),
        _ => null,
    };
}

public enum MessageRole
{
    User,
    Agent,
    Thought,
    /// <summary>A status report Vibe UI injected on behalf of a delegated task.</summary>
    Briefing,
}

/// <summary>A paragraph run or a fenced code block inside a message.</summary>
public sealed partial class MessageBlock(bool isCode, string language, string text) : ObservableObject
{
    public bool IsCode { get; } = isCode;
    public string Language { get; } = language;
    public bool HasLanguage => Language.Length > 0;

    [ObservableProperty] private string _text = text;
}

public sealed partial class MessageEntry : TranscriptEntry
{
    public MessageEntry(MessageRole role, string text, string? messageId = null)
    {
        Role = role;
        MessageId = messageId;
        _text = text;
        UpdateBlocks();
    }

    public MessageRole Role { get; }
    public string? MessageId { get; }
    public bool IsUser => Role == MessageRole.User;
    public bool IsAgent => Role == MessageRole.Agent;
    public bool IsThought => Role == MessageRole.Thought;
    public bool IsBriefing => Role == MessageRole.Briefing;
    public bool IsProse => Role is MessageRole.Agent or MessageRole.Thought;
    public string Author => Role switch { MessageRole.User => "You", MessageRole.Agent => "Vibe", MessageRole.Briefing => "Briefing", _ => "Thinking" };
    public ObservableCollection<MessageBlock> Blocks { get; } = [];

    [ObservableProperty] private string _text;
    [ObservableProperty] private bool _isHidden;

    public void Append(string more) => Text += more;

    partial void OnTextChanged(string value) => UpdateBlocks();

    public override TranscriptRecord ToRecord()
        => new("message", Role switch { MessageRole.User => "user", MessageRole.Thought => "thought", MessageRole.Briefing => "briefing", _ => "agent" }, Text);

    /// <summary>Splits the text on ``` fences. While streaming, an open fence runs to the end.</summary>
    private void UpdateBlocks()
    {
        var parsed = Parse(Text);
        var keep = 0;
        while (keep < Blocks.Count && keep < parsed.Count && Blocks[keep].IsCode == parsed[keep].IsCode && Blocks[keep].Language == parsed[keep].Language)
        {
            if (Blocks[keep].Text != parsed[keep].Text)
                Blocks[keep].Text = parsed[keep].Text;
            keep++;
        }
        while (Blocks.Count > keep)
            Blocks.RemoveAt(Blocks.Count - 1);
        for (var index = keep; index < parsed.Count; index++)
            Blocks.Add(new MessageBlock(parsed[index].IsCode, parsed[index].Language, parsed[index].Text));
    }

    private static List<(bool IsCode, string Language, string Text)> Parse(string text)
    {
        var blocks = new List<(bool, string, string)>();
        var current = new StringBuilder();
        var inCode = false;
        var language = "";
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                Flush();
                inCode = !inCode;
                language = inCode ? line.Trim()[3..].Trim() : "";
                continue;
            }
            if (current.Length > 0)
                current.Append('\n');
            current.Append(line);
        }
        Flush(final: true);
        return blocks;

        void Flush(bool final = false)
        {
            var body = current.ToString();
            if (!inCode)
                body = body.Trim('\n');
            if (body.Length > 0 || (inCode && final))
                blocks.Add((inCode, language, inCode ? body.TrimEnd('\n') : body));
            current.Clear();
        }
    }
}

public sealed partial class ToolCallEntry(string toolCallId) : TranscriptEntry
{
    public string ToolCallId { get; } = toolCallId;

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _kind = "other";
    [ObservableProperty] private string _status = ToolCallStatuses.Pending;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasOutput))] private string _output = "";
    [ObservableProperty] private string _locations = "";
    [ObservableProperty] private bool _isExpanded;

    public bool HasOutput => Output.Length > 0;

    public string Icon => Kind switch
    {
        "read" => "📖",
        "edit" => "✏️",
        "delete" => "🗑",
        "move" => "📦",
        "search" => "🔍",
        "execute" => "⚙️",
        "think" => "💭",
        "fetch" => "🌐",
        "switch_mode" => "🔀",
        _ => "🔧",
    };

    public string StatusText => Status switch
    {
        ToolCallStatuses.Pending => "waiting",
        ToolCallStatuses.InProgress => "running",
        ToolCallStatuses.Completed => "done",
        ToolCallStatuses.Failed => "failed",
        var other => other,
    };

    public bool IsFailed => Status == ToolCallStatuses.Failed;
    public bool IsRunning => Status is ToolCallStatuses.Pending or ToolCallStatuses.InProgress;

    public void Apply(ToolCallUpdate update)
    {
        if (update.Title is { Length: > 0 } title)
            Title = title;
        if (update.Kind is { Length: > 0 } kind)
        {
            Kind = kind;
            OnPropertyChanged(nameof(Icon));
        }
        if (update.Status is { Length: > 0 } status)
        {
            Status = status;
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(IsFailed));
            OnPropertyChanged(nameof(IsRunning));
            if (status == ToolCallStatuses.Failed)
                IsExpanded = true;
        }
        if (update.Locations is { Count: > 0 } locations)
            Locations = string.Join('\n', locations.Select(location => location.Line is { } line ? $"{location.Path}:{line}" : location.Path));
        if (update.Content is { Count: > 0 } content)
            Output = Render(content);
    }

    public override TranscriptRecord ToRecord() => new("tool", Title: Title, Kind: Kind, Status: Status, Output: Output, Locations: Locations);

    public static ToolCallEntry FromToolRecord(TranscriptRecord record)
    {
        var entry = new ToolCallEntry(Guid.NewGuid().ToString("N"))
        {
            Title = record.Title ?? "",
            Kind = record.Kind ?? "other",
            Status = record.Status is ToolCallStatuses.Pending or ToolCallStatuses.InProgress ? ToolCallStatuses.Completed : record.Status ?? ToolCallStatuses.Completed,
            Output = record.Output ?? "",
            Locations = record.Locations ?? "",
        };
        return entry;
    }

    private static string Render(IReadOnlyList<ToolCallContent> content)
    {
        var text = new StringBuilder();
        foreach (var item in content)
        {
            switch (item.Type)
            {
                case "content" when item.Content?.Text is { } blockText:
                    text.Append(blockText);
                    break;
                case "diff":
                    text.Append(item.Path).Append('\n');
                    if (item.OldText is { Length: > 0 } old)
                        foreach (var line in old.Split('\n'))
                            text.Append("- ").Append(line).Append('\n');
                    foreach (var line in (item.NewText ?? "").Split('\n'))
                        text.Append("+ ").Append(line).Append('\n');
                    break;
                case "terminal":
                    text.Append("terminal ").Append(item.TerminalId);
                    break;
            }
            text.Append('\n');
        }
        return text.ToString().TrimEnd();
    }
}

public sealed partial class NoticeEntry(string text) : TranscriptEntry
{
    [ObservableProperty] private string _text = text;

    public override TranscriptRecord ToRecord() => new("notice", Text: Text);
}

public sealed partial class PlanEntryItem : TranscriptEntry
{
    [ObservableProperty] private string _text = "";

    public void Apply(IReadOnlyList<PlanEntry> entries)
        => Text = string.Join('\n', entries.Select(entry => entry.Status switch
        {
            "completed" => $"☑ {entry.Content}",
            "in_progress" => $"▶ {entry.Content}",
            _ => $"☐ {entry.Content}",
        }));

    public override TranscriptRecord ToRecord() => new("plan", Text: Text);
}
