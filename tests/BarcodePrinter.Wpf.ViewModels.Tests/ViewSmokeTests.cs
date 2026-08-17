using System.Net;
using System.Windows;
using System.Windows.Controls;
using BarcodePrinter.Client.Core;
using FluentAssertions;
using Xunit;

namespace BarcodePrinter.Wpf.ViewModels.Tests;

/// <summary>
/// Instantiates every screen for real. A missing StaticResource key, a broken
/// ControlTemplate or a bad converter compiles perfectly and only explodes when
/// the user opens the page — this is the net that catches those before an
/// operator does. Bindings are exercised too: the views get live ViewModels.
/// </summary>
public sealed class ViewSmokeTests
{
    /// <summary>Everything answers with a benign empty payload, so a screen is
    /// judged on whether it can render, not on what data it received.</summary>
    private static RoutingHandler Handler()
    {
        var handler = new RoutingHandler();
        handler.Route("/api/", _ => RoutingHandler.Json(Array.Empty<object>()));
        handler.Route("/api/dashboard", new
        {
            kpis = new
            {
                labelsToday = 0, jobsToday = 0, failedToday = 0, activeProducts = 0,
                activeUsersToday = 0, queuedJobs = 0,
            },
            recentJobs = Array.Empty<object>(),
            printers = Array.Empty<object>(),
            lastSevenDays = Array.Empty<object>(),
            alerts = Array.Empty<object>(),
            partial = false,
        });
        handler.Route("/api/settings", Array.Empty<object>());
        handler.Route("/api/audit", new { items = Array.Empty<object>(), nextCursor = (string?)null, hasMore = false });
        handler.Route("/api/print/history", new { items = Array.Empty<object>(), nextCursor = (string?)null, hasMore = false });
        handler.Route("/api/products", new { items = Array.Empty<object>(), nextCursor = (string?)null, hasMore = false });
        handler.Route("/api/reports", new
        {
            title = "Report", type = "PrintLog", columns = Array.Empty<string>(),
            rows = Array.Empty<object>(), totals = (object?)null,
            nextCursor = (string?)null, hasMore = false,
        });
        return handler;
    }

    /// <summary>App.xaml's dictionaries are what the views resolve against, so
    /// the smoke test must load the same set the running application does.</summary>
    private static void EnsureApplicationResources()
    {
        if (Application.Current is null)
        {
            _ = new Application();
        }
        if (Application.Current!.Resources.MergedDictionaries.Count > 0)
        {
            return;
        }
        foreach (var path in new[]
                 {
                     "DesignSystem/Tokens.xaml", "DesignSystem/Colors.xaml",
                     "DesignSystem/Controls.xaml", "DesignSystem/ScreenStates.xaml",
                 })
        {
            Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/BarcodePrinter.Wpf;component/{path}", UriKind.Absolute),
            });
        }
        // App.xaml itself cannot be loaded here (its root IS the Application
        // class, and only one may exist per AppDomain), so its converter
        // resources are registered under the same keys the views use.
        var converters = new Dictionary<string, object>
        {
            ["BoolToVisibility"] = new BooleanToVisibilityConverter(),
            ["NullToCollapsed"] = new Wpf.NullToCollapsedConverter(),
            ["NullToVisible"] = new Wpf.NullToVisibleConverter(),
            ["InverseBool"] = new Wpf.InverseBoolConverter(),
            ["JoinList"] = new Wpf.JoinListConverter(),
            ["InverseBoolToVisibility"] = new Wpf.InverseBoolToVisibilityConverter(),
            ["CountToVisibility"] = new Wpf.CountToVisibilityConverter(),
            ["ZeroToVisibility"] = new Wpf.ZeroToVisibilityConverter(),
            ["FractionOfHeight"] = new Wpf.FractionOfHeightConverter(),
            ["Plural"] = new Wpf.PluralConverter(),
        };
        foreach (var (key, converter) in converters)
        {
            Application.Current.Resources[key] = converter;
        }
    }

    public static TheoryData<string> ScreenNames =>
    [
        "Dashboard", "Products", "Print", "PrintHistory", "Import",
        "Reports", "Users", "Roles", "Printers", "Settings", "Audit",
    ];

    private static async Task<UserControl> BuildAsync(string screen)
    {
        var handler = Handler();
        var api = await handler.LoggedInClientAsync();
        var session = TestSession.Create();

        return screen switch
        {
            "Dashboard" => new Wpf.Features.Dashboard.DashboardView(
                new Wpf.Features.Dashboard.DashboardViewModel(new DashboardApi(api))),
            "Products" => new Wpf.Features.Products.ProductsView(
                new Wpf.Features.Products.ProductsViewModel(new ProductsApi(api), session)),
            "Print" => new Wpf.Features.Printing.PrintView(
                new Wpf.Features.Printing.PrintViewModel(new PrintApi(api), new ProductsApi(api), session)),
            "PrintHistory" => new Wpf.Features.Printing.PrintHistoryView(
                new Wpf.Features.Printing.PrintHistoryViewModel(new PrintApi(api), session)),
            "Import" => new Wpf.Features.Imports.ImportView(
                new Wpf.Features.Imports.ImportViewModel(new ImportsApi(api))),
            "Reports" => new Wpf.Features.Reports.ReportsView(
                new Wpf.Features.Reports.ReportsViewModel(new ReportsApi(api), session)),
            "Users" => new Wpf.Features.Admin.UsersView(
                new Wpf.Features.Admin.UsersViewModel(new AdminApi(api), session)),
            "Roles" => new Wpf.Features.Admin.RolesView(
                new Wpf.Features.Admin.RolesViewModel(new AdminApi(api), session)),
            "Printers" => new Wpf.Features.Admin.PrintersView(
                new Wpf.Features.Admin.PrintersViewModel(new PrintApi(api), session)),
            "Settings" => new Wpf.Features.Admin.SettingsView(
                new Wpf.Features.Admin.SettingsViewModel(new AdminApi(api), session)),
            "Audit" => new Wpf.Features.Admin.AuditView(
                new Wpf.Features.Admin.AuditViewModel(new AdminApi(api))),
            _ => throw new ArgumentOutOfRangeException(nameof(screen), screen, "unmapped screen"),
        };
    }

    [StaTheory]
    [MemberData(nameof(ScreenNames))]
    public async Task Every_screen_renders_with_the_design_system(string screen)
    {
        EnsureApplicationResources();

        var view = await BuildAsync(screen);

        // Measure/arrange forces the whole template tree — including data
        // templates and row details — to be realised, which is where a missing
        // resource key actually throws.
        var host = new Window
        {
            Content = view, Width = 1280, Height = 800,
            WindowStyle = WindowStyle.None, ShowInTaskbar = false,
        };
        host.Measure(new Size(1280, 800));
        host.Arrange(new Rect(0, 0, 1280, 800));
        host.UpdateLayout();

        view.ActualWidth.Should().BeGreaterThanOrEqualTo(0);
    }

    [StaFact]
    public async Task The_login_window_renders()
    {
        EnsureApplicationResources();

        var handler = Handler();
        var api = await handler.LoggedInClientAsync();
        var login = new Wpf.Features.Login.LoginView(new Wpf.Features.Login.LoginViewModel(api));

        // A Window that is never shown has no layout pass of its own, so the
        // content tree is what proves the template and its resources resolved.
        var content = (FrameworkElement)login.Content;
        content.Measure(new Size(440, 600));
        content.Arrange(new Rect(0, 0, 440, 600));
        content.UpdateLayout();

        content.IsMeasureValid.Should().BeTrue();
        content.DesiredSize.Height.Should().BeGreaterThan(0, "the sign-in form must actually lay out");
    }

    /// <summary>Every supported resolution, including the narrowest laptop:
    /// a page that demands more width than the content area is one that clips
    /// controls off-screen for the operator.</summary>
    public static TheoryData<string, double, double> ScreenSizes
    {
        get
        {
            var data = new TheoryData<string, double, double>();
            foreach (var screen in new[]
                     {
                         "Dashboard", "Products", "Print", "PrintHistory", "Import",
                         "Reports", "Users", "Roles", "Printers", "Settings", "Audit",
                     })
            {
                foreach (var (width, height) in new[]
                         {
                             (1280d, 720d), (1366d, 768d), (1440d, 900d),
                             (1600d, 900d), (1920d, 1080d), (2560d, 1440d),
                         })
                {
                    data.Add(screen, width, height);
                }
            }
            return data;
        }
    }

    [StaTheory]
    [MemberData(nameof(ScreenSizes))]
    public async Task No_screen_overflows_its_content_area(string screen, double width, double height)
    {
        EnsureApplicationResources();

        var view = await BuildAsync(screen);

        // What a page actually gets: window minus the sidebar and shell chrome.
        var content = new Size(width - 240, height - 84);
        var host = new Window { Content = view, WindowStyle = WindowStyle.None, ShowInTaskbar = false };
        host.Measure(content);
        host.Arrange(new Rect(0, 0, content.Width, content.Height));
        host.UpdateLayout();

        view.DesiredSize.Width.Should().BeLessThanOrEqualTo(content.Width + 1,
            "{0} must fit the content area at {1}x{2} rather than clipping", screen, width, height);
    }
}
