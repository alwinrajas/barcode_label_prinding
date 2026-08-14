namespace BarcodePrinter.Contracts;

/// <summary>
/// Stable machine-readable error codes (blueprint §22.2). The server sends the
/// code inside ProblemDetails; the client maps it to localized user-facing
/// text via its ErrorCatalog. The server never composes UI text.
/// </summary>
public static class ErrorCodes
{
    // Auth. LOGIN_FAILED is deliberately uniform for unknown-user and
    // wrong-password (no account enumeration, §19.3).
    public const string LoginFailed = "LOGIN_FAILED";
    public const string AccountLocked = "ACCOUNT_LOCKED";
    public const string AccountInactive = "ACCOUNT_INACTIVE";
    public const string PasswordChangeRequired = "PASSWORD_CHANGE_REQUIRED";
    public const string RefreshTokenInvalid = "REFRESH_TOKEN_INVALID";
    public const string PasswordPolicyViolation = "PASSWORD_POLICY_VIOLATION";
    public const string CurrentPasswordIncorrect = "CURRENT_PASSWORD_INCORRECT";

    // Cross-cutting
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string NotFound = "NOT_FOUND";
    public const string ConcurrencyConflict = "CONCURRENCY_CONFLICT";
    public const string RateLimited = "RATE_LIMITED";
    public const string Unexpected = "UNEXPECTED";

    // Printing (used from phase 5/6)
    public const string PrinterUnreachable = "PRINTER_UNREACHABLE";
    public const string PrinterUsbFault = "PRINTER_USB_FAULT";
    public const string ClientLost = "CLIENT_LOST";
    public const string PrintTimeout = "PRINT_TIMEOUT";

    // Import (used from phase 4)
    public const string ImportRowLimitExceeded = "IMPORT_ROW_LIMIT_EXCEEDED";
    public const string ProductCodeDuplicate = "PRODUCT_CODE_DUPLICATE";

    /// <summary>The client build is older than the server's minimum. This is
    /// what makes sharing the Contracts assembly between tiers safe: a breaking
    /// change raises MinimumClientVersion and stale clients stop rather than
    /// misinterpret a payload (§16).</summary>
    public const string ClientUpdateRequired = "CLIENT_UPDATE_REQUIRED";
}
