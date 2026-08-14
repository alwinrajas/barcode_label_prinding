using BarcodePrinter.Client.Core;
using BarcodePrinter.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BarcodePrinter.Wpf.Features.Login;

public sealed partial class LoginViewModel(ApiClient api) : ObservableObject
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    private string username = string.Empty;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string? errorMessage;

    /// <summary>True after a successful login when the server demands a
    /// password change before continuing (admin reset / first login).</summary>
    [ObservableProperty]
    private bool isChangePasswordMode;

    /// <summary>Raised when authentication is fully complete (including a
    /// forced password change) — App swaps to the shell.</summary>
    public event EventHandler? Authenticated;

    public Session? Session => api.Session;

    private bool CanLogin() => !string.IsNullOrWhiteSpace(Username) && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanLogin))]
    private async Task LoginAsync(object? passwordBox)
    {
        var password = ReadPassword(passwordBox);
        if (string.IsNullOrEmpty(password))
        {
            ErrorMessage = "Enter your password.";
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var login = await api.LoginAsync(Username.Trim(), password, CancellationToken.None);
            if (login.MustChangePassword)
            {
                IsChangePasswordMode = true;   // view swaps panels
            }
            else
            {
                Authenticated?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (ApiException ex)
        {
            ErrorMessage = ErrorCatalog.MessageFor(ex.Code);
        }
        catch (ApiUnreachableException)
        {
            ErrorMessage = "Cannot reach the server. Check your network connection and try again.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ChangePasswordAsync(object? boxes)
    {
        // boxes: [current, new, confirm] PasswordBoxes passed from the view.
        if (boxes is not object[] { Length: 3 } arr)
        {
            return;
        }
        var current = ReadPassword(arr[0]);
        var fresh = ReadPassword(arr[1]);
        var confirm = ReadPassword(arr[2]);

        if (fresh != confirm)
        {
            ErrorMessage = "The new passwords do not match.";
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            await api.ChangePasswordAsync(current, fresh, CancellationToken.None);
            Authenticated?.Invoke(this, EventArgs.Empty);
        }
        catch (ApiException ex)
        {
            ErrorMessage = ErrorCatalog.MessageFor(ex.Code);
        }
        catch (ApiUnreachableException)
        {
            ErrorMessage = "Cannot reach the server. Check your network connection and try again.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string ReadPassword(object? box) =>
        box is System.Windows.Controls.PasswordBox pb ? pb.Password : string.Empty;
}

/// <summary>Client-side message catalogue (blueprint §22.2): stable server
/// codes → user-facing text. The server never composes UI copy.</summary>
public static class ErrorCatalog
{
    public static string MessageFor(string code) => code switch
    {
        ErrorCodes.LoginFailed => "Invalid username or password.",
        ErrorCodes.AccountLocked => "This account is temporarily locked after repeated failed attempts. Try again in a few minutes.",
        ErrorCodes.CurrentPasswordIncorrect => "The current password is incorrect.",
        ErrorCodes.PasswordPolicyViolation => "The new password does not meet the password policy.",
        ErrorCodes.RefreshTokenInvalid => "Your session has expired. Please sign in again.",
        ErrorCodes.RateLimited => "Too many attempts. Please wait a moment and try again.",
        ErrorCodes.ClientUpdateRequired =>
            "This workstation is running an out-of-date version. Ask IT to install the current version before printing.",
        _ => "Something went wrong. Please try again.",
    };
}
