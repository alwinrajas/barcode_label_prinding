using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using BarcodePrinter.Client.Core;
using BarcodePrinter.Contracts;
using BarcodePrinter.Contracts.Admin;
using BarcodePrinter.Contracts.Auth;
using BarcodePrinter.Wpf.Features.Login;
using BarcodePrinter.Wpf.Services;
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
    /// <summary>Edit-form properties that flip the drawer's dirty flag.</summary>
    private static readonly HashSet<string> DirtyProps =
        [nameof(EditUsername), nameof(EditFullName), nameof(EditEmail)];

    private readonly AdminApi _api;
    private readonly Session _session;
    private readonly ICollectionView _usersView;
    private bool _suppressDirty;

    public UsersViewModel(AdminApi api, Session session)
    {
        _api = api;
        _session = session;
        CanAdd = session.Has(PermissionCodes.UserAdd);
        CanEdit = session.Has(PermissionCodes.UserEdit);
        CanDeactivate = session.Has(PermissionCodes.UserDeactivate);
        CanResetPassword = session.Has(PermissionCodes.UserResetPassword);
        _usersView = CollectionViewSource.GetDefaultView(Users);
        _usersView.Filter = MatchesSearch;
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

    // Screen states
    [ObservableProperty] private bool isEmpty;
    [ObservableProperty] private bool loadFailed;
    [ObservableProperty] private string? loadErrorMessage;
    [ObservableProperty] private string? loadErrorReference;

    // Client-side search over the fully loaded list (the API returns all users).
    [ObservableProperty] private string searchText = "";

    // Editor state
    [ObservableProperty] private bool isEditorOpen;
    [ObservableProperty] private bool isNewUser;
    [ObservableProperty] private bool isDirty;
    [ObservableProperty] private long editingId;
    [ObservableProperty] private string editUsername = "";
    [ObservableProperty] private string editFullName = "";
    [ObservableProperty] private string? editEmail;
    [ObservableProperty] private string editorTitle = "";
    private string? _concurrencyStamp;

    partial void OnSearchTextChanged(string value)
    {
        _usersView.Refresh();
        IsEmpty = !LoadFailed && _usersView.IsEmpty;
    }

    private bool MatchesSearch(object item)
    {
        if (string.IsNullOrWhiteSpace(SearchText) || item is not UserSummary user)
        {
            return true;
        }
        var term = SearchText.Trim();
        return user.Username.Contains(term, StringComparison.OrdinalIgnoreCase)
            || user.FullName.Contains(term, StringComparison.OrdinalIgnoreCase)
            || (user.Email?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
            || user.Roles.Any(r => r.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_suppressDirty && IsEditorOpen && e.PropertyName is not null && DirtyProps.Contains(e.PropertyName))
        {
            IsDirty = true;
        }
    }

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
                var check = new RoleCheck(role.Id, role.Code, role.Name);
                check.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(RoleCheck.IsSelected) && !_suppressDirty && IsEditorOpen)
                    {
                        IsDirty = true;
                    }
                };
                Roles.Add(check);
            }
            await RefreshUsersAsync();
        }, isLoad: true);
    }

    [RelayCommand]
    private Task RetryLoadAsync() => LoadAsync();

    [RelayCommand]
    private async Task RefreshUsersAsync()
    {
        var users = await _api.ListUsersAsync(CancellationToken.None);
        Users.Clear();
        foreach (var user in users)
        {
            Users.Add(user);
        }
        IsEmpty = _usersView.IsEmpty;
    }

    [RelayCommand]
    private void NewUser()
    {
        _suppressDirty = true;
        try
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
            StatusMessage = null;
        }
        finally
        {
            _suppressDirty = false;
        }
        IsDirty = false;
    }

    private async Task OpenEditorAsync(long id)
    {
        await GuardAsync(async () =>
        {
            var detail = await _api.GetUserAsync(id, CancellationToken.None);
            _suppressDirty = true;
            try
            {
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
            }
            finally
            {
                _suppressDirty = false;
            }
            IsDirty = false;
        });
    }

    /// <summary>Drawer close (X / Cancel / Escape) with a discard confirm when
    /// there are unsaved edits.</summary>
    [RelayCommand]
    private async Task CloseEditorAsync()
    {
        if (!IsEditorOpen)
        {
            return;
        }
        if (IsDirty && !await DialogService.ConfirmAsync(
                "Discard changes?", "You have unsaved changes. Close the editor without saving?",
                "Discard", danger: true))
        {
            return;
        }
        IsEditorOpen = false;
        IsDirty = false;
        SelectedUser = null;
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
                ToastService.Instance.Success(
                    "User created. They must change this password at first sign-in.");
                await RefreshUsersAsync();
                await OpenEditorAsync(id);
            }
            else
            {
                await _api.UpdateUserAsync(EditingId, new UpdateUserRequest(
                    EditFullName.Trim(), EditEmail, selectedRoles, _concurrencyStamp!),
                    CancellationToken.None);
                ToastService.Instance.Success("User saved.");
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
        var message = target
            ? $"Activate user {SelectedUser.Username}?"
            : $"Deactivate user {SelectedUser.Username}? Their sessions will be ended.";
        if (!await DialogService.ConfirmAsync($"{verb} user", message, verb, danger: !target))
        {
            return;
        }

        var id = SelectedUser.Id;
        await GuardAsync(async () =>
        {
            await _api.SetUserActiveAsync(id, target, CancellationToken.None);
            ToastService.Instance.Success(target
                ? "User activated."
                : "User deactivated — their sessions were ended.");
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
        if (!await DialogService.ConfirmAsync("Reset password",
                $"Reset the password for {EditUsername}? Their sessions will end and they must set a new password at next sign-in.",
                "Reset", danger: true))
        {
            return;
        }

        await GuardAsync(async () =>
        {
            await _api.ResetPasswordAsync(EditingId, box.Password, CancellationToken.None);
            box.Clear();
            ToastService.Instance.Success(
                "Password reset. The user must set a new password at next sign-in.");
        });
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
            var message = ex.Code == ErrorCodes.ConcurrencyConflict
                ? "This user was changed by someone else. Reopen the record to see their changes."
                : ex.Message;
            ToastService.Instance.Error(message, ex.CorrelationId);
            if (isLoad)
            {
                SetLoadFailed(message, ex.CorrelationId);
            }
        }
        catch (ApiUnreachableException)
        {
            const string message = "Cannot reach the server. Check your network connection.";
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
