using System.Collections.ObjectModel;
using BarcodePrinter.Client.Core;
using BarcodePrinter.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BarcodePrinter.Wpf.Shell;

/// <summary>One sidebar entry. Items the user's role cannot access are never
/// constructed — hidden, not disabled (blueprint §12).</summary>
public sealed record NavItem(string Key, string Title, string Glyph, string Section, string PageHint);

public sealed partial class ShellViewModel : ObservableObject
{
    private readonly Session _session;
    private readonly ConnectionStatus _connection;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SidebarTextVisibility))]
    private bool isSidebarCollapsed;

    [ObservableProperty]
    private NavItem? selectedItem;

    public System.Windows.Visibility SidebarTextVisibility =>
        IsSidebarCollapsed ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    public ObservableCollection<NavItem> NavItems { get; } = [];

    /// <summary>Observed, never assumed. A green light that is always green is
    /// worse than no light: during an outage it contradicts the screen and the
    /// operator keeps working.</summary>
    public bool IsServerOnline => _connection.IsOnline;

    public string ServerStatusText => _connection.IsOnline
        ? "Server: Connected"
        : _connection.LastSuccessLocal is { } last
            ? $"Server: Not reachable (last contact {last:HH:mm})"
            : "Server: Not reachable";

    public string UserDisplay { get; }
    public string RoleDisplay { get; }
    public string VersionDisplay { get; } =
        "v" + (typeof(ShellViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");

    public ShellViewModel(Session session, ConnectionStatus connection)
    {
        _session = session;
        _connection = connection;
        connection.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(ServerStatusText));
            OnPropertyChanged(nameof(IsServerOnline));
        };
        UserDisplay = session.User.FullName;
        RoleDisplay = string.Join(", ", session.User.Roles);

        // (key, title, MDL2 glyph, section, permission, page hint)
        (string Key, string Title, string Glyph, string Section, string Permission, string Hint)[] all =
        [
            ("dashboard", "Dashboard", "", "GENERAL", PermissionCodes.DashboardView, ""),
            ("print", "Print Labels", "", "PRINT", PermissionCodes.PrintView, ""),
            ("history", "Print History", "", "PRINT", PermissionCodes.HistoryView, ""),
            ("products", "Products", "", "DATA", PermissionCodes.ProductView, ""),
            ("import", "Excel Import", "", "DATA", PermissionCodes.ProductImport, ""),
            ("reports", "Reports", "", "INSIGHTS", PermissionCodes.ReportView, ""),
            ("users", "Users", "", "ADMIN", PermissionCodes.UserView, ""),
            ("roles", "Roles", "", "ADMIN", PermissionCodes.RoleView, ""),
            ("printers", "Printers", "", "ADMIN", PermissionCodes.SettingsManagePrinters, "Configure Zebra and Windows printers, set the default, and send a test label."),
            ("settings", "Settings", "", "ADMIN", PermissionCodes.SettingsView, ""),
            ("audit", "Audit Log", "", "ADMIN", PermissionCodes.AuditView, ""),
        ];

        foreach (var item in all)
        {
            if (_session.Has(item.Permission))
            {
                NavItems.Add(new NavItem(item.Key, item.Title, item.Glyph, item.Section, item.Hint));
            }
        }

        SelectedItem = NavItems.FirstOrDefault();
    }

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarCollapsed = !IsSidebarCollapsed;
}
