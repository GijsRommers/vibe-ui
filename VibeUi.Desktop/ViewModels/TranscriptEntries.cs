using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using VibeUi.Acp;

namespace VibeUi.Desktop.ViewModels;

public abstract class TranscriptEntry : ObservableObject;

public enum MessageRole
{
    User,
    Agent,
    Thought,
}

public sealed partial class MessageEntry(MessageRole role, string text, string? messageId = null) : TranscriptEntry
{
    public MessageRole Role { get; } = role;
    public string? MessageId { get; } = messageId;
    public bool IsUser => Role == MessageRole.User;
    public bool IsAgent => Role == MessageRole.Agent;
    public bool IsThought => Role == MessageRole.Thought;
    public string Author => Role switch { MessageRole.User => "You", MessageRole.Agent => "Vibe", _ => "Thinking" };

    [ObservableProperty] private string _text = text;
    [ObservableProperty] private bool _isHidden;

    public void Append(string more) => Text += more;
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
            if (status == ToolCallStatuses.Failed)
                IsExpanded = true;
        }
        if (update.Locations is { Count: > 0 } locations)
            Locations = string.Join('\n', locations.Select(location => location.Line is { } line ? $"{location.Path}:{line}" : location.Path));
        if (update.Content is { Count: > 0 } content)
            Output = Render(content);
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
}
