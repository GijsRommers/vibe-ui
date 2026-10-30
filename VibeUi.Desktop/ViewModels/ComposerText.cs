using Avalonia.Data.Converters;

namespace VibeUi.Desktop.ViewModels;

public static class ComposerText
{
    public static FuncValueConverter<ThreadKind, string> Placeholder { get; } = new(kind => kind switch
    {
        ThreadKind.Orchestrator => "Describe the job to split up…",
        ThreadKind.Chief => "What needs doing?",
        ThreadKind.Task => "Follow up…",
        _ => "Message Vibe…",
    });

    public static FuncValueConverter<ThreadKind, string> EmptyTitle { get; } = new(kind => kind switch
    {
        ThreadKind.Orchestrator => "Orchestrator",
        ThreadKind.Chief => "Chief of Staff",
        ThreadKind.Task => "Delegated task",
        _ => "New thread",
    });

    public static FuncValueConverter<ThreadKind, string> EmptyBody { get; } = new(kind => kind switch
    {
        ThreadKind.Orchestrator => "Splits a job into child threads and collects the results.",
        ThreadKind.Chief => "Starts tasks in any project. They brief it back as they run.",
        ThreadKind.Task => "Started by an orchestrator.",
        _ => "Vibe runs in this folder with its own tools and config.",
    });

    public static FuncValueConverter<string, string> Thinking { get; } = new(name => name is { Length: > 0 } ? $"Thinking: {name}" : "Thinking");
}
