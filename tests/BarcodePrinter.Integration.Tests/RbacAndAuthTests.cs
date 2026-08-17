using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BarcodePrinter.Contracts;
using BarcodePrinter.Contracts.Auth;
using FluentAssertions;
using Xunit;

namespace BarcodePrinter.Integration.Tests;

/// <summary>
/// The blueprint's RBAC exit criterion (§19.2 / phase 2): authorization is
/// proven at the API with real tokens against a real MySQL — the hidden
/// button is never the control.
/// </summary>
[Collection("api")]
public class RbacAndAuthTests(ApiFixture fx)
{
    // ---- RBAC bypass matrix ------------------------------------------------

    [Fact]
    public async Task Unauthenticated_request_is_401()
    {
        var client = fx.CreateClient();
        var response = await client.GetAsync(ApiRoutes.Users.List);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("it-user", ApiFixture.UserPassword, HttpStatusCode.Forbidden)]
    [InlineData("it-manager", ApiFixture.ManagerPassword, HttpStatusCode.Forbidden)]
    [InlineData("it-admin", ApiFixture.AdminPassword, HttpStatusCode.OK)]
    public async Task Users_endpoint_enforces_UserView_permission(
        string username, string password, HttpStatusCode expected)
    {
        var client = await LoginClientAsync(username, password);
        var response = await client.GetAsync(ApiRoutes.Users.List);
        response.StatusCode.Should().Be(expected,
            "the API — not the hidden button — is the authorization control");
    }

    [Fact]
    public async Task Login_returns_permissions_matching_role()
    {
        var login = await LoginAsync("it-user", ApiFixture.UserPassword);
        login.User.Permissions.Should().Contain(PermissionCodes.PrintExecute);
        login.User.Permissions.Should().NotContain(PermissionCodes.UserView);
        login.User.Permissions.Should().NotContain(PermissionCodes.PrintReprint,
            "C-15 is unresolved: Users do not get Reprint until the client decides");
    }

    // ---- Login failures ----------------------------------------------------

    [Fact]
    public async Task Wrong_password_and_unknown_user_return_the_same_error()
    {
        var client = fx.CreateClient();

        var wrongPassword = await client.PostAsJsonAsync(ApiRoutes.Auth.Login,
            new LoginRequest("it-admin", "not-the-password", null));
        var unknownUser = await client.PostAsJsonAsync(ApiRoutes.Auth.Login,
            new LoginRequest("does-not-exist", "whatever", null));

        wrongPassword.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        unknownUser.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body1 = await wrongPassword.Content.ReadAsStringAsync();
        var body2 = await unknownUser.Content.ReadAsStringAsync();
        body1.Should().Contain(ErrorCodes.LoginFailed);
        body2.Should().Contain(ErrorCodes.LoginFailed, "no account enumeration (§19.3)");
    }

    // ---- Refresh rotation & reuse detection --------------------------------

    [Fact]
    public async Task Refresh_rotates_and_replayed_token_revokes_the_chain()
    {
        var login = await LoginAsync("it-manager", ApiFixture.ManagerPassword);
        var client = fx.CreateClient();

        // Rotate once — old token is now spent.
        var first = await client.PostAsJsonAsync(ApiRoutes.Auth.Refresh,
            new RefreshRequest(login.RefreshToken, null));
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var rotated = (await first.Content.ReadFromJsonAsync<RefreshResponse>())!;

        // Replay the SPENT token → reuse detected → whole chain revoked.
        var replay = await client.PostAsJsonAsync(ApiRoutes.Auth.Refresh,
            new RefreshRequest(login.RefreshToken, null));
        replay.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // The rotated (newest) token must now be dead too.
        var afterBreach = await client.PostAsJsonAsync(ApiRoutes.Auth.Refresh,
            new RefreshRequest(rotated.RefreshToken, null));
        afterBreach.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a replayed refresh token means the token leaked — the chain dies");
    }

    // ---- Security-stamp revocation ------------------------------------------

    [Fact]
    public async Task Bumping_the_security_stamp_kills_live_access_tokens()
    {
        var login = await LoginAsync("it-user", ApiFixture.UserPassword);
        var client = fx.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", login.AccessToken);

        // Token works now.
        (await client.GetAsync(ApiRoutes.Auth.Me)).StatusCode.Should().Be(HttpStatusCode.OK);

        // Simulate an admin action that bumps the stamp (role change / reset).
        await using (var conn = await fx.OpenDbAsync())
        await using (var cmd = new MySqlConnector.MySqlCommand(
            "UPDATE users SET security_stamp = UUID() WHERE id = @id", conn))
        {
            cmd.Parameters.AddWithValue("@id", login.User.Id);
            await cmd.ExecuteNonQueryAsync();
        }
        fx.EvictSecurityStamp(login.User.Id);   // stand-in for the 60 s cache window

        (await client.GetAsync(ApiRoutes.Auth.Me)).StatusCode.Should().Be(
            HttpStatusCode.Unauthorized,
            "revocation must not wait for token expiry (§19.3)");
    }

    // ---- Lockout -------------------------------------------------------------

    [Fact]
    public async Task Repeated_failures_lock_the_account()
    {
        // Dedicated victim so other tests are unaffected.
        await using (var conn = await fx.OpenDbAsync())
        await using (var cmd = new MySqlConnector.MySqlCommand(
            """
            INSERT INTO users (username, full_name, password_hash, security_stamp,
                               is_active, must_change_password, concurrency_stamp, created_at)
            SELECT 'it-lockout', 'Lockout', password_hash, UUID(), 1, 0, UUID(), UTC_TIMESTAMP(3)
            FROM users WHERE username = 'it-user';
            """, conn))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        var client = fx.CreateClient();
        for (var i = 0; i < 5; i++)
        {
            await client.PostAsJsonAsync(ApiRoutes.Auth.Login,
                new LoginRequest("it-lockout", "wrong-password", null));
        }

        // Correct password now → still refused, with the lockout code.
        var locked = await client.PostAsJsonAsync(ApiRoutes.Auth.Login,
            new LoginRequest("it-lockout", ApiFixture.UserPassword, null));
        locked.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await locked.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.AccountLocked);
    }

    // ---- Change password ------------------------------------------------------

    [Fact]
    public async Task Change_password_revokes_existing_sessions()
    {
        var login = await LoginAsync("it-admin", ApiFixture.AdminPassword);
        var client = fx.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", login.AccessToken);

        try
        {
            var change = await client.PostAsJsonAsync(ApiRoutes.Auth.ChangePassword,
                new ChangePasswordRequest(ApiFixture.AdminPassword, "NewAdmin@Test2!", "it-tests"));

            // The change hands back a REPLACEMENT session, because it just revoked
            // the one the caller used to make this request.
            change.StatusCode.Should().Be(HttpStatusCode.OK);
            var replacement = (await change.Content.ReadFromJsonAsync<LoginResponse>())!;
            replacement.AccessToken.Should().NotBe(login.AccessToken);

            // The pre-change refresh token must be dead.
            var refresh = await fx.CreateClient().PostAsJsonAsync(ApiRoutes.Auth.Refresh,
                new RefreshRequest(login.RefreshToken, null));
            refresh.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            // Restore for other tests, using the session the change just issued —
            // no second login needed, which is the point of returning it.
            var restore = fx.CreateClient();
            restore.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", replacement.AccessToken);
            (await restore.PostAsJsonAsync(ApiRoutes.Auth.ChangePassword,
                new ChangePasswordRequest("NewAdmin@Test2!", ApiFixture.AdminPassword, "it-tests")))
                .StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            // Guaranteed state cleanup: restore the admin password even if assertions fail
            await fx.ResetUserPasswordAsync("it-admin", ApiFixture.AdminPassword);
        }
    }

    // ---- helpers ---------------------------------------------------------------

    private async Task<LoginResponse> LoginAsync(string username, string password)
    {
        var response = await fx.CreateClient().PostAsJsonAsync(ApiRoutes.Auth.Login,
            new LoginRequest(username, password, "it-tests"));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
    }

    private async Task<HttpClient> LoginClientAsync(string username, string password)
    {
        var login = await LoginAsync(username, password);
        var client = fx.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", login.AccessToken);
        return client;
    }
}
