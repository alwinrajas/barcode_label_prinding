using System.Collections.ObjectModel;
using BarcodePrinter.Client.Core;
using BarcodePrinter.Contracts;
using BarcodePrinter.Contracts.Admin;
using BarcodePrinter.Wpf.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BarcodePrinter.Wpf.Features.Admin;

/// <summary>One permission cell in the matrix.</summary>
public sealed partial class PermissionCheck(PermissionDto permission) : ObservableObject
{
    public long Id { get; } = permission.Id;
    public string Code { get; } = permission.Code;
    public string Action { get; } = permission.Action;
    public string DisplayName { get; } = permission.DisplayName;

    [ObservableProperty]
    private bool isSelected;
}

/// <summary>Permissions grouped by module — the matrix's rows.</summary>
public sealed class PermissionModule(string module, IEnumerable<PermissionCheck> permissions)
{
    public string Module { get; } = module;
    public IReadOnlyList<PermissionCheck> Permissions { get; } = permissions.ToList();
}

public sealed partial class RolesViewModel : ObservableObject
{
    private readonly AdminApi _api;
    private IReadOnlyList<PermissionDto> _allPermissions = [];

    public RolesViewModel(AdminApi api, Session session)
    {
        _api = api;
        CanManage = session.Has(PermissionCodes.RoleManage);
        _ = LoadAsync();
    }

    public ObservableCollection<RoleSummary> Roles { get; } = [];
    public ObservableCollection<PermissionModule> Modules { get; } = [];

    public bool CanManage { get; }

    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string? statusMessage;
    [ObservableProperty] private string? errorMessage;
    [ObservableProperty] private RoleSummary? selectedRole;
    [ObservableProperty] private bool isEditorOpen;
    [ObservableProperty] private bool isNewRole;
    [ObservableProperty] private bool isSystemRole;
    [ObservableProperty] private long editingId;
    [ObservableProperty] private string editCode = "";
    [ObservableProperty] private string editName = "";
    [ObservableProperty] private string? editDescription;
    [ObservableProperty] private string editorTitle = "";
    [ObservableProperty] private string? pendingChangeSummary;

    // Screen states
    [ObservableProperty] private bool isEmpty;
    [ObservableProperty] private bool loadFailed;
    [ObservableProperty] private string? loadErrorMessage;
    [ObservableProperty] private string? loadErrorReference;

    private HashSet<long> _originalPermissions = [];

    async partial void OnSelectedRoleChanged(RoleSummary? value)
    {
        if (value is not null)
        {
            await OpenRoleAsync(value.Id);
        }
    }

    private async Task LoadAsync()
    {
        await GuardAsync(async () =>
        {
            _allPermissions = await _api.ListPermissionsAsync(CancellationToken.None);
            BuildMatrix();
            await RefreshRolesAsync();
        }, isLoad: true);
    }

    [RelayCommand]
    private Task RetryLoadAsync() => LoadAsync();

    private void BuildMatrix()
    {
        Modules.Clear();
        foreach (var group in _allPermissions.GroupBy(p => p.Module).OrderBy(g => g.First().SortOrder))
        {
            Modules.Add(new PermissionModule(group.Key,
                group.OrderBy(p => p.SortOrder).Select(p =>
                {
                    var check = new PermissionCheck(p);
                    check.PropertyChanged += (_, e) =>
                    {
                        if (e.PropertyName == nameof(PermissionCheck.IsSelected))
                        {
                            UpdateChangeSummary();
                        }
                    };
                    return check;
                })));
        }
    }

    [RelayCommand]
    private async Task RefreshRolesAsync()
    {
        var roles = await _api.ListRolesAsync(CancellationToken.None);
        Roles.Clear();
        foreach (var role in roles)
        {
            Roles.Add(role);
        }
        IsEmpty = !LoadFailed && Roles.Count == 0;
    }

    [RelayCommand]
    private void NewRole()
    {
        SelectedRole = null;
        IsNewRole = true;
        IsSystemRole = false;
        IsEditorOpen = true;
        EditorTitle = "New role";
        EditingId = 0;
        EditCode = "";
        EditName = "";
        EditDescription = null;
        SetPermissions([]);
        ErrorMessage = null;
        StatusMessage = null;
    }

    private async Task OpenRoleAsync(long id)
    {
        await GuardAsync(async () =>
        {
            var detail = await _api.GetRoleAsync(id, CancellationToken.None);
            IsNewRole = false;
            IsSystemRole = detail.IsSystem;
            IsEditorOpen = true;
            EditorTitle = $"Edit {detail.Name}";
            EditingId = detail.Id;
            EditCode = detail.Code;
            EditName = detail.Name;
            EditDescription = detail.Description;
            SetPermissions(detail.PermissionIds);
            ErrorMessage = null;
            StatusMessage = null;
        });
    }

    private void SetPermissions(IReadOnlyList<long> ids)
    {
        _originalPermissions = [.. ids];
        foreach (var permission in Modules.SelectMany(m => m.Permissions))
        {
            permission.IsSelected = _originalPermissions.Contains(permission.Id);
        }
        UpdateChangeSummary();
    }

    /// <summary>Diff-before-save: an admin editing an RBAC matrix must see
    /// exactly what they are about to change.</summary>
    private void UpdateChangeSummary()
    {
        var selected = Modules.SelectMany(m => m.Permissions).Where(p => p.IsSelected)
            .Select(p => p.Id).ToHashSet();
        var added = selected.Except(_originalPermissions).Count();
        var removed = _originalPermissions.Except(selected).Count();

        PendingChangeSummary = (added, removed) switch
        {
            (0, 0) => null,
            (> 0, 0) => $"{added} permission(s) will be granted.",
            (0, > 0) => $"{removed} permission(s) will be revoked.",
            _ => $"{added} granted, {removed} revoked.",
        };
    }

    /// <summary>Drawer/editor close with a discard confirm when the matrix has
    /// pending edits.</summary>
    [RelayCommand]
    private async Task CloseEditorAsync()
    {
        if (!IsEditorOpen)
        {
            return;
        }
        if (PendingChangeSummary is not null && !await DialogService.ConfirmAsync(
                "Discard changes?", "You have unsaved permission changes. Close the editor without saving?",
                "Discard", danger: true))
        {
            return;
        }
        IsEditorOpen = false;
        SelectedRole = null;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var permissionIds = Modules.SelectMany(m => m.Permissions)
            .Where(p => p.IsSelected).Select(p => p.Id).ToList();

        // Changing an existing role signs its holders out — say so before it
        // happens, not after.
        if (!IsNewRole && PendingChangeSummary is not null && !await DialogService.ConfirmAsync(
                "Confirm permission change",
                $"{PendingChangeSummary}\n\nEveryone with this role will be signed out and must sign in again.",
                "Apply changes"))
        {
            return;
        }

        await GuardAsync(async () =>
        {
            var request = new SaveRoleRequest(
                EditCode.Trim(), EditName.Trim(), EditDescription, permissionIds);
            if (IsNewRole)
            {
                var id = await _api.CreateRoleAsync(request, CancellationToken.None);
                await RefreshRolesAsync();
                await OpenRoleAsync(id);
                ToastService.Instance.Success("Role created.");
            }
            else
            {
                await _api.UpdateRoleAsync(EditingId, request, CancellationToken.None);
                await RefreshRolesAsync();
                await OpenRoleAsync(EditingId);
                ToastService.Instance.Success("Role saved. Its holders must sign in again.");
            }
            StatusMessage = "Saved.";
        });
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (EditingId == 0 || IsSystemRole)
        {
            return;
        }
        if (!await DialogService.ConfirmAsync("Delete role",
                $"Delete the role {EditName}? Users holding it lose these permissions.",
                "Delete", danger: true))
        {
            return;
        }

        await GuardAsync(async () =>
        {
            await _api.DeleteRoleAsync(EditingId, CancellationToken.None);
            IsEditorOpen = false;
            StatusMessage = "Role deleted.";
            ToastService.Instance.Success("Role deleted.");
            await RefreshRolesAsync();
        });
    }

    [RelayCommand]
    private void SelectAllInModule(PermissionModule? module)
    {
        if (module is null)
        {
            return;
        }
        var target = !module.Permissions.All(p => p.IsSelected);
        foreach (var permission in module.Permissions)
        {
            permission.IsSelected = target;
        }
    }

    private async Task GuardAsync(Func<Task> action, bool isLoad = false)
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            await action();
            if (isLoad)
            {
                LoadFailed = false;
                LoadErrorMessage = null;
                LoadErrorReference = null;
            }
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
            ToastService.Instance.Error(ex.Message, ex.CorrelationId);
            if (isLoad)
            {
                SetLoadFailed(ex.Message, ex.CorrelationId);
            }
        }
        catch (ApiUnreachableException)
        {
            const string message = "Cannot reach the server. Check your network connection.";
            ErrorMessage = message;
            ToastService.Instance.Error(message);
            if (isLoad)
            {
                SetLoadFailed(message, null);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SetLoadFailed(string message, string? reference)
    {
        LoadFailed = true;
        LoadErrorMessage = message;
        LoadErrorReference = reference;
        IsEmpty = false;
    }
}
