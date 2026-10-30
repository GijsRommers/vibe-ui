using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VibeUi.Acp;

namespace VibeUi.Desktop.ViewModels;

/// <summary>One agent permission prompt; answered once, by a user choice or by cancellation.</summary>
public sealed partial class PermissionRequestViewModel : ObservableObject
{
    private readonly TaskCompletionSource<RequestPermissionResponse> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PermissionRequestViewModel(RequestPermissionRequest request, ToolCallEntry? toolCall)
    {
        Request = request;
        ToolCall = toolCall;
        Options = request.Options.Select(option => new PermissionChoice(option, this)).ToList();
    }

    public RequestPermissionRequest Request { get; }
    public ToolCallEntry? ToolCall { get; }
    public string Title => Request.ToolCall.Title ?? ToolCall?.Title ?? "Vibe wants to run a tool";
    public string Detail => ToolCall is { Locations.Length: > 0 } ? ToolCall.Locations : ToolCall?.Output ?? "";
    public IReadOnlyList<PermissionChoice> Options { get; }
    public Task<RequestPermissionResponse> Answer => _answer.Task;
    public bool IsAnswered => _answer.Task.IsCompleted;

    public void Choose(PermissionOption option)
    {
        if (_answer.TrySetResult(new RequestPermissionResponse(PermissionOutcome.Selected(option.OptionId))))
            OnPropertyChanged(nameof(IsAnswered));
    }

    public void Cancel()
    {
        if (_answer.TrySetResult(new RequestPermissionResponse(PermissionOutcome.Cancelled)))
            OnPropertyChanged(nameof(IsAnswered));
    }
}

public sealed partial class PermissionChoice(PermissionOption option, PermissionRequestViewModel owner) : ObservableObject
{
    public PermissionOption Option { get; } = option;
    public string Name => Option.Name;
    public bool IsAllow => Option.Kind.StartsWith("allow", StringComparison.Ordinal);

    [RelayCommand]
    private void Choose() => owner.Choose(Option);
}
