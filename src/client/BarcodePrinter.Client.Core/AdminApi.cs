using BarcodePrinter.Contracts;
using BarcodePrinter.Contracts.Admin;
using BarcodePrinter.Contracts.Auth;
using BarcodePrinter.Contracts.Products;

namespace BarcodePrinter.Client.Core;

public sealed class AdminApi(ApiClient api)
{
    // Users
    public Task<IReadOnlyList<UserSummary>> ListUsersAsync(CancellationToken ct) =>
        api.GetAsync<IReadOnlyList<UserSummary>>(ApiRoutes.Users.List, ct);

    public Task<UserDetail> GetUserAsync(long id, CancellationToken ct) =>
        api.GetAsync<UserDetail>(ApiRoutes.Users.ById(id), ct);

    public async Task<long> CreateUserAsync(CreateUserRequest request, CancellationToken ct) =>
        (await api.PostAsync<CreateUserRequest, UserCreatedResponse>(
            ApiRoutes.Users.Base, request, ct)).Id;

    public Task UpdateUserAsync(long id, UpdateUserRequest request, CancellationToken ct) =>
        api.PutAsync(ApiRoutes.Users.ById(id), request, ct);

    public Task SetUserActiveAsync(long id, bool active, CancellationToken ct) =>
        api.PostAsync($"{ApiRoutes.Users.Activate(id)}?active={active.ToString().ToLowerInvariant()}", ct);

    public Task ResetPasswordAsync(long id, string password, CancellationToken ct) =>
        api.PostAsync<ResetPasswordRequest, object>(
            ApiRoutes.Users.ResetPassword(id), new ResetPasswordRequest(password), ct);

    // Roles
    public Task<IReadOnlyList<RoleSummary>> ListRolesAsync(CancellationToken ct) =>
        api.GetAsync<IReadOnlyList<RoleSummary>>(ApiRoutes.Roles.Base, ct);

    public Task<RoleDetail> GetRoleAsync(long id, CancellationToken ct) =>
        api.GetAsync<RoleDetail>(ApiRoutes.Roles.ById(id), ct);

    public Task<IReadOnlyList<PermissionDto>> ListPermissionsAsync(CancellationToken ct) =>
        api.GetAsync<IReadOnlyList<PermissionDto>>(ApiRoutes.Roles.Permissions, ct);

    public Task<long> CreateRoleAsync(SaveRoleRequest request, CancellationToken ct) =>
        api.PostAsync<SaveRoleRequest, IdResponse>(ApiRoutes.Roles.Base, request, ct)
            .ContinueWith(t => t.Result.Id, ct, TaskContinuationOptions.OnlyOnRanToCompletion,
                TaskScheduler.Default);

    public Task UpdateRoleAsync(long id, SaveRoleRequest request, CancellationToken ct) =>
        api.PutAsync(ApiRoutes.Roles.ById(id), request, ct);

    public Task DeleteRoleAsync(long id, CancellationToken ct) =>
        api.DeleteAsync(ApiRoutes.Roles.ById(id), ct);

    // Settings
    public Task<IReadOnlyList<SettingDto>> ListSettingsAsync(CancellationToken ct) =>
        api.GetAsync<IReadOnlyList<SettingDto>>(ApiRoutes.Settings.Base, ct);

    public Task SaveSettingsAsync(IReadOnlyDictionary<string, string?> values, CancellationToken ct) =>
        api.PutAsync(ApiRoutes.Settings.Base, new SaveSettingsRequest(values), ct);

    // Audit
    public Task<PagedResult<AuditEntryDto>> QueryAuditAsync(
        DateTime? from, DateTime? to, string? action, string? severity,
        string? cursor, int pageSize, CancellationToken ct)
    {
        var query = new List<string> { $"pageSize={pageSize}" };
        if (from is not null) query.Add($"from={Uri.EscapeDataString(from.Value.ToString("O"))}");
        if (to is not null) query.Add($"to={Uri.EscapeDataString(to.Value.ToString("O"))}");
        if (!string.IsNullOrWhiteSpace(action)) query.Add($"action={Uri.EscapeDataString(action)}");
        if (!string.IsNullOrWhiteSpace(severity)) query.Add($"severity={severity}");
        if (!string.IsNullOrWhiteSpace(cursor)) query.Add($"cursor={cursor}");
        return api.GetAsync<PagedResult<AuditEntryDto>>(
            $"{ApiRoutes.Audit.Base}?{string.Join("&", query)}", ct);
    }

    public Task<IReadOnlyList<string>> ListAuditActionsAsync(CancellationToken ct) =>
        api.GetAsync<IReadOnlyList<string>>(ApiRoutes.Audit.Actions, ct);

    private sealed record IdResponse(long Id);
}
