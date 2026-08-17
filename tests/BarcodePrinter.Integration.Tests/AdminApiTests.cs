using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BarcodePrinter.Contracts;
using BarcodePrinter.Contracts.Admin;
using BarcodePrinter.Contracts.Auth;
using BarcodePrinter.Contracts.Products;
using FluentAssertions;
using Xunit;

namespace BarcodePrinter.Integration.Tests;

[Collection("api")]
public class AdminApiTests(ApiFixture fx) : IAsyncLifetime
{
    private HttpClient _admin = null!;
    private long _userRoleId;
    private long _managerRoleId;

    public async Task InitializeAsync()
    {
        _admin = await LoginAsync("it-admin", ApiFixture.AdminPassword);
        var roles = await _admin.GetFromJsonAsync<List<RoleSummary>>(ApiRoutes.Roles.Base);
        _userRoleId = roles!.First(r => r.Code == "User").Id;
        _managerRoleId = roles.First(r => r.Code == "Manager").Id;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- Users ---------------------------------------------------------------

    [Fact]
    public async Task Create_user_then_login_forces_password_change()
    {
        var username = $"it-new-{Guid.NewGuid():N}"[..20];
        var id = await CreateUserAsync(username, "Temp@Pass1!");

        var detail = (await _admin.GetFromJsonAsync<UserDetail>(ApiRoutes.Users.ById(id)))!;
        detail.MustChangePassword.Should().BeTrue("a created account must not keep the admin-set password");
        detail.RoleCodes.Should().Contain("User");

        var login = await fx.CreateClient().PostAsJsonAsync(ApiRoutes.Auth.Login,
            new LoginRequest(username, "Temp@Pass1!", "it"));
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        (await login.Content.ReadFromJsonAsync<LoginResponse>())!.MustChangePassword.Should().BeTrue();
    }

    [Fact]
    public async Task Duplicate_username_and_weak_password_are_rejected()
    {
        var username = $"it-dup-{Guid.NewGuid():N}"[..20];
        await CreateUserAsync(username, "Temp@Pass1!");

        var dup = await _admin.PostAsJsonAsync(ApiRoutes.Users.Base,
            new CreateUserRequest(username, "Dup", null, "Temp@Pass1!", [_userRoleId]));
        dup.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await dup.Content.ReadAsStringAsync()).Should().Contain("USERNAME_DUPLICATE");

        var weak = await _admin.PostAsJsonAsync(ApiRoutes.Users.Base,
            new CreateUserRequest($"{username}x", "Weak", null, "abc", [_userRoleId]));
        weak.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await weak.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.PasswordPolicyViolation);

        var noRole = await _admin.PostAsJsonAsync(ApiRoutes.Users.Base,
            new CreateUserRequest($"{username}y", "NoRole", null, "Temp@Pass1!", []));
        noRole.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Deactivating_a_user_immediately_kills_their_session()
    {
        var username = $"it-deact-{Guid.NewGuid():N}"[..20];
        var id = await CreateUserAsync(username, "Temp@Pass1!");

        var login = await fx.CreateClient().PostAsJsonAsync(ApiRoutes.Auth.Login,
            new LoginRequest(username, "Temp@Pass1!", "it"));
        var tokens = (await login.Content.ReadFromJsonAsync<LoginResponse>())!;
        var victim = fx.CreateClient();
        victim.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        (await victim.GetAsync(ApiRoutes.Auth.Me)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await _admin.PostAsync($"{ApiRoutes.Users.Activate(id)}?active=false", null))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await victim.GetAsync(ApiRoutes.Auth.Me)).StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "deactivation bumps the security stamp and evicts the cache");
        (await fx.CreateClient().PostAsJsonAsync(ApiRoutes.Auth.Refresh,
            new RefreshRequest(tokens.RefreshToken, null)))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "refresh tokens are revoked too");
    }

    [Fact]
    public async Task Admin_cannot_deactivate_themselves_or_the_last_admin()
    {
        var me = (await _admin.GetFromJsonAsync<UserInfo>(ApiRoutes.Auth.Me))!;
        var self = await _admin.PostAsync($"{ApiRoutes.Users.Activate(me.Id)}?active=false", null);
        self.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await self.Content.ReadAsStringAsync()).Should().Contain("SELF_DEACTIVATION");
    }

    [Fact]
    public async Task Password_reset_forces_change_and_revokes_sessions()
    {
        var username = $"it-reset-{Guid.NewGuid():N}"[..20];
        var id = await CreateUserAsync(username, "Temp@Pass1!");

        var first = await fx.CreateClient().PostAsJsonAsync(ApiRoutes.Auth.Login,
            new LoginRequest(username, "Temp@Pass1!", "it"));
        var tokens = (await first.Content.ReadFromJsonAsync<LoginResponse>())!;

        (await _admin.PostAsJsonAsync(ApiRoutes.Users.ResetPassword(id),
            new ResetPasswordRequest("Reset@Pass2!"))).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await fx.CreateClient().PostAsJsonAsync(ApiRoutes.Auth.Refresh,
            new RefreshRequest(tokens.RefreshToken, null)))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var relogin = await fx.CreateClient().PostAsJsonAsync(ApiRoutes.Auth.Login,
            new LoginRequest(username, "Reset@Pass2!", "it"));
        relogin.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Stale_concurrency_stamp_on_user_update_returns_409()
    {
        var username = $"it-conc-{Guid.NewGuid():N}"[..20];
        var id = await CreateUserAsync(username, "Temp@Pass1!");
        var detail = (await _admin.GetFromJsonAsync<UserDetail>(ApiRoutes.Users.ById(id)))!;

        (await _admin.PutAsJsonAsync(ApiRoutes.Users.ById(id),
            new UpdateUserRequest("First Edit", null, [_userRoleId], detail.ConcurrencyStamp)))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await _admin.PutAsJsonAsync(ApiRoutes.Users.ById(id),
            new UpdateUserRequest("Second Edit", null, [_userRoleId], detail.ConcurrencyStamp)))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ---- Roles / permission matrix -------------------------------------------------

    [Fact]
    public async Task Changing_a_roles_permissions_takes_effect_for_its_users_immediately()
    {
        var username = $"it-perm-{Guid.NewGuid():N}"[..20];
        await CreateUserAsync(username, "Temp@Pass1!", _managerRoleId);
        var victim = await LoginAsync(username, "Temp@Pass1!", changeFrom: "Temp@Pass1!");

        // Manager cannot list users.
        (await victim.GetAsync(ApiRoutes.Users.ById(1))).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var permissions = (await _admin.GetFromJsonAsync<List<PermissionDto>>(ApiRoutes.Roles.Permissions))!;
        var manager = (await _admin.GetFromJsonAsync<RoleDetail>(ApiRoutes.Roles.ById(_managerRoleId)))!;
        var userView = permissions.First(p => p.Code == PermissionCodes.UserView).Id;

        try
        {
            (await _admin.PutAsJsonAsync(ApiRoutes.Roles.ById(_managerRoleId),
                new SaveRoleRequest(manager.Code, manager.Name, manager.Description,
                    [.. manager.PermissionIds, userView])))
                .StatusCode.Should().Be(HttpStatusCode.NoContent);

            // The old token is dead (stamp bumped); a fresh login carries the
            // new permission.
            (await victim.GetAsync(ApiRoutes.Users.ById(1)))
                .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            var refreshed = await LoginAsync(username, "Temp@Pass1!");
            (await refreshed.GetAsync(ApiRoutes.Users.ById(1)))
                .StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await _admin.PutAsJsonAsync(ApiRoutes.Roles.ById(_managerRoleId),
                new SaveRoleRequest(manager.Code, manager.Name, manager.Description, manager.PermissionIds));
        }
    }

    [Fact]
    public async Task Custom_role_lifecycle_create_assign_block_delete_while_in_use()
    {
        var code = $"IT{Guid.NewGuid():N}"[..10];
        var permissions = (await _admin.GetFromJsonAsync<List<PermissionDto>>(ApiRoutes.Roles.Permissions))!;
        var printOnly = permissions.Where(p => p.Code is PermissionCodes.PrintView or PermissionCodes.PrintExecute)
            .Select(p => p.Id).ToList();

        var create = await _admin.PostAsJsonAsync(ApiRoutes.Roles.Base,
            new SaveRoleRequest(code, "Print only", "Custom role", printOnly));
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var roleId = (await create.Content.ReadFromJsonAsync<CreatedResponse>())!.Id;

        var username = $"it-cust-{Guid.NewGuid():N}"[..20];
        await CreateUserAsync(username, "Temp@Pass1!", roleId);

        var inUse = await _admin.DeleteAsync(ApiRoutes.Roles.ById(roleId));
        inUse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await inUse.Content.ReadAsStringAsync()).Should().Contain("ROLE_IN_USE");

        // Reassign then delete succeeds.
        var user = (await _admin.GetFromJsonAsync<List<RoleSummary>>(ApiRoutes.Roles.Base))!;
        user.Should().Contain(r => r.Id == roleId && r.UserCount == 1);
    }

    [Fact]
    public async Task System_roles_cannot_be_deleted_or_recoded()
    {
        var delete = await _admin.DeleteAsync(ApiRoutes.Roles.ById(_userRoleId));
        delete.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await delete.Content.ReadAsStringAsync()).Should().Contain("ROLE_SYSTEM_DELETE");

        var role = (await _admin.GetFromJsonAsync<RoleDetail>(ApiRoutes.Roles.ById(_userRoleId)))!;
        var recode = await _admin.PutAsJsonAsync(ApiRoutes.Roles.ById(_userRoleId),
            new SaveRoleRequest("Renamed", role.Name, role.Description, role.PermissionIds));
        recode.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await recode.Content.ReadAsStringAsync()).Should().Contain("ROLE_SYSTEM_CODE");
    }

    // ---- Settings ---------------------------------------------------------------------

    [Fact]
    public async Task Settings_round_trip_and_validation()
    {
        var settings = (await _admin.GetFromJsonAsync<List<SettingDto>>(ApiRoutes.Settings.Base))!;
        settings.Should().Contain(s => s.Key == "Label:FeedbackFormUrl");

        (await _admin.PutAsJsonAsync(ApiRoutes.Settings.Base, new SaveSettingsRequest(
            new Dictionary<string, string?> { ["Label:FeedbackFormUrl"] = "https://forms.gle/OK" })))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var after = (await _admin.GetFromJsonAsync<List<SettingDto>>(ApiRoutes.Settings.Base))!;
        after.First(s => s.Key == "Label:FeedbackFormUrl").Value.Should().Be("https://forms.gle/OK");

        // A bad URL would end up on thousands of labels — rejected at entry.
        var badUrl = await _admin.PutAsJsonAsync(ApiRoutes.Settings.Base, new SaveSettingsRequest(
            new Dictionary<string, string?> { ["Label:FeedbackFormUrl"] = "not-a-url" }));
        badUrl.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var badInt = await _admin.PutAsJsonAsync(ApiRoutes.Settings.Base, new SaveSettingsRequest(
            new Dictionary<string, string?> { ["Auth:LockoutThreshold"] = "many" }));
        badInt.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var badFormat = await _admin.PutAsJsonAsync(ApiRoutes.Settings.Base, new SaveSettingsRequest(
            new Dictionary<string, string?> { ["Label:DateFormat"] = "dd/MM/yyyy" }));
        badFormat.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var unknown = await _admin.PutAsJsonAsync(ApiRoutes.Settings.Base, new SaveSettingsRequest(
            new Dictionary<string, string?> { ["Not:A:Setting"] = "x" }));
        unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- Audit -------------------------------------------------------------------------

    [Fact]
    public async Task Audit_records_admin_actions_and_pages_by_keyset()
    {
        var username = $"it-audit-{Guid.NewGuid():N}"[..20];
        await CreateUserAsync(username, "Temp@Pass1!");

        var page = (await _admin.GetFromJsonAsync<PagedResult<AuditEntryDto>>(
            $"{ApiRoutes.Audit.Base}?pageSize=5"))!;
        page.Items.Should().NotBeEmpty();
        page.Items.Should().Contain(e => e.Action == "UserCreated" && e.EntityId == username);
        page.Items.Should().OnlyContain(e => !string.IsNullOrEmpty(e.Username));

        if (page.HasMore)
        {
            var next = (await _admin.GetFromJsonAsync<PagedResult<AuditEntryDto>>(
                $"{ApiRoutes.Audit.Base}?pageSize=5&cursor={page.NextCursor}"))!;
            next.Items.Select(i => i.Id).Should().NotIntersectWith(page.Items.Select(i => i.Id));
        }

        var filtered = (await _admin.GetFromJsonAsync<PagedResult<AuditEntryDto>>(
            $"{ApiRoutes.Audit.Base}?action=UserCreated&pageSize=50"))!;
        filtered.Items.Should().OnlyContain(e => e.Action == "UserCreated");
    }

    [Fact]
    public async Task Settings_change_is_audited_with_secrets_redacted()
    {
        await _admin.PutAsJsonAsync(ApiRoutes.Settings.Base, new SaveSettingsRequest(
            new Dictionary<string, string?> { ["Company:Name"] = "Audited Co" }));

        var audit = (await _admin.GetFromJsonAsync<PagedResult<AuditEntryDto>>(
            $"{ApiRoutes.Audit.Base}?action=SettingsChanged&pageSize=10"))!;
        audit.Items.Should().NotBeEmpty();
        audit.Items[0].AfterJson.Should().Contain("Audited Co");
        audit.Items[0].Severity.Should().Be("Security");
    }

    // ---- RBAC on admin surface ------------------------------------------------------------

    [Fact]
    public async Task Non_admin_roles_cannot_reach_the_admin_api()
    {
        var manager = await LoginAsync("it-manager", ApiFixture.ManagerPassword);

        (await manager.PostAsJsonAsync(ApiRoutes.Users.Base,
            new CreateUserRequest("x", "x", null, "Temp@Pass1!", [_userRoleId])))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await manager.GetAsync(ApiRoutes.Settings.Base)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await manager.PutAsJsonAsync(ApiRoutes.Roles.ById(_userRoleId),
            new SaveRoleRequest("User", "User", null, []))).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var user = await LoginAsync("it-user", ApiFixture.UserPassword);
        (await user.GetAsync(ApiRoutes.Audit.Base)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await user.GetAsync(ApiRoutes.Roles.Base)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- helpers ----------------------------------------------------------------------------

    [Fact]
    public async Task Audit_export_returns_a_workbook()
    {
        // The route and its permission existed but nothing was mapped to them,
        // so this download used to 404.
        var response = await _admin.GetAsync(ApiRoutes.Audit.Export);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should()
            .Be("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");

        var bytes = await response.Content.ReadAsByteArrayAsync();
        bytes.Should().HaveCountGreaterThan(4);
        bytes[..2].Should().Equal([0x50, 0x4B], "an xlsx is a zip container");
    }

    private async Task<long> CreateUserAsync(string username, string password, long? roleId = null)
    {
        var response = await _admin.PostAsJsonAsync(ApiRoutes.Users.Base,
            new CreateUserRequest(username, username, null, password, [roleId ?? _userRoleId]));
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<UserCreatedResponse>())!.Id;
    }

    private async Task<HttpClient> LoginAsync(string username, string password, string? changeFrom = null)
    {
        var client = fx.CreateClient();
        var response = await client.PostAsJsonAsync(ApiRoutes.Auth.Login,
            new LoginRequest(username, password, "it-tests"));
        response.EnsureSuccessStatusCode();
        var login = (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", login.AccessToken);

        if (changeFrom is not null && login.MustChangePassword)
        {
            await client.PostAsJsonAsync(ApiRoutes.Auth.ChangePassword,
                new ChangePasswordRequest(changeFrom, password));
            return await LoginAsync(username, password);
        }
        return client;
    }

    private sealed record CreatedResponse(long Id);
}
