using System.Text;
using System.Threading.RateLimiting;
using BarcodePrinter.Api.Auth;
using BarcodePrinter.Api.Endpoints;
using BarcodePrinter.Api.Health;
using BarcodePrinter.Api.Middleware;
using BarcodePrinter.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.Tokens;
using Serilog;

// ============================================================================
// BarcodePrinter.Api — ASP.NET Core host, deployed as a Windows Service.
// Phase 2 scope: auth (JWT + rotating refresh), permission-per-endpoint RBAC,
// security-stamp revocation, audit, ProblemDetails, rate-limited login.
// ============================================================================

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

// The service inherits the culture of whatever account Windows starts it under,
// and an operator's custom short-date pattern then leaks into exports and logs
// (observed in the field: "16 - 08 - 2026" instead of "16/08/2026"). Business
// output must not depend on a machine setting, so the culture is pinned here —
// en-GB to match the client and the dd/MM/yyyy label defaults.
var serverCulture = System.Globalization.CultureInfo.GetCultureInfo("en-GB");
System.Globalization.CultureInfo.DefaultThreadCurrentCulture = serverCulture;
System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = serverCulture;

try
{
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory,
    });

    builder.Host.UseWindowsService(o => o.ServiceName = "BarcodePrinter.Api");

    // Validate the container in EVERY environment, not just Development.
    // A captive-dependency mistake (a singleton worker consuming a scoped
    // service) otherwise passes the whole test suite and only fails when the
    // Windows Service starts on the customer's server.
    builder.Host.UseDefaultServiceProvider(o =>
    {
        o.ValidateScopes = true;
        o.ValidateOnBuild = true;
    });

    builder.Host.UseSerilog((ctx, services, cfg) => cfg
        .ReadFrom.Configuration(ctx.Configuration)
        .Enrich.FromLogContext()
        .Enrich.WithMachineName()
        // Applied before any value is read, so a careless "{@request}" cannot
        // put a password into a log file that is kept 30 days and backed up
        // off-box (§13).
        .Destructure.With<SecretRedactionPolicy>());

    // Windows services must not rely on Data Protection's profile-based defaults.
    // LocalMachine DPAPI keeps the key ring readable across service account password lifecycle events.
    if (builder.Environment.IsProduction())
    {
        var keyRingPath = builder.Configuration["DataProtection:KeyRingPath"];
        if (string.IsNullOrWhiteSpace(keyRingPath))
        {
            keyRingPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "BarcodePrinter", "keys");
        }
        SanitizeKeyRing(keyRingPath, Log.Logger);
        builder.Services.AddDataProtection()
            .SetApplicationName("Barcode Label Printing")
            .PersistKeysToFileSystem(new DirectoryInfo(keyRingPath))
            .ProtectKeysWithDpapi(protectToLocalMachine: true);
    }

    builder.Services.AddBarcodePrinterInfrastructure(builder.Configuration);

    // --- AuthN: JWT bearer with security-stamp validation (§19.3) ---
    builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.Section));
    builder.Services.AddSingleton<JwtTokenService>();
    builder.Services.AddSingleton<SecurityStampValidator>();

    var jwt = builder.Configuration.GetSection(JwtOptions.Section).Get<JwtOptions>()
        ?? throw new InvalidOperationException("Jwt configuration section is missing.");
    if (string.IsNullOrWhiteSpace(jwt.SigningKey) || Encoding.UTF8.GetByteCount(jwt.SigningKey) < 32)
    {
        throw new InvalidOperationException(
            "Jwt:SigningKey is missing or shorter than 256 bits. Generate one at install (§19.4).");
    }

    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(o =>
        {
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = jwt.Issuer,
                ValidAudience = jwt.Audience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
                ClockSkew = TimeSpan.FromSeconds(30),
            };
            o.MapInboundClaims = false;   // keep our compact claim names
            o.Events = new JwtBearerEvents
            {
                // SignalR websockets cannot send an Authorization header �
                // the token arrives as ?access_token= on hub paths only.
                OnMessageReceived = ctx =>
                {
                    var accessToken = ctx.Request.Query["access_token"];
                    if (!string.IsNullOrEmpty(accessToken) &&
                        ctx.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                    {
                        ctx.Token = accessToken;
                    }
                    return Task.CompletedTask;
                },
                OnTokenValidated = async ctx =>
                {
                    var stampClaim = ctx.Principal?.FindFirst(AppClaimTypes.SecurityStamp)?.Value;
                    var idClaim = ctx.Principal?.FindFirst(AppClaimTypes.UserId)?.Value;
                    if (stampClaim is null || !long.TryParse(idClaim, out var userId))
                    {
                        ctx.Fail("Token is missing required claims.");
                        return;
                    }
                    var validator = ctx.HttpContext.RequestServices
                        .GetRequiredService<SecurityStampValidator>();
                    if (!await validator.IsCurrentAsync(userId, stampClaim, ctx.HttpContext.RequestAborted))
                    {
                        ctx.Fail("Security stamp is no longer current.");
                    }
                },
            };
        });

    // --- AuthZ: no endpoint is unprotected by default (§19.2) ---
    builder.Services.AddSingleton<IAuthorizationHandler, PermissionHandler>();
    builder.Services.AddAuthorization(o =>
    {
        o.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();

        // Named equivalents of RequirePermission, for [Authorize] on a hub —
        // there the policy name has to be a compile-time constant.
        foreach (var permission in BarcodePrinter.Contracts.PermissionCodes.All)
        {
            o.AddPolicy(PermissionPolicy.For(permission), p => p
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(permission)));
        }
    });

    // --- Rate limiting on login (§19.5): per client IP ---
    var loginPermitLimit = builder.Configuration.GetValue("RateLimit:LoginPerMinute", 10);
    builder.Services.AddRateLimiter(o =>
    {
        o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromMinutes(1),
                PermitLimit = loginPermitLimit,
                QueueLimit = 0,
            }));
    });

    builder.Services.AddHealthChecks()
        .AddCheck<MySqlHealthCheck>("mysql", tags: ["ready"]);

    // --- Imports: SignalR progress + hosted worker (B-16 / �15) ---
    builder.Services.AddSignalR();
    builder.Services.AddScoped<BarcodePrinter.Api.Endpoints.ProductsTemplateData>();
    builder.Services.AddScoped<BarcodePrinter.Infrastructure.Imports.IImportProgressBroadcaster,
        BarcodePrinter.Api.Imports.SignalRImportProgressBroadcaster>();
    builder.Services.AddHostedService<BarcodePrinter.Api.Imports.ImportWorker>();

    // --- Printing: transports + per-printer dispatch + lease watchdog (�8.3) ---
    builder.Services.AddSingleton<BarcodePrinter.Api.Printing.PrintJobQueue>();
    builder.Services.AddSingleton<BarcodePrinter.Infrastructure.Printing.IPrintJobQueue>(
        sp => sp.GetRequiredService<BarcodePrinter.Api.Printing.PrintJobQueue>());
    builder.Services.AddSingleton<BarcodePrinter.Printing.Abstractions.IPrintTransport,
        BarcodePrinter.Printing.Server.TcpRawTransport>();
    builder.Services.AddSingleton<BarcodePrinter.Printing.Abstractions.IPrintTransport>(
        _ => new BarcodePrinter.Printing.Server.FilePrintTransport(
            builder.Configuration["Printing:FileOutputPath"]
            ?? Path.Combine(AppContext.BaseDirectory, "data", "printouts")));
    // Singleton: consumed by the singleton dispatch worker and lease watchdog as
    // well as by scoped request services. It creates its own scope per push.
    builder.Services.AddSingleton<BarcodePrinter.Infrastructure.Printing.IPrintJobStatusBroadcaster,
        BarcodePrinter.Api.Printing.SignalRPrintJobStatusBroadcaster>();
    builder.Services.AddHostedService<BarcodePrinter.Api.Printing.PrintDispatchWorker>();
    builder.Services.AddHostedService<BarcodePrinter.Api.Printing.PrintLeaseWatchdog>();

    var app = builder.Build();

    // HTTPS everywhere except Development and Testing (A-28). Access tokens and
    // whole print payloads cross the LAN; a redirect does not secure the first
    // request on its own, but it stops a mis-typed http:// URL from sending them
    // in the clear and HSTS stops the client repeating the mistake.
    if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing"))
    {
        app.UseHsts();
        app.UseHttpsRedirection();
    }

    app.UseMiddleware<CorrelationIdMiddleware>();
    app.UseMiddleware<ExceptionMappingMiddleware>();
    app.UseMiddleware<ClientVersionMiddleware>();
    app.UseSerilogRequestLogging();
    app.UseRateLimiter();
    app.UseAuthentication();
    app.UseAuthorization();

    app.MapHealthChecks("/health").AllowAnonymous();
    app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        Predicate = r => r.Tags.Contains("ready"),
    }).AllowAnonymous();

    app.MapGet("/", () => Results.Ok(new
    {
        service = "BarcodePrinter.Api",
        version = typeof(Program).Assembly.GetName().Version?.ToString(3),
    })).AllowAnonymous();

    app.MapAuthEndpoints();
    app.MapProductEndpoints();
    app.MapImportEndpoints();
    app.MapTemplateEndpoints();
    app.MapAdminEndpoints();
    app.MapPrintEndpoints();
    app.MapReportEndpoints();
    app.MapGet(BarcodePrinter.Contracts.ApiRoutes.Dashboard.Base, async (
            BarcodePrinter.Infrastructure.Dashboard.DashboardQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.GetAsync(ct)))
        .RequirePermission(BarcodePrinter.Contracts.PermissionCodes.DashboardView);
    // Hubs push the same data the endpoints return, so they are authorized the
    // same way — an anonymous subscription would be a way round the permission.
    app.MapHub<BarcodePrinter.Api.Imports.ImportsHub>(
        BarcodePrinter.Contracts.ApiRoutes.Imports.Hub);
    app.MapHub<BarcodePrinter.Api.Printing.PrintJobsHub>(
        BarcodePrinter.Contracts.ApiRoutes.Print.Hub);

    app.Run();
}
catch (Exception ex)
{
    // Log and RETHROW: the service manager (and WebApplicationFactory in
    // tests) must see startup failures — swallowing them hides the cause.
    Log.Fatal(ex, "BarcodePrinter.Api terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

// Exposed for WebApplicationFactory in integration tests.
public partial class Program
{
    private static void SanitizeKeyRing(string keyRingPath, Serilog.ILogger logger)
    {
        try
        {
            if (!Directory.Exists(keyRingPath))
            {
                Directory.CreateDirectory(keyRingPath);
                return;
            }

            var xmlFiles = Directory.GetFiles(keyRingPath, "*.xml");
            string? quarantineDir = null;

            foreach (var file in xmlFiles)
            {
                try
                {
                    var content = File.ReadAllText(file);
                    if (content.Contains("DpapiXmlDecryptor"))
                    {
                        var match = System.Text.RegularExpressions.Regex.Match(content, @"<value>([^<]+)</value>");
                        if (match.Success)
                        {
                            var cipherBytes = Convert.FromBase64String(match.Groups[1].Value.Trim());
                            try
                            {
#pragma warning disable CA1416
                                System.Security.Cryptography.ProtectedData.Unprotect(
                                    cipherBytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
#pragma warning restore CA1416
                            }
                            catch (System.Security.Cryptography.CryptographicException ex)
                            {
                                quarantineDir ??= Directory.CreateDirectory(Path.Combine(keyRingPath, "quarantine")).FullName;
                                var fileName = Path.GetFileName(file);
                                var destPath = Path.Combine(quarantineDir, $"{fileName}.quarantine-{DateTime.UtcNow:yyyyMMddHHmmss}.bak");
                                File.Move(file, destPath);
                                logger.Warning(ex, "[DataProtection] Quarantined unreadable DPAPI key file '{FileName}' to '{QuarantinePath}'. A new machine-scoped key will be generated.", fileName, destPath);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.Warning(ex, "[DataProtection] Preflight error checking key file '{File}'.", file);
                }
            }
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "[DataProtection] Preflight key ring sanitization encountered an issue.");
        }
    }
}
