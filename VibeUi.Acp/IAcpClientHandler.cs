namespace VibeUi.Acp;

/// <summary>What the agent asks of the client: streamed session updates, permission prompts and file access.</summary>
public interface IAcpClientHandler
{
    Task OnSessionUpdateAsync(string sessionId, SessionUpdate update);

    Task<RequestPermissionResponse> RequestPermissionAsync(RequestPermissionRequest request, CancellationToken cancellationToken);

    Task<ReadTextFileResponse> ReadTextFileAsync(ReadTextFileRequest request, CancellationToken cancellationToken);

    Task WriteTextFileAsync(WriteTextFileRequest request, CancellationToken cancellationToken);
}
