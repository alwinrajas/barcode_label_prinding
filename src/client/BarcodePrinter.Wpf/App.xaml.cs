using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using BarcodePrinter.Client.Core;
using BarcodePrinter.Wpf.Features.Login;
using BarcodePrinter.Wpf.Shell;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace BarcodePrinter.Wpf;

/// <summary>
/// WPF entry point on the generic host (blueprint §12): same DI/config/logging
/// idioms as the server. Startup flow: LoginView → (forced password change) →
/// ShellView.
/// </summary>
public partial class App : Application
{
    private IHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        // The confirmed date format is dd/MM/yyyy (A-32). WPF formats dates with
        // the OS culture unless told otherwise, so DatePickers would otherwise
        // show "Tuesday - 21-Jul-26" on a machine set to another locale.
        var culture = new System.Globalization.CultureInfo("en-GB");
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(
                System.Windows.Markup.XmlLanguage.GetLanguage(culture.IetfLanguageTag)));

        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "BarcodePrinter");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(dataDir, "logs", "client-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14)
            .CreateLogger();

        // Blueprint §21.2: all three global handlers; an unhandled exception
        // never terminates the shell.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Fatal(args.ExceptionObject as Exception, "Unhandled AppDomain exception");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        // Client config: ONLY the API base URL lives here (§19.4) —
        // no credentials of any kind.
        var apiBaseUrl = ReadApiBaseUrl(Path.Combine(dataDir, "client.json"));

        _host = Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices(services =>
            {
                services.AddSingleton(_ =>
                {
                    var client = new HttpClient
                    {
                        BaseAddress = new Uri(apiBaseUrl),
                        Timeout = TimeSpan.FromSeconds(30),
                    };
                    // Declared on every request so the server can refuse a build
                    // too old to read its payloads correctly (§16). On a print
                    // screen a mis-read payload means wrong labels, not an error.
                    client.DefaultRequestHeaders.Add("X-Client-Version",
                        typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");
                    return client;
                });
                // Same instance the shell's ToastHost renders — VMs may also
                // reach it via ToastService.Instance this phase.
                services.AddSingleton(Services.ToastService.Instance);
                services.AddSingleton<ConnectionStatus>();
                services.AddSingleton<BarcodePrinter.Printing.Client.IWindowsPrinterProbe,
                    BarcodePrinter.Printing.Client.WindowsPrinterProbe>();
                services.AddSingleton<ApiClient>();
                services.AddSingleton<ProductsApi>();
                services.AddTransient<LoginViewModel>();
                services.AddTransient<LoginView>();
                // Session exists only after login; resolved lazily by pages.
                services.AddTransient(sp => sp.GetRequiredService<ApiClient>().Session
                    ?? throw new InvalidOperationException("Not authenticated."));
                services.AddTransient<Features.Products.ProductsViewModel>();
                services.AddTransient<Features.Products.ProductsView>();
                services.AddSingleton<ImportsApi>();
                services.AddSingleton<AdminApi>();
                services.AddSingleton<PrintApi>();
                services.AddSingleton<BarcodePrinter.Printing.Abstractions.IPrintTransport,
                    BarcodePrinter.Printing.Client.WindowsGraphicsTransport>();
                services.AddSingleton<BarcodePrinter.Printing.Abstractions.IPrintTransport,
                    BarcodePrinter.Printing.Client.WindowsRawTransport>();
                services.AddSingleton<BarcodePrinter.Printing.Client.ClientPrintDispatcher>();
                services.AddTransient<Features.Printing.PrintViewModel>();
                services.AddTransient<Features.Printing.PrintView>();
                services.AddTransient<Features.Printing.PrintHistoryViewModel>();
                services.AddTransient<Features.Printing.PrintHistoryView>();
                services.AddTransient<Features.Admin.PrintersViewModel>();
                services.AddTransient<Features.Admin.PrintersView>();
                services.AddSingleton<ReportsApi>();
                services.AddSingleton<DashboardApi>();
                services.AddTransient<Features.Dashboard.DashboardViewModel>();
                services.AddTransient<Features.Dashboard.DashboardView>();
                services.AddTransient<Features.Reports.ReportsViewModel>();
                services.AddTransient<Features.Reports.ReportsView>();
                services.AddTransient<Features.Admin.UsersViewModel>();
                services.AddTransient<Features.Admin.UsersView>();
                services.AddTransient<Features.Admin.RolesViewModel>();
                services.AddTransient<Features.Admin.RolesView>();
                services.AddTransient<Features.Admin.SettingsViewModel>();
                services.AddTransient<Features.Admin.SettingsView>();
                services.AddTransient<Features.Admin.AuditViewModel>();
                services.AddTransient<Features.Admin.AuditView>();
                services.AddTransient<Features.Imports.ImportViewModel>();
                services.AddTransient<Features.Imports.ImportView>();
            })
            .Build();
        _host.Start();

        ShowLogin();
        base.OnStartup(e);
    }

    private void ShowLogin()
    {
        var login = _host!.Services.GetRequiredService<LoginView>();
        var viewModel = (LoginViewModel)login.DataContext;

        viewModel.Authenticated += (_, _) =>
        {
            var session = _host.Services.GetRequiredService<ApiClient>().Session!;
            var shell = new ShellView(new ShellViewModel(session,
                _host.Services.GetRequiredService<ConnectionStatus>()), key => key switch
            {
                "dashboard" => _host.Services.GetRequiredService<Features.Dashboard.DashboardView>(),
                "products" => _host.Services.GetRequiredService<Features.Products.ProductsView>(),
                "import" => _host.Services.GetRequiredService<Features.Imports.ImportView>(),
                "print" => _host.Services.GetRequiredService<Features.Printing.PrintView>(),
                "history" => _host.Services.GetRequiredService<Features.Printing.PrintHistoryView>(),
                "users" => _host.Services.GetRequiredService<Features.Admin.UsersView>(),
                "roles" => _host.Services.GetRequiredService<Features.Admin.RolesView>(),
                "reports" => _host.Services.GetRequiredService<Features.Reports.ReportsView>(),
                "printers" => _host.Services.GetRequiredService<Features.Admin.PrintersView>(),
                "settings" => _host.Services.GetRequiredService<Features.Admin.SettingsView>(),
                "audit" => _host.Services.GetRequiredService<Features.Admin.AuditView>(),
                _ => null,   // remaining pages arrive with their phases
            });
            // Local printers attached to this PC are dispatched from here (§7.3).
            _host.Services.GetRequiredService<BarcodePrinter.Printing.Client.ClientPrintDispatcher>().Start();

            MainWindow = shell;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            shell.Show();
            login.Close();
        };

        login.Closed += (_, _) =>
        {
            // Closing login without authenticating exits the app.
            if (MainWindow is not ShellView)
            {
                Shutdown();
            }
        };

        login.Show();
    }

    private static string ReadApiBaseUrl(string configPath)
    {
        try
        {
            if (File.Exists(configPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
                if (doc.RootElement.TryGetProperty("apiBaseUrl", out var url) &&
                    url.GetString() is { Length: > 0 } value)
                {
                    return value;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "client.json unreadable — using default API URL");
        }
        return "http://127.0.0.1:5188";   // development default
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled dispatcher exception");
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.StopAsync().GetAwaiter().GetResult();
        _host?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
