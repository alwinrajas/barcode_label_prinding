using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using BarcodePrinter.Client.Core;
using FluentAssertions;
using Xunit;

namespace BarcodePrinter.Wpf.ViewModels.Tests;

/// <summary>
/// Regression tests for the defect where multipart uploads bypassed the
/// refresh-on-401 pipeline: after the 15-minute access token expired, every
/// image/import upload failed while ordinary calls recovered transparently.
/// </summary>
public sealed class ApiClientMultipartTests
{
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public List<(string Path, string? Authorization, bool HadMultipartBody)> Requests { get; } = [];
        public Queue<Func<HttpRequestMessage, HttpResponseMessage>> Script { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var multipart = request.Content is MultipartFormDataContent;
            if (multipart)
            {
                // Reading proves the content stream is fresh and readable on
                // every attempt — a reused single-shot stream would be empty
                // or throw here on the retry.
                var body = await request.Content!.ReadAsByteArrayAsync(ct);
                body.Length.Should().BeGreaterThan(0, "each attempt must carry a fresh content stream");
            }
            Requests.Add((request.RequestUri!.AbsolutePath,
                request.Headers.Authorization?.Parameter, multipart));
            return Script.Dequeue()(request);
        }
    }

    private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json"),
        };

    private static object LoginBody(string access, string refresh) => new
    {
        accessToken = access,
        accessTokenExpiresUtc = DateTime.UtcNow.AddMinutes(15),
        refreshToken = refresh,
        refreshTokenExpiresUtc = DateTime.UtcNow.AddDays(1),
        user = new
        {
            id = 1L, username = "op", fullName = "Operator",
            roles = Array.Empty<string>(), permissions = Array.Empty<string>(),
        },
        mustChangePassword = false,
        minimumClientVersion = "1.0",
    };

    private static async Task<(ApiClient Api, ScriptedHandler Handler)> LoggedInClientAsync()
    {
        var handler = new ScriptedHandler();
        handler.Script.Enqueue(_ => Json(LoginBody("token-1", "refresh-1")));
        var api = new ApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://server.test") },
            new ConnectionStatus());
        await api.LoginAsync("op", "pw", CancellationToken.None);
        return (api, handler);
    }

    private static Func<MultipartFormDataContent> ContentFactory() => () =>
    {
        var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent("payload-bytes"u8.ToArray()), "file", "test.jpg");
        return content;
    };

    [Fact]
    public async Task Multipart_refreshes_the_token_and_retries_once_on_401()
    {
        var (api, handler) = await LoggedInClientAsync();
        handler.Script.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        handler.Script.Enqueue(_ => Json(new
        {
            accessToken = "token-2",
            accessTokenExpiresUtc = DateTime.UtcNow.AddMinutes(15),
            refreshToken = "refresh-2",
            refreshTokenExpiresUtc = DateTime.UtcNow.AddDays(1),
        }));
        handler.Script.Enqueue(_ => Json(new { hash = "abc123" }));

        var result = await api.PostMultipartAsync<HashOnly>(
            "/api/products/1/image", ContentFactory(), CancellationToken.None);

        result.Hash.Should().Be("abc123");
        handler.Requests.Should().HaveCount(4);                       // login, 401 attempt, refresh, retry
        handler.Requests[1].Authorization.Should().Be("token-1");
        handler.Requests[2].Path.Should().EndWith("/auth/refresh");
        handler.Requests[3].Authorization.Should().Be("token-2", "the retry must carry the refreshed token");
        handler.Requests[3].HadMultipartBody.Should().BeTrue();
    }

    [Fact]
    public async Task Multipart_succeeds_first_time_without_refresh()
    {
        var (api, handler) = await LoggedInClientAsync();
        handler.Script.Enqueue(_ => Json(new { hash = "abc123" }));

        var result = await api.PostMultipartAsync<HashOnly>(
            "/api/products/1/image", ContentFactory(), CancellationToken.None);

        result.Hash.Should().Be("abc123");
        handler.Requests.Should().HaveCount(2);                       // login + upload only
    }

    [Fact]
    public async Task Multipart_maps_problem_details_to_ApiException()
    {
        var (api, handler) = await LoggedInClientAsync();
        handler.Script.Enqueue(_ => Json(new
        {
            status = 400, title = "IMAGE_TOO_LARGE", detail = "The image must be between 1 byte and 5 MB.",
            code = "IMAGE_TOO_LARGE", correlationId = "BP-TEST1",
        }, HttpStatusCode.BadRequest));

        var act = () => api.PostMultipartAsync<HashOnly>(
            "/api/products/1/image", ContentFactory(), CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<ApiException>()).Which;
        ex.Code.Should().Be("IMAGE_TOO_LARGE");
        ex.CorrelationId.Should().Be("BP-TEST1");
        ex.Message.Should().Contain("5 MB");
    }

    private sealed record HashOnly(string Hash);
}
