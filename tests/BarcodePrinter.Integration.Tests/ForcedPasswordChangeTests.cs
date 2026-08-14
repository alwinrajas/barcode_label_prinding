using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BarcodePrinter.Contracts;
using BarcodePrinter.Contracts.Auth;
using FluentAssertions;
using MySqlConnector;
using Xunit;

namespace BarcodePrinter.Integration.Tests;

/// <summary>
/// Changing a password rotates the security stamp, which is what revokes the
/// caller's own token. Without a replacement session the user completes the
/// forced change and lands in an application where every request 401s — the
/// exact experience a first-time administrator gets on a fresh installation.
/// </summary>
[Collection("api")]
public class ForcedPasswordChangeTests(ApiFixture fx)
{
    [Fact]
    public async Task Changing_a_password_returns_a_session_that_works_immediately()
    {
        var (client, username) = await SeedUserRequiringChangeAsync();

        var login = await LoginAsync(client, username, "Initial@Pass1!");
        login.MustChangePassword.Should().BeTrue();
        client.DefaultRequestHeaders.Authorization = new("Bearer", login.AccessToken);

        var response = await client.PostAsJsonAsync(ApiRoutes.Auth.ChangePassword,
            new ChangePasswordRequest("Initial@Pass1!", "Replaced@Pass1!", "it-tests"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var refreshed = (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
        refreshed.AccessToken.Should().NotBeNullOrEmpty();
        refreshed.AccessToken.Should().NotBe(login.AccessToken, "the old token was just revoked");
        refreshed.MustChangePassword.Should().BeFalse();
        refreshed.User.Permissions.Should().NotBeEmpty("the shell needs permissions to draw itself");

        // The new token must actually work — this is the assertion that would
        // have caught the original defect.
        client.DefaultRequestHeaders.Authorization = new("Bearer", refreshed.AccessToken);
        (await client.GetAsync(ApiRoutes.Auth.Me)).StatusCode.Should().Be(HttpStatusCode.OK);
        // A permission-gated endpoint this role actually holds, so a 403 here
        // would mean the replacement token lost its claims.
        (await client.GetAsync(ApiRoutes.Dashboard.Base)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_old_password_stops_working_and_the_new_one_starts()
    {
        var (client, username) = await SeedUserRequiringChangeAsync();
        var login = await LoginAsync(client, username, "Initial@Pass1!");
        client.DefaultRequestHeaders.Authorization = new("Bearer", login.AccessToken);

        (await client.PostAsJsonAsync(ApiRoutes.Auth.ChangePassword,
            new ChangePasswordRequest("Initial@Pass1!", "Replaced@Pass1!", "it-tests")))
            .EnsureSuccessStatusCode();

        var fresh = fx.CreateClient();
        (await fresh.PostAsJsonAsync(ApiRoutes.Auth.Login,
            new LoginRequest(username, "Initial@Pass1!", "it-tests")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "the old password must be dead");

        (await fresh.PostAsJsonAsync(ApiRoutes.Auth.Login,
            new LoginRequest(username, "Replaced@Pass1!", "it-tests")))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_wrong_current_password_is_refused_and_changes_nothing()
    {
        var (client, username) = await SeedUserRequiringChangeAsync();
        var login = await LoginAsync(client, username, "Initial@Pass1!");
        client.DefaultRequestHeaders.Authorization = new("Bearer", login.AccessToken);

        (await client.PostAsJsonAsync(ApiRoutes.Auth.ChangePassword,
            new ChangePasswordRequest("NotTheCurrent@1!", "Replaced@Pass1!", "it-tests")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // The original session must survive a rejected attempt.
        (await client.GetAsync(ApiRoutes.Auth.Me)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<(HttpClient Client, string Username)> SeedUserRequiringChangeAsync()
    {
        var username = $"it-pwchange-{Guid.NewGuid():N}"[..24];
        var hash = new Microsoft.AspNetCore.Identity.PasswordHasher<object>()
            .HashPassword(null!, "Initial@Pass1!");

        await using var conn = await fx.OpenDbAsync();
        await using var cmd = new MySqlCommand(
            """
            INSERT INTO users (username, full_name, password_hash, security_stamp,
                               is_active, must_change_password, concurrency_stamp, created_at)
            VALUES (@u, @u, @h, UUID(), 1, 1, UUID(), UTC_TIMESTAMP(3));
            INSERT INTO user_roles (user_id, role_id)
            SELECT u.id, r.id FROM users u JOIN roles r ON r.code = 'User' WHERE u.username = @u;
            """, conn);
        cmd.Parameters.AddWithValue("@u", username);
        cmd.Parameters.AddWithValue("@h", hash);
        await cmd.ExecuteNonQueryAsync();

        return (fx.CreateClient(), username);
    }

    private static async Task<LoginResponse> LoginAsync(
        HttpClient client, string username, string password)
    {
        var response = await client.PostAsJsonAsync(ApiRoutes.Auth.Login,
            new LoginRequest(username, password, "it-tests"));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
    }
}
