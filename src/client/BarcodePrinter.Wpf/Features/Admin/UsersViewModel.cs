using System.Collections.ObjectModel;
using System.Windows;
using BarcodePrinter.Client.Core;
using BarcodePrinter.Contracts;
using BarcodePrinter.Contracts.Admin;
using BarcodePrinter.Contracts.Auth;
using BarcodePrinter.Wpf.Features.Login;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BarcodePrinter.Wpf.Features.Admin;

public sealed partial class RoleCheck(long id, string code, string name) : ObservableObject
{
    public long Id { get; } = id;
    public string Code { get; } = code;
    public string Name { get; } = name;

    [ObservableProperty]
    private bool isSelected;
}

public sealed partial class UsersViewModel : ObservableObject
{
    private readonly AdminApi _api;
    private readonly Session _session;

    public UsersViewModel(AdminApi api, Session session)
    {
        _api = api;
        _session = session;
        CanAdd = session.Has(PermissionCodes.UserAdd);
        CanEdit = session.Has(PermissionCodes.UserEdit);
        CanDeactivate = session.Has(PermissionCodes.UserDeactivate);
        CanResetPassword = session.Has(PermissionCodes.UserResetPassword);
        _ = LoadAsync();
    }

    public ObservableCollection<UserSummary> Users { get; } = [];
    public ObservableCollection<RoleCheck> Roles { get; } = [];

    public bool CanAdd { get; }
    public bool CanEdit { get; }
    public bool CanDeactivate { get; }
    public bool CanResetPassword { get; }

    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string? statusMessage;
    [ObservableProperty] private string? errorMessage;
    [ObservableProperty] private UserSummary? selectedUser;

    // Editor state
    [ObservableProperty] private bool isEditorOpen;
    [ObservableProperty] private bool isNewUser;
    [ObservableProperty] private long editingId;
    [ObservableProperty] private string editUsername = "";
    [ObservableProperty] private string editFullName = "";
    [ObservableProperty] private string? editEmail;
    [ObservableProperty] private string editorTitle = "";
    private string? _concurrencyStamp;

    async partial void OnSelectedUserChanged(UserSummary? value)
    {
        if (value is not null)
        {
            await OpenEditorAsync(value.Id);
        }
    }

    private async Task LoadAsync()
    {
        await GuardAsync(async () =>
        {
            var roles = await _api.ListRolesAsync(CancellationToken.None);
            Roles.Clear();
            foreach (var role in roles)
            {
                Roles.Add(new RoleCheck(role.Id, role.Code, role.Name));
            }
            await RefreshUsersAsync();
        });
    }

    [RelayCommand]
    private async Task RefreshUsersAsync()
    {
        var users = await _api.ListUsersAsync(CancellationToken.None);
        Users.Clear();
        foreach (var user in users)
        {
            Users.Add(user);
        }
    }

    [RelayCommand]
    private void NewUser()
    {
        SelectedUser = null;
        IsNewUser = true;
        IsEditorOpen = true;
        EditorTitle = "New user";
        EditingId = 0;
        EditUsername = "";
        EditFullName = "";
        EditEmail = null;
        _concurrencyStamp = null;
        foreach (var role in Roles)
        {
            role.IsSelected = false;
        }
        ErrorMessage = null;
    }

    private async Task OpenEditorAsync(long id)
    {
        await GuardAsync(async () =>
        {
            var detail = await _api.GetUserAsync(id, CancellationToken.None);
            IsNewUser = false;
            IsEditorOpen = true;
            EditorTitle = $"Edit {detail.Username}";
            EditingId = detail.Id;
            EditUsername = detail.Username;
            EditFullName = detail.FullName;
            EditEmail = detail.Email;
            _concurrencyStamp = detail.ConcurrencyStamp;
            foreach (var role in Roles)
            {
                role.IsSelected = detail.RoleIds.Contains(role.Id);
            }
            ErrorMessage = null;
            StatusMessage = null;
        });
    }

    [RelayCommand]
    private async Task SaveAsync(object? passwordBox)
    {
        var selectedRoles = Roles.Where(r => r.IsSelected).Select(r => r.Id).ToList();
        if (selectedRoles.Count == 0)
        {
            ErrorMessage = "Select at least one role.";
            return;
        }

        await GuardAsync(async () =>
        {
            if (IsNewUser)
            {
                var password = passwordBox is System.Windows.Controls.PasswordBox box ? box.Password : "";
                var id = await _api.CreateUserAsync(new CreateUserRequest(
                    EditUsername.Trim(), EditFullName.Trim(), EditEmail, password, selectedRoles),
                    CancellationToken.None);
                if (passwordBox is System.Windows.Controls.PasswordBox clear)
                {
                    clear.Clear();
                }
                StatusMessage = $"User created. They must change this password at first sign-in.";
                await RefreshUsersAsync();
                await OpenEditorAsync(id);
            }
            else
            {
                await _api.UpdateUserAsync(EditingId, new UpdateUserRequest(
                    EditFullName.Trim(), EditEmail, selectedRoles, _concurrencyStamp!),
                    CancellationToken.None);
                StatusMessage = "Saved.";
                await RefreshUsersAsync();
                await OpenEditorAsync(EditingId);
            }
        });
    }

    [RelayCommand]
    private async Task ToggleActiveAsync()
    {
        if (SelectedUser is null)
        {
            return;
        }
        var target = !SelectedUser.IsActive;
        var verb = target ? "Activate" : "Deactivate";
        if (MessageBox.Show($"{verb} user {SelectedUser.Username}?", "Confirm",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        var id = SelectedUser.Id;
        await GuardAsync(async () =>
        {
            await _api.SetUserActiveAsync(id, target, CancellationToken.None);
            StatusMessage = target ? "User activated." : "User deactivated — their sessions were ended.";
            await RefreshUsersAsync();
        });
    }

    [RelayCommand]
    private async Task ResetPasswordAsync(object? passwordBox)
    {
        if (EditingId == 0 || passwordBox is not System.Windows.Controls.PasswordBox box)
        {
            return;
        }
        if (box.Password.Length == 0)
        {
            ErrorMessage = "Enter the new password.";
            return;
        }
        if (MessageBox.Show($"Reset the password for {EditUsername}? Their sessions will end and they must set a new password at next sign-in.",
                "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await GuardAsync(async () =>
        {
            await _api.ResetPasswordAsync(EditingId, box.Password, CancellationToken.None);
            box.Clear();
            StatusMessage = "Password reset.";
        });
    }

    private async Task GuardAsync(Func<Task> action)
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            await action();
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Code == ErrorCodes.ConcurrencyConflict
                ? "This user was changed by someone else. Reopen the record to see their changes."
                : ex.Message;
        }
        catch (ApiUnreachableException)
        {
            ErrorMessage = "Cannot reach the server. Check your network connection.";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
