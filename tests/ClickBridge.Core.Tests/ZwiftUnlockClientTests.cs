using ClickBridge.Core.Zwift;
using System.Net;
using System.Text;

namespace ClickBridge.Core.Tests;

public sealed class ZwiftUnlockClientTests
{
    [Fact]
    public async Task LoginAndSolve_PostsBinaryPayloadAndBuildsBleReply()
    {
        var cancellationToken = CancellationToken.None;
        byte[] protobuf = [0x0A, 0x02, 0x02, 0xFF];
        var calls = new Queue<Func<HttpRequestMessage, Task<HttpResponseMessage>>>([
            async request =>
            {
                Assert.Equal("secure.zwift.com", request.RequestUri!.Host);
                var form = await request.Content!.ReadAsStringAsync(cancellationToken);
                Assert.Contains("grant_type=password", form, StringComparison.Ordinal);
                Assert.Contains("client_id=Zwift+Game+Client", form, StringComparison.Ordinal);
                Assert.Contains("password=pass%26word", form, StringComparison.Ordinal);
                return Tokens("access", "refresh");
            },
            async request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("/api/d-lock-service/device/authenticate", request.RequestUri!.AbsolutePath);
                Assert.Equal("Bearer access", request.Headers.Authorization?.ToString());
                Assert.Equal("application/x-protobuf-lite", request.Content!.Headers.ContentType?.MediaType);
                Assert.Equal("application/x-protobuf-lite", Assert.Single(request.Headers.Accept).MediaType);
                Assert.Equal(protobuf, await request.Content.ReadAsByteArrayAsync(cancellationToken));
                return Reply(0xFE, 0x01);
            },
        ]);
        using var http = new HttpClient(new StubHandler(request => calls.Dequeue()(request)));
        var saved = new List<string>();
        var client = CreateClient(http, null, saved);

        var refreshToken = await client.LoginAsync("test@example.com", "pass&word", cancellationToken);
        var challenge = Assert.IsType<UnlockChallenge>(ZwiftMessageParser.Parse([0xFF, 0x03, 0x00, .. protobuf]));
        var signedReply = await client.SolveAsync(challenge.Payload, cancellationToken);

        Assert.NotNull(signedReply);
        Assert.Equal([0xFF, 0x04, 0x00, 0xFE, 0x01], ZwiftConstants.CreateUnlockReply(signedReply));
        Assert.Equal("refresh", refreshToken);
        Assert.Empty(saved);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task Solve_NoContentReturnsNoReplyAndTheNextChallengeCanSucceed()
    {
        var responses = new Queue<HttpResponseMessage>([
            Tokens("access", "rotated"),
            new(HttpStatusCode.NoContent),
            Reply(0xAB),
        ]);
        using var http = new HttpClient(new StubHandler(_ => Task.FromResult(responses.Dequeue())));
        var saved = new List<string>();
        var client = CreateClient(http, "saved", saved);

        Assert.Null(await client.SolveAsync(new byte[] { 1 }, CancellationToken.None));
        Assert.Equal(new byte[] { 0xAB }, await client.SolveAsync(new byte[] { 2 }, CancellationToken.None));
        Assert.Equal("rotated", Assert.Single(saved));
        Assert.Empty(responses);
    }

    [Fact]
    public async Task Solve_RetriesOnceWithRotatedSessionAfterUnauthorizedResponse()
    {
        var cancellationToken = CancellationToken.None;
        var calls = new Queue<Func<HttpRequestMessage, Task<HttpResponseMessage>>>([
            async request =>
            {
                Assert.Contains("refresh_token=saved", await request.Content!.ReadAsStringAsync(cancellationToken), StringComparison.Ordinal);
                return Tokens("access1", "rotated1");
            },
            request =>
            {
                Assert.Equal("access1", request.Headers.Authorization?.Parameter);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            },
            async request =>
            {
                Assert.Contains("refresh_token=rotated1", await request.Content!.ReadAsStringAsync(cancellationToken), StringComparison.Ordinal);
                return Tokens("access2", "rotated2");
            },
            request =>
            {
                Assert.Equal("access2", request.Headers.Authorization?.Parameter);
                return Task.FromResult(Reply(0xAA));
            },
        ]);
        using var http = new HttpClient(new StubHandler(request => calls.Dequeue()(request)));
        var saved = new List<string>();
        var client = CreateClient(http, "saved", saved);

        Assert.Equal(new byte[] { 0xAA }, await client.SolveAsync(new byte[] { 1 }, cancellationToken));
        Assert.Collection(saved, token => Assert.Equal("rotated1", token), token => Assert.Equal("rotated2", token));
        Assert.Empty(calls);
    }

    [Fact]
    public async Task Solve_CancelledBleAttemptDoesNotDiscardRotatedRefreshToken()
    {
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshRequests = 0;
        using var http = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri!.Host == "secure.zwift.com")
            {
                Interlocked.Increment(ref refreshRequests);
                refreshStarted.TrySetResult();
                return refreshResponse.Task;
            }

            return Task.FromResult(Reply(0xAB));
        }));
        var saved = new List<string>();
        var client = CreateClient(http, "saved", saved);
        using var cancelled = new CancellationTokenSource();

        var firstAttempt = client.SolveAsync(new byte[] { 1 }, cancelled.Token);
        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstAttempt);
        var newAttempt = client.SolveAsync(new byte[] { 2 }, CancellationToken.None);
        refreshResponse.SetResult(Tokens("access", "rotated"));

        Assert.Equal(new byte[] { 0xAB }, await newAttempt.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, refreshRequests);
        Assert.Equal("rotated", Assert.Single(saved));
    }

    private static ZwiftUnlockClient CreateClient(HttpClient http, string? refreshToken, List<string> saved) =>
        new(http, refreshToken, token =>
        {
            saved.Add(token);
            return Task.CompletedTask;
        });

    private static HttpResponseMessage Tokens(string access, string refresh) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            $$"""{"access_token":"{{access}}","refresh_token":"{{refresh}}","expires_in":3600}""",
            Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Reply(params byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}
