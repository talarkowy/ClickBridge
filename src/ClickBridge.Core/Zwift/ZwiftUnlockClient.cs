using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClickBridge.Core.Zwift;

/// <summary>Obtains signed Click v2 unlock responses using a Zwift account session.</summary>
public sealed class ZwiftUnlockClient(
    HttpClient http,
    string? refreshToken,
    Func<string, Task> saveRefreshToken)
{
    private const string ClientId = "Zwift Game Client";
    private const string Protobuf = "application/x-protobuf-lite";
    private static readonly Uri TokenUri = new("https://secure.zwift.com/auth/realms/zwift/protocol/openid-connect/token");
    private static readonly Uri UnlockUri = new("https://us-or-rly101.zwift.com/api/d-lock-service/device/authenticate");
    private static readonly TimeSpan RefreshLeeway = TimeSpan.FromSeconds(30);

    private readonly Lock _sessionLock = new();
    private string? _refreshToken = refreshToken;
    private Session? _session;
    private Task<Session>? _refresh;

    public bool HasSession
    {
        get
        {
            lock (_sessionLock)
            {
                return _refreshToken is not null;
            }
        }
    }

    public async Task<string> LoginAsync(string email, string password, CancellationToken cancellationToken)
    {
        var session = await RequestSessionAsync("password", new Dictionary<string, string>
        {
            ["username"] = email,
            ["password"] = password,
        }, previousRefreshToken: null, cancellationToken).ConfigureAwait(false);
        UpdateSession(session);
        return session.RefreshToken;
    }

    public async Task<byte[]> SolveAsync(ReadOnlyMemory<byte> challenge, CancellationToken cancellationToken)
    {
        var session = await GetSessionAsync(rejectedAccessToken: null, cancellationToken).ConfigureAwait(false);
        try
        {
            return await RequestReplyAsync(challenge, session.AccessToken, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            session = await GetSessionAsync(session.AccessToken, cancellationToken).ConfigureAwait(false);
            return await RequestReplyAsync(challenge, session.AccessToken, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task<Session> GetSessionAsync(string? rejectedAccessToken, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sessionLock)
        {
            if (_session is { } session && session.AccessToken != rejectedAccessToken
                && session.ExpiresAt > DateTimeOffset.UtcNow + RefreshLeeway)
            {
                return Task.FromResult(session);
            }

            var token = _refreshToken ?? throw new UnauthorizedAccessException("Zwift sign-in required.");
            // A token refresh can rotate the stored token. Let it finish even if a BLE session ends.
            if (_refresh is null || _refresh.IsCompleted)
            {
                _refresh = RefreshSessionAsync(token);
            }

            return _refresh.WaitAsync(cancellationToken);
        }
    }

    private async Task<Session> RefreshSessionAsync(string token)
    {
        Session session;
        try
        {
            session = await RequestSessionAsync("refresh_token", new Dictionary<string, string>
            {
                ["refresh_token"] = token,
            }, token, CancellationToken.None).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException)
        {
            lock (_sessionLock)
            {
                _session = null;
                _refreshToken = null;
            }

            throw;
        }

        await saveRefreshToken(session.RefreshToken).ConfigureAwait(false);
        UpdateSession(session);
        return session;
    }

    private void UpdateSession(Session session)
    {
        lock (_sessionLock)
        {
            _refreshToken = session.RefreshToken;
            _session = session;
        }
    }

    private async Task<Session> RequestSessionAsync(
        string grantType, Dictionary<string, string> fields, string? previousRefreshToken, CancellationToken cancellationToken)
    {
        fields["grant_type"] = grantType;
        fields["client_id"] = ClientId;
        using var content = new FormUrlEncodedContent(fields);
        using var response = await http.PostAsync(TokenUri, content, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.BadRequest && IsInvalidGrant(body))
            {
                throw new UnauthorizedAccessException("Zwift rejected the sign-in session or credentials.");
            }

            throw new HttpRequestException("Zwift token request failed.", null, response.StatusCode);
        }

        var tokens = JsonSerializer.Deserialize<TokenResponse>(body);
        var nextRefreshToken = string.IsNullOrEmpty(tokens?.RefreshToken) ? previousRefreshToken : tokens.RefreshToken;
        if (string.IsNullOrEmpty(tokens?.AccessToken) || string.IsNullOrEmpty(nextRefreshToken))
        {
            throw new InvalidDataException("Zwift did not return usable session tokens.");
        }

        return new Session(tokens.AccessToken, nextRefreshToken, DateTimeOffset.UtcNow.AddSeconds(tokens.ExpiresIn));
    }

    private async Task<byte[]> RequestReplyAsync(
        ReadOnlyMemory<byte> challenge, string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, UnlockUri)
        {
            Content = new ReadOnlyMemoryContent(challenge),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.ParseAdd(Protobuf);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(Protobuf);

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new HttpRequestException("Zwift unlock request failed.", null, response.StatusCode);
        }

        var reply = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return reply.Length > 0 ? reply : throw new InvalidDataException("Zwift returned an empty unlock response.");
    }

    private static bool IsInvalidGrant(byte[] body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String
                && error.ValueEquals("invalid_grant");
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed record Session(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
