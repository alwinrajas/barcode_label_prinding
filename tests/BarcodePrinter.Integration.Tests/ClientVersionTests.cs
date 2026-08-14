using System.Net;
using System.Net.Http.Json;
using BarcodePrinter.Contracts;
using BarcodePrinter.Contracts.Auth;
using FluentAssertions;
using Xunit;

namespace BarcodePrinter.Integration.Tests;

/// <summary>
/// The version gate (§16) is what makes sharing the Contracts assembly across
/// the tiers safe. It has to refuse old builds, and — just as importantly — it
/// must not refuse anything else, or a health probe takes the server "down".
/// </summary>
[Collection("api")]
public class ClientVersionTests(ApiFixture fx)
{
    // ApiFixture configures MinimumClientVersion = 1.0.0.
    private const string TooOld = "0.9.9";
    private const string Current = "1.0.0";
    private const string Newer = "1.4.0";

    [Fact]
    public async Task A_client_below_the_minimum_is_refused_with_an_actionable_code()
    {
        var client = fx.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Version", TooOld);

        var response = await client.PostAsJsonAsync(ApiRoutes.Auth.Login,
            new LoginRequest("it-admin", ApiFixture.AdminPassword, "old-workstation"));

        response.StatusCode.Should().Be(HttpStatusCode.UpgradeRequired);

        var problem = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        problem!["code"].ToString().Should().Be(ErrorCodes.ClientUpdateRequired);
        problem["minimumClientVersion"].ToString().Should().Be("1.0.0");
    }

    /// <summary>The refusal must land BEFORE authentication. An out-of-date
    /// client that can still log in has already read a payload it may not
    /// understand.</summary>
    [Fact]
    public async Task The_gate_applies_before_credentials_are_even_checked()
    {
        var client = fx.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Version", TooOld);

        var response = await client.PostAsJsonAsync(ApiRoutes.Auth.Login,
            new LoginRequest("it-admin", "quite-wrong-password", "old-workstation"));

        response.StatusCode.Should().Be(HttpStatusCode.UpgradeRequired,
            "not 401 — the version is the reason, and saying so avoids a wild goose chase");
    }

    [Theory]
    [InlineData(Current)]
    [InlineData(Newer)]
    public async Task A_current_or_newer_client_is_allowed(string version)
    {
        var client = fx.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Version", version);

        var response = await client.PostAsJsonAsync(ApiRoutes.Auth.Login,
            new LoginRequest("it-admin", ApiFixture.AdminPassword, "workstation"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>Health probes, monitoring and curl send no version. Refusing
    /// them would make the monitoring report an outage that is not happening.</summary>
    [Fact]
    public async Task No_version_header_is_allowed_through()
    {
        var client = fx.CreateClient();

        (await client.GetAsync("/health")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsJsonAsync(ApiRoutes.Auth.Login,
            new LoginRequest("it-admin", ApiFixture.AdminPassword, "curl")))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_unparseable_version_is_allowed_rather_than_locking_everyone_out()
    {
        var client = fx.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Version", "not-a-version");

        (await client.PostAsJsonAsync(ApiRoutes.Auth.Login,
            new LoginRequest("it-admin", ApiFixture.AdminPassword, "odd-client")))
            .StatusCode.Should().Be(HttpStatusCode.OK,
            "a garbled header is a bug to investigate, not a reason to stop the line");
    }

    [Fact]
    public async Task Login_still_tells_the_client_what_the_minimum_is()
    {
        var client = fx.CreateClient();
        var response = await client.PostAsJsonAsync(ApiRoutes.Auth.Login,
            new LoginRequest("it-admin", ApiFixture.AdminPassword, "workstation"));

        var login = (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
        login.MinimumClientVersion.Should().Be("1.0.0");
    }
}
