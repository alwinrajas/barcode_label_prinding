using System.Net;
using System.Net.Http.Json;
using BarcodePrinter.Api.Auth;
using BarcodePrinter.Contracts;
using BarcodePrinter.Contracts.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace BarcodePrinter.Integration.Tests;

/// <summary>
/// A-6 and §13 say authorization is enforced at the API, and that no endpoint is
/// unprotected by default. Both are properties of the whole surface, so they are
/// checked by ENUMERATING the surface rather than by testing endpoints someone
/// remembered to write a test for — the endpoint that gets forgotten is exactly
/// the one that ships unprotected.
/// </summary>
[Collection("api")]
public class EndpointSecurityTests(ApiFixture fx, ITestOutputHelper output)
{
    /// <summary>
    /// The only endpoints allowed to be anonymous, each with the reason.
    /// Adding to this list should require justifying the entry.
    /// </summary>
    private static readonly Dictionary<string, string> AllowedAnonymous = new()
    {
        ["/"] = "service banner: name and version only",
        ["/health"] = "liveness probe; monitoring cannot authenticate",
        ["/health/ready"] = "readiness probe",
        [ApiRoutes.Auth.Login] = "you cannot authenticate to authenticate",
        [ApiRoutes.Auth.Refresh] = "presents a refresh token, not a bearer token",
    };

    /// <summary>
    /// Endpoints that act on the CALLER and nobody else. There is no permission
    /// for "be yourself": every authenticated user must be able to see who they
    /// are, change their own password and sign out, whatever their role.
    /// </summary>
    private static readonly Dictionary<string, string> AllowedSelfService = new()
    {
        [ApiRoutes.Auth.Me] = "reads the caller's own identity and permissions",
        [ApiRoutes.Auth.ChangePassword] = "changes the caller's own password",
        [ApiRoutes.Auth.Logout] = "revokes the caller's own refresh token",
    };

    [Fact]
    public void Every_endpoint_is_authorized_or_explicitly_and_justifiably_anonymous()
    {
        var unprotected = new List<string>();

        foreach (var endpoint in RouteEndpoints())
        {
            var pattern = "/" + endpoint.RoutePattern.RawText?.TrimStart('/');
            var metadata = endpoint.Metadata;

            if (metadata.GetMetadata<IAllowAnonymous>() is not null)
            {
                AllowedAnonymous.Should().ContainKey(pattern,
                    $"'{pattern}' is anonymous and is not on the justified list");
                continue;
            }

            // The fallback policy covers authentication; a permission requirement
            // is what stops any authenticated user reaching an admin route.
            var hasPermission = metadata.GetOrderedMetadata<AuthorizationPolicy>()
                .Any(p => p.Requirements.OfType<PermissionRequirement>().Any());
            if (!hasPermission && !AllowedSelfService.ContainsKey(pattern))
            {
                unprotected.Add($"{string.Join(",", HttpMethods(endpoint))} {pattern}");
            }
        }

        foreach (var route in unprotected)
        {
            output.WriteLine(route);
        }

        unprotected.Should().BeEmpty(
            "every endpoint must declare the permission it requires — authentication alone " +
            "lets any operator reach user management");
    }

    /// <summary>Hiding a button is presentation. The endpoint behind it has to
    /// refuse as well, or a tampered client simply calls it (A-6).</summary>
    [Theory]
    [InlineData("GET", "/api/users")]
    [InlineData("GET", "/api/roles")]
    [InlineData("GET", "/api/audit")]
    [InlineData("GET", "/api/settings")]
    [InlineData("POST", "/api/printers")]
    public async Task An_operator_is_refused_by_the_server_not_just_by_the_hidden_button(
        string method, string path)
    {
        var user = await LoginAsync("it-user", ApiFixture.UserPassword);

        var response = await user.SendAsync(new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = method == "POST" ? JsonContent.Create(new { }) : null,
        });

        response.StatusCode.Should().BeOneOf(
            [HttpStatusCode.Forbidden, HttpStatusCode.NotFound],
            $"{method} {path} must not be reachable by an operator");
    }

    [Fact]
    public async Task No_endpoint_accepts_an_unsigned_or_tampered_token()
    {
        var client = fx.CreateClient();

        // A structurally valid JWT signed with the wrong key.
        const string forged =
            "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9." +
            "eyJzdWIiOiIxIiwibmFtZSI6ImFkbWluIiwicGVybSI6IlVzZXIuVmlldyJ9." +
            "Zm9yZ2VkLXNpZ25hdHVyZS10aGF0LWlzLW5vdC12YWxpZA";
        client.DefaultRequestHeaders.Authorization = new("Bearer", forged);

        (await client.GetAsync(ApiRoutes.Users.List)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>An error must identify itself well enough to support the user
    /// and no better — §13 forbids stack traces and SQL crossing the wire.</summary>
    [Fact]
    public async Task Error_responses_carry_a_code_and_correlation_id_but_no_internals()
    {
        var admin = await LoginAsync("it-admin", ApiFixture.AdminPassword);

        var response = await admin.GetAsync(ApiRoutes.Products.ById(999_999_999));
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        body.Should().Contain(ErrorCodes.NotFound);
        response.Headers.Should().ContainKey("X-Correlation-Id");

        body.Should().NotContainAny(
            ["at BarcodePrinter.", "MySqlConnector", "SELECT ", "StackTrace", "ConnectionString"]);
    }

    /// <summary>Nothing that would let a client reach the database, mint a token
    /// or decrypt the Oracle password may ever appear in a response (A-28).</summary>
    [Fact]
    public async Task No_response_leaks_a_secret()
    {
        var admin = await LoginAsync("it-admin", ApiFixture.AdminPassword);

        string[] paths =
        [
            ApiRoutes.Settings.Base,
            ApiRoutes.Users.List,
            ApiRoutes.Printers.Base + "/",
            "/",
        ];

        foreach (var path in paths)
        {
            var body = await (await admin.GetAsync(path)).Content.ReadAsStringAsync();
            body.Should().NotContainAny(
                ["password_hash", "passwordHash", "security_stamp", "securityStamp",
                 "SigningKey", "signingKey", "ConnectionString", "connectionString",
                 "Uid=", "Pwd=", "password_protected"],
                $"{path} must not disclose credentials or key material");
        }
    }

    [Fact]
    public async Task The_login_endpoint_does_not_reveal_whether_an_account_exists()
    {
        var client = fx.CreateClient();

        var noSuchUser = await client.PostAsJsonAsync(ApiRoutes.Auth.Login,
            new LoginRequest("definitely-not-a-user", "Whatever@1!", "test"));
        var wrongPassword = await client.PostAsJsonAsync(ApiRoutes.Auth.Login,
            new LoginRequest("it-admin", "Whatever@1!", "test"));

        noSuchUser.StatusCode.Should().Be(wrongPassword.StatusCode);

        // Compare everything EXCEPT the correlation id, which is a fresh GUID
        // per request and carries no information about the account.
        static async Task<string> ShapeOf(HttpResponseMessage response)
        {
            var problem = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
            problem!.Remove("correlationId");
            return string.Join("|", problem.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
        }

        (await ShapeOf(noSuchUser)).Should().Be(await ShapeOf(wrongPassword),
            "differing responses turn the login form into an account enumerator");

        // NOT asserted here: response TIMING. AuthService verifies a dummy hash
        // for unknown users so the two paths cost the same, but measuring that
        // against an in-memory test server is noise, and probing it repeatedly
        // trips the lockout on a shared account and fails unrelated tests.
    }

    private IEnumerable<RouteEndpoint> RouteEndpoints() =>
        fx.Factory.Services.GetRequiredService<EndpointDataSource>()
            .Endpoints.OfType<RouteEndpoint>()
            // Hubs are authorized by [Authorize] on the class and their negotiate
            // route carries no permission metadata; covered by PrintStatusHubTests.
            .Where(e => !(e.RoutePattern.RawText ?? "").StartsWith("/hubs/"));

    private static IEnumerable<string> HttpMethods(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()
            ?.HttpMethods ?? ["*"];

    private async Task<HttpClient> LoginAsync(string username, string password)
    {
        var client = fx.CreateClient();
        var response = await client.PostAsJsonAsync(ApiRoutes.Auth.Login,
            new LoginRequest(username, password, "it-tests"));
        response.EnsureSuccessStatusCode();
        var login = (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", login.AccessToken);
        return client;
    }
}
