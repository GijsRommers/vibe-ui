namespace VibeUi.Acp;

/// <summary>The client side of one ACP agent connection.</summary>
public interface IAcpClient : IAsyncDisposable
{
    /// <summary>Completes when the agent closes the connection; the result is the failure, if any.</summary>
    Task<Exception?> Closed { get; }

    Task<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken = default);

    Task<AuthenticateResponse> AuthenticateAsync(string methodId, IReadOnlyDictionary<string, object?>? arguments = null, CancellationToken cancellationToken = default);

    Task<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Resumes an earlier session in place. History arrives as session updates before this completes.</summary>
    Task<LoadSessionResponse> LoadSessionAsync(LoadSessionRequest request, CancellationToken cancellationToken = default);

    Task<ListSessionsResponse> ListSessionsAsync(ListSessionsRequest request, CancellationToken cancellationToken = default);

    /// <summary>Runs one prompt turn. Cancelling the token stops waiting; use <see cref="CancelAsync"/> to stop the agent.</summary>
    Task<PromptResponse> PromptAsync(PromptRequest request, CancellationToken cancellationToken = default);

    Task CancelAsync(string sessionId);

    Task SetModeAsync(string sessionId, string modeId, CancellationToken cancellationToken = default);

    Task<SetSessionConfigOptionResponse> SetConfigOptionAsync(string sessionId, string configId, string value, CancellationToken cancellationToken = default);

    Task CloseSessionAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Calls an agent-specific extension method (ACP reserves the <c>_</c> prefix for these).</summary>
    Task<System.Text.Json.JsonElement> ExtensionRequestAsync(string method, object? parameters, CancellationToken cancellationToken = default);
}
