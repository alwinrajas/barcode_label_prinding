using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BarcodePrinter.Contracts;
using BarcodePrinter.Contracts.Auth;

namespace BarcodePrinter.Client.Core;

/// <summary>Error surfaced to ViewModels: a stable code (for the message
/// catalogue) + correlation id (shown in error dialogs, §21.2).</summary>
public sealed class ApiException(string code, string message, string? correlationId, HttpStatusCode status)
    : Exception(message)
{
    public string Code { get; } = code;
    public string? CorrelationId { get; } = correlationId;
    public HttpStatusCode Status { get; } = status;
}

public sealed class ApiUnreachableException(Exception inner)
    : Exception("The server could not be reached.", inner);

/// <summary>
/// Typed API client (blueprint §6 Client.Core). Owns the access/refresh token
/// pair, attaches the bearer header, transparently refreshes once on 401, and
/// maps ProblemDetails to ApiException. The client never holds DB credentials —
/// its only server contact is HTTPS (A-28).
/// </summary>
public sealed class ApiClient(HttpClient http, ConnectionStatus connection)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private string? _accessToken;
    private string? _refreshToken;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public Session? Session { get; private set; }

    /// <summary>Current bearer token — consumed by SignalR hub connections
    /// (websockets cannot carry an Authorization header).</summary>
    public string? AccessToken => _accessToken;

    /// <summary>Absolute base address, for hub URL composition.</summary>
    public Uri BaseAddress => http.BaseAddress!;

    public async Task<LoginResponse> LoginAsync(string username, string password, CancellationToken ct)
    {
        var response = await SendRawAsync(
            () => new HttpRequestMessage(HttpMethod.Post, ApiRoutes.Auth.Login)
            {
                Content = JsonContent.Create(new LoginRequest(username, password, Environment.MachineName)),
            }, ct);
        var login = await ReadOrThrowAsync<LoginResponse>(response, ct);

        _accessToken = login.AccessToken;
        _refreshToken = login.RefreshToken;
        Session = new Session(login.User, login.MustChangePassword);
        return login;
    }

    public async Task LogoutAsync(CancellationToken ct)
    {
        if (_refreshToken is null)
        {
            return;
        }
        try
        {
            await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, ApiRoutes.Auth.Logout)
            {
                Content = JsonContent.Create(new LogoutRequest(_refreshToken)),
            }, ct);
        }
        finally
        {
            _accessToken = null;
            _refreshToken = null;
            Session = null;
        }
    }

    public async Task ChangePasswordAsync(string current, string newPassword, CancellationToken ct)
    {
        var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, ApiRoutes.Auth.ChangePassword)
        {
            Content = JsonContent.Create(
                new ChangePasswordRequest(current, newPassword, Environment.MachineName)),
        }, ct);

        // Changing the password revoked the token this very request was made
        // with. The server returns a replacement session; adopting it is what
        // stops the user landing in a shell where everything fails.
        var login = await ReadOrThrowAsync<LoginResponse>(response, ct);
        _accessToken = login.AccessToken;
        _refreshToken = login.RefreshToken;
        Session = new Session(login.User, login.MustChangePassword);
    }

    public async Task<IReadOnlyList<UserSummary>> GetUsersAsync(CancellationToken ct)
    {
        var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, ApiRoutes.Users.List), ct);
        return await ReadOrThrowAsync<IReadOnlyList<UserSummary>>(response, ct);
    }

    // ---- generic verbs (used by feature API classes) -----------------------

    public async Task<T> GetAsync<T>(string url, CancellationToken ct)
    {
        var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);
        return await ReadOrThrowAsync<T>(response, ct);
    }

    public async Task<byte[]?> GetBytesAsync(string url, CancellationToken ct)
    {
        var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        await ThrowIfProblemAsync(response, ct);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<TRes> PostAsync<TReq, TRes>(string url, TReq body, CancellationToken ct)
    {
        var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body),
        }, ct);
        return await ReadOrThrowAsync<TRes>(response, ct);
    }

    public async Task PostAsync(string url, CancellationToken ct)
    {
        var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, url), ct);
        await ThrowIfProblemAsync(response, ct);
    }

    /// <summary>POST returning plain text (label preview ZPL).</summary>
    public async Task<string> PostForTextAsync<TReq>(string url, TReq body, CancellationToken ct)
    {
        var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body),
        }, ct);
        await ThrowIfProblemAsync(response, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>POST where a 409 is an expected outcome, not an error — used by
    /// the print dispatcher when another workstation claimed the job first.</summary>
    public async Task<bool> PostForStatusAsync(string url, CancellationToken ct)
    {
        var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, url), ct);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return false;
        }
        await ThrowIfProblemAsync(response, ct);
        return true;
    }

    public async Task PutAsync<TReq>(string url, TReq body, CancellationToken ct)
    {
        var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = JsonContent.Create(body),
        }, ct);
        await ThrowIfProblemAsync(response, ct);
    }

    public async Task DeleteAsync(string url, CancellationToken ct)
    {
        var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, url), ct);
        await ThrowIfProblemAsync(response, ct);
    }

    /// <summary>Multipart POST. Takes a content *factory* (a content stream is
    /// single-shot) so the request goes through the same pipeline as every
    /// other call — bearer header, refresh-on-401 retry, unreachable/timeout
    /// mapping. Uploads no longer die when the access token expires.</summary>
    public async Task<T> PostMultipartAsync<T>(
        string url, Func<MultipartFormDataContent> contentFactory, CancellationToken ct)
    {
        var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, url) { Content = contentFactory() }, ct);
        return await ReadOrThrowAsync<T>(response, ct);
    }

    // ---- internals ---------------------------------------------------------

    /// <summary>Sends with bearer; on 401 refreshes the token pair once and retries.</summary>
    private async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> requestFactory, CancellationToken ct)
    {
        var response = await SendRawAsync(requestFactory, ct);
        if (response.StatusCode != HttpStatusCode.Unauthorized || _refreshToken is null)
        {
            return response;
        }

        await RefreshAsync(ct);
        return await SendRawAsync(requestFactory, ct);
    }

    private async Task<HttpResponseMessage> SendRawAsync(
        Func<HttpRequestMessage> requestFactory, CancellationToken ct)
    {
        // Disposing the request disposes its content chain — this is what
        // deterministically closes the FileStream under a multipart upload,
        // and it is why retries need a factory rather than a reusable request.
        using var request = requestFactory();
        request.Headers.Add("X-Correlation-Id", Guid.NewGuid().ToString());
        if (_accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        }
        try
        {
            var response = await http.SendAsync(request, ct);
            connection.ReportSuccess();
            return response;
        }
        catch (HttpRequestException ex)
        {
            connection.ReportUnreachable();
            throw new ApiUnreachableException(ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            connection.ReportUnreachable();
            throw new ApiUnreachableException(ex);   // timeout, not user cancel
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        await _refreshLock.WaitAsync(ct);
        try
        {
            if (_refreshToken is null)
            {
                return;
            }
            var response = await SendRawAsync(
                () => new HttpRequestMessage(HttpMethod.Post, ApiRoutes.Auth.Refresh)
                {
                    Content = JsonContent.Create(new RefreshRequest(_refreshToken, Environment.MachineName)),
                }, ct);
            var refreshed = await ReadOrThrowAsync<RefreshResponse>(response, ct);
            _accessToken = refreshed.AccessToken;
            _refreshToken = refreshed.RefreshToken;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private static async Task<T> ReadOrThrowAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        await ThrowIfProblemAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    private static async Task ThrowIfProblemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string code = ErrorCodes.Unexpected;
        string message = "An unexpected error occurred.";
        string? correlationId = null;
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("code", out var c)) code = c.GetString() ?? code;
            if (doc.RootElement.TryGetProperty("detail", out var d)) message = d.GetString() ?? message;
            if (doc.RootElement.TryGetProperty("correlationId", out var id)) correlationId = id.GetString();
        }
        catch (JsonException)
        {
            // Non-ProblemDetails body (proxy error page etc.) — keep defaults.
        }
        throw new ApiException(code, message, correlationId, response.StatusCode);
    }
}

/// <summary>Authenticated user state for the client session.</summary>
public sealed class Session(UserInfo user, bool mustChangePassword)
{
    public UserInfo User { get; } = user;
    public bool MustChangePassword { get; } = mustChangePassword;

    public bool Has(string permission) => User.Permissions.Contains(permission);

    public Session WithPasswordChanged() => new(User, false);
}
