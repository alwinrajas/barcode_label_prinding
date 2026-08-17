using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using BarcodePrinter.Client.Core;
using BarcodePrinter.Contracts.Auth;

namespace BarcodePrinter.Wpf.ViewModels.Tests;

/// <summary>
/// Routes fake API responses by request path so a ViewModel can be constructed
/// against a real ApiClient. ViewModels fire several independent calls from
/// their constructor, so matching by path (not a strict queue) is what keeps
/// these tests from depending on call ordering.
/// </summary>
public sealed class RoutingHandler : HttpMessageHandler
{
    private readonly List<(string? Method, string PathFragment, Func<HttpRequestMessage, HttpResponseMessage> Respond)> _routes = [];

    /// <summary>Every request seen, with its body — lets a test assert what
    /// actually went on the wire.</summary>
    public List<(string Method, string Path, string Body)> Requests { get; } = [];

    public RoutingHandler Route(string pathFragment, object body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes.Add((null, pathFragment, _ => Json(body, status)));
        return this;
    }

    public RoutingHandler Route(
        string pathFragment, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _routes.Add((null, pathFragment, respond));
        return this;
    }

    /// <summary>Method-scoped route, so overriding (say) POST /api/products for
    /// a create does not also hijack the GET that reloads the grid.</summary>
    public RoutingHandler RouteMethod(
        HttpMethod method, string pathFragment, object body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes.Add((method.Method, pathFragment, _ => Json(body, status)));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        Requests.Add((request.Method.Method, path, body));

        // Last matching route wins, so a test can override a default.
        for (var i = _routes.Count - 1; i >= 0; i--)
        {
            var (method, fragment, respond) = _routes[i];
            if (method is not null && !string.Equals(method, request.Method.Method, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (path.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return respond(request);
            }
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
    }

    public static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                Encoding.UTF8, "application/json"),
        };

    /// <summary>An authenticated ApiClient wired to this handler.</summary>
    public async Task<ApiClient> LoggedInClientAsync()
    {
        Route("/auth/login", new
        {
            accessToken = "test-token",
            accessTokenExpiresUtc = DateTime.UtcNow.AddMinutes(15),
            refreshToken = "test-refresh",
            refreshTokenExpiresUtc = DateTime.UtcNow.AddDays(1),
            user = new
            {
                id = 1L, username = "op", fullName = "Operator",
                roles = new[] { "Operator" }, permissions = TestSession.AllPermissions,
            },
            mustChangePassword = false,
            minimumClientVersion = "1.0",
        });

        var api = new ApiClient(
            new HttpClient(this) { BaseAddress = new Uri("https://server.test") },
            new ConnectionStatus());
        await api.LoginAsync("op", "pw", CancellationToken.None);
        return api;
    }
}

public static class TestSession
{
    /// <summary>Every permission, so a test exercises behaviour rather than
    /// tripping over authorisation gating.</summary>
    public static readonly string[] AllPermissions = BarcodePrinter.Contracts.PermissionCodes.All.ToArray();

    public static Session Create() => new(
        new UserInfo(1, "op", "Operator", ["Operator"], AllPermissions), false);
}
