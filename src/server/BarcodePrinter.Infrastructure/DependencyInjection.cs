using BarcodePrinter.Application.Abstractions;
using BarcodePrinter.Application.Auth;
using BarcodePrinter.Application.Products;
using BarcodePrinter.Application.Printing;
using BarcodePrinter.Infrastructure.Admin;
using BarcodePrinter.Infrastructure.Dashboard;
using BarcodePrinter.Infrastructure.Printing;
using BarcodePrinter.Infrastructure.Reports;
using BarcodePrinter.Infrastructure.Imports;
using BarcodePrinter.Infrastructure.Templates;
using BarcodePrinter.Labels;
using BarcodePrinter.Labels.Barcodes;
using BarcodePrinter.Labels.Binding;
using BarcodePrinter.Labels.Zpl;
using BarcodePrinter.Infrastructure.Persistence;
using BarcodePrinter.Infrastructure.Queries;
using BarcodePrinter.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BarcodePrinter.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddBarcodePrinterInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        Persistence.DapperConfiguration.Configure();

        var connectionString = ConnectionStrings.Normalize(
            configuration.GetConnectionString("BarcodePrinter")
            ?? throw new InvalidOperationException("Connection string 'BarcodePrinter' is not configured."));

        services.AddDbContext<AppDbContext>(o => o.UseMySql(
            connectionString,
            ServerVersion.Create(8, 4, 0, Pomelo.EntityFrameworkCore.MySql.Infrastructure.ServerType.MySql)));

        services.AddMemoryCache();

        services.AddSingleton<IDbConnectionFactory, MySqlConnectionFactory>();
        services.AddSingleton<IPasswordService, PasswordService>();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<IAuditWriter, AuditWriter>();
        services.AddSingleton<ISettingsProvider, SettingsProvider>();

        services.AddScoped<IUserStore, UserStore>();
        services.AddScoped<IRefreshTokenStore, RefreshTokenStore>();
        services.AddScoped<AuthService>();
        services.AddScoped<UsersQuery>();

        services.AddSingleton<IProductImageStore, FileSystemImageStore>();
        services.AddScoped<IProductRepository, ProductRepository>();
        services.AddScoped<ProductService>();
        services.AddScoped<ProductsQuery>();

        services.AddSingleton<ImportQueue>();
        services.AddScoped<ImportPipeline>();
        services.AddScoped<ImportsQuery>();
        services.AddScoped<ErrorReportBuilder>();
        services.AddScoped<ProductExport>();

        services.AddSingleton<ITemplateAdapter, ZplTemplateAdapter>();
        // Native = a definition we own; Zpl = a file the client owns. Both
        // compile to the same stored format, so the print engine sees one shape.
        services.AddSingleton<ITemplateAdapter, Labels.Native.NativeTemplateAdapter>();
        services.AddScoped<Printing.ZplImageConverter>();
        services.AddSingleton<Printing.LabelRasterizer>();
        services.AddScoped<Printing.LabelPreviewService>();
        services.AddSingleton<TemplateAdapterRegistry>();
        services.AddSingleton<IBarcodeEncoder, BarcodeEncoder>();
        services.AddSingleton<FieldBinder>();
        services.AddScoped<TemplateService>();
        services.AddScoped<TemplatesQuery>();

        services.AddScoped<AdminQueries>();
        services.AddScoped<UserAdminService>();
        services.AddScoped<RoleAdminService>();
        services.AddScoped<SettingsAdminService>();

        services.AddSingleton<ICartonSequenceAllocator, CartonSequenceAllocator>();
        services.AddSingleton<CartonStrategyResolver>();
        services.AddScoped<TemplateRenderService>();
        services.AddScoped<PrintJobService>();
        services.AddScoped<PrintQueries>();
        services.AddScoped<PrinterAdminService>();
        services.AddScoped<ClientDispatchService>();
        services.AddScoped<PrintPreviewService>();

        services.AddScoped<ReportQueries>();
        services.AddScoped<ReportExport>();
        services.AddSingleton<Dashboard.BackupStatusReader>();
        services.AddScoped<DashboardQueries>();

        return services;
    }
}
