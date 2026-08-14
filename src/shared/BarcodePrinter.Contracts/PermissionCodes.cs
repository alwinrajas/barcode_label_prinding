namespace BarcodePrinter.Contracts;

/// <summary>
/// The complete permission vocabulary (blueprint §13/§19.1).
/// These strings are seeded into the `permissions` table by the DbMigrator and
/// carried as JWT claims; the two must never diverge, which is why the seed
/// script and this class are generated from the same list.
/// </summary>
public static class PermissionCodes
{
    // Product master
    public const string ProductView = "Product.View";
    public const string ProductAdd = "Product.Add";
    public const string ProductEdit = "Product.Edit";
    public const string ProductDelete = "Product.Delete";
    public const string ProductImport = "Product.Import";
    public const string ProductExport = "Product.Export";

    // Printing
    public const string PrintView = "Print.View";
    public const string PrintExecute = "Print.Execute";
    public const string PrintReprint = "Print.Reprint";   // seeded distinct from day one (A-22)
    public const string PrintCancel = "Print.Cancel";

    // Print history
    public const string HistoryView = "History.View";
    public const string HistoryExport = "History.Export";

    // Reports
    public const string ReportView = "Report.View";
    public const string ReportExport = "Report.Export";
    public const string ReportPrint = "Report.Print";

    // User management
    public const string UserView = "User.View";
    public const string UserAdd = "User.Add";
    public const string UserEdit = "User.Edit";
    public const string UserDeactivate = "User.Deactivate";
    public const string UserResetPassword = "User.ResetPassword";

    // Roles
    public const string RoleView = "Role.View";
    public const string RoleManage = "Role.Manage";

    // Settings
    public const string SettingsView = "Settings.View";
    public const string SettingsManage = "Settings.Manage";
    public const string SettingsManagePrinters = "Settings.ManagePrinters";
    public const string SettingsManageTemplates = "Settings.ManageTemplates";
    public const string SettingsManageIntegration = "Settings.ManageIntegration";

    // Audit
    public const string AuditView = "Audit.View";
    public const string AuditExport = "Audit.Export";

    // Dashboard
    public const string DashboardView = "Dashboard.View";

    /// <summary>All permission codes, for validation and completeness tests.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        ProductView, ProductAdd, ProductEdit, ProductDelete, ProductImport, ProductExport,
        PrintView, PrintExecute, PrintReprint, PrintCancel,
        HistoryView, HistoryExport,
        ReportView, ReportExport, ReportPrint,
        UserView, UserAdd, UserEdit, UserDeactivate, UserResetPassword,
        RoleView, RoleManage,
        SettingsView, SettingsManage, SettingsManagePrinters, SettingsManageTemplates, SettingsManageIntegration,
        AuditView, AuditExport,
        DashboardView,
    ];
}
