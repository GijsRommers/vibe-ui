using System.Text.Json;
using VibeUi.Acp.JsonRpc;

namespace VibeUi.Acp.Tests;

public sealed class JsonRpcConnectionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task RequestsAreAnsweredAndCorrelated()
    {
        await using var agent = new FakeAgent();
        agent.On("echo", parameters => new { echoed = parameters!.Value.GetProperty("value").GetString() });
        agent.Start();
        await using var client = new JsonRpcConnection(agent.OutputForClient, agent.InputForClient, Unsupported, Ignore);

        var tasks = Enumerable.Range(0, 20).Select(i => client.RequestAsync("echo", new { value = $"v{i}" })).ToArray();
        var results = await Task.WhenAll(tasks).WaitAsync(Timeout);

        for (var i = 0; i < results.Length; i++)
            Assert.Equal($"v{i}", results[i].GetProperty("echoed").GetString());
    }

    [Fact]
    public async Task ErrorResponsesBecomeExceptionsWithTheAgentCode()
    {
        await using var agent = new FakeAgent();
        agent.On("session/new", _ => throw new JsonRpcException(JsonRpcException.AuthenticationRequired, "Authentication required"));
        agent.Start();
        await using var client = new JsonRpcConnection(agent.OutputForClient, agent.InputForClient, Unsupported, Ignore);

        var exception = await Assert.ThrowsAsync<JsonRpcException>(() => client.RequestAsync("session/new", new { }).WaitAsync(Timeout));

        Assert.Equal(JsonRpcException.AuthenticationRequired, exception.Code);
        Assert.Equal("Authentication required", exception.Message);
    }

    [Fact]
    public async Task NotificationsArriveInOrder()
    {
        await using var agent = new FakeAgent();
        agent.Start();
        var received = new List<int>();
        var all = new TaskCompletionSource();
        await using var client = new JsonRpcConnection(agent.OutputForClient, agent.InputForClient, Unsupported, (_, parameters) =>
        {
            received.Add(parameters!.Value.GetProperty("n").GetInt32());
            if (received.Count == 200)
                all.TrySetResult();
            return Task.CompletedTask;
        });

        for (var i = 0; i < 200; i++)
            await agent.NotifyClientAsync("tick", new { n = i });

        await all.Task.WaitAsync(Timeout);
        Assert.Equal(Enumerable.Range(0, 200), received);
    }

    [Fact]
    public async Task IncomingRequestsAreAnsweredWithoutBlockingNotifications()
    {
        await using var agent = new FakeAgent();
        agent.Start();
        var release = new TaskCompletionSource();
        var sawNotification = new TaskCompletionSource();
        await using var client = new JsonRpcConnection(agent.OutputForClient, agent.InputForClient,
            async (method, _, _) =>
            {
                Assert.Equal("session/request_permission", method);
                await release.Task;
                return new { outcome = new { outcome = "selected", optionId = "allow_once" } };
            },
            (_, _) =>
            {
                sawNotification.TrySetResult();
                return Task.CompletedTask;
            });

        var pending = agent.RequestClientAsync("session/request_permission", new { });
        await agent.NotifyClientAsync("session/update", new { });
        await sawNotification.Task.WaitAsync(Timeout);
        Assert.False(pending.IsCompleted);

        release.SetResult();
        var answer = await pending.WaitAsync(Timeout);
        Assert.Equal("allow_once", answer.GetProperty("outcome").GetProperty("optionId").GetString());
    }

    [Fact]
    public async Task UnknownIncomingMethodsGetMethodNotFound()
    {
        await using var agent = new FakeAgent();
        agent.Start();
        await using var client = new JsonRpcConnection(agent.OutputForClient, agent.InputForClient, Unsupported, Ignore);

        var exception = await Assert.ThrowsAsync<JsonRpcException>(() => agent.RequestClientAsync("terminal/create", new { }).WaitAsync(Timeout));

        Assert.Equal(JsonRpcException.MethodNotFound, exception.Code);
    }

    [Fact]
    public async Task PendingRequestsFailWhenThePeerCloses()
    {
        var agent = new FakeAgent();
        agent.On("session/prompt", async _ =>
        {
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan);
            return null;
        });
        agent.Start();
        await using var client = new JsonRpcConnection(agent.OutputForClient, agent.InputForClient, Unsupported, Ignore);
        var pending = client.RequestAsync("session/prompt", new { });

        await agent.DisposeAsync();

        var exception = await Assert.ThrowsAsync<JsonRpcException>(() => pending.WaitAsync(Timeout));
        Assert.Equal(JsonRpcException.ConnectionClosed, exception.Code);
        Assert.Null(await client.Closed.WaitAsync(Timeout));
    }

    [Fact]
    public async Task FramesAreNewlineDelimitedJsonWithoutNulls()
    {
        var agentSide = new System.IO.Pipelines.Pipe();
        await using var client = new JsonRpcConnection(Stream.Null, agentSide.Writer.AsStream(), Unsupported, Ignore);

        await client.NotifyAsync("session/cancel", new CancelNotification("s1"));

        using var reader = new StreamReader(agentSide.Reader.AsStream());
        var line = await reader.ReadLineAsync().WaitAsync(Timeout);
        Assert.Equal("""{"jsonrpc":"2.0","method":"session/cancel","params":{"sessionId":"s1"}}""", line);
    }

    private static Task<object?> Unsupported(string method, JsonElement? parameters, CancellationToken cancellationToken)
        => throw new JsonRpcException(JsonRpcException.MethodNotFound, method);

    private static Task Ignore(string method, JsonElement? parameters) => Task.CompletedTask;
}
