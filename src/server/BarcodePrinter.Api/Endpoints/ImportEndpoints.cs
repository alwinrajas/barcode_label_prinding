using System.Security.Claims;
using BarcodePrinter.Api.Auth;
using BarcodePrinter.Contracts;
using BarcodePrinter.Contracts.Imports;
using BarcodePrinter.Infrastructure.Imports;
using BarcodePrinter.Infrastructure.Services;
using Dapper;

namespace BarcodePrinter.Api.Endpoints;

public static class ImportEndpoints
{
    public static void MapImportEndpoints(this WebApplication app)
    {
        var group = app.MapGroup(ApiRoutes.Imports.Base);

        group.MapGet("/template.xlsx", async (ProductsTemplateData data, CancellationToken ct) =>
            {
                var bytes = ExcelTemplate.Build(await data.UomCodesAsync(ct));
                return Results.File(bytes,
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    "product-import-template.xlsx");
            })
            .RequirePermission(PermissionCodes.ProductImport);

        group.MapPost("/", async (
                IFormFile file, HttpContext http, ClaimsPrincipal user,
                ImportsQuery query, ImportQueue queue, IDbConnectionFactory connections,
                BarcodePrinter.Application.Abstractions.ISettingsProvider settings,
                IConfiguration configuration, CancellationToken ct) =>
            {
                var userId = long.Parse(user.FindFirstValue(AppClaimTypes.UserId)!);

                var maxMb = await settings.GetIntAsync("Import:MaxUploadMb", 100, ct);
                if (file.Length == 0 || file.Length > maxMb * 1024L * 1024L)
                {
                    return Results.Problem(statusCode: 400, title: ErrorCodes.ValidationFailed,
                        detail: $"The file must be between 1 byte and {maxMb} MB.");
                }
                if (!Path.GetExtension(file.FileName).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
                {
                    return Results.Problem(statusCode: 400, title: ErrorCodes.ValidationFailed,
                        detail: "Only .xlsx files are supported. Download the template to get started.");
                }
                // One running import per user (§15 guard rails).
                if (await query.HasRunningAsync(userId, ct))
                {
                    return Results.Problem(statusCode: 400, title: ErrorCodes.ValidationFailed,
                        detail: "You already have an import in progress. Wait for it to finish.");
                }

                var importsDir = configuration["Imports:RootPath"]
                    ?? Path.Combine(AppContext.BaseDirectory, "data", "imports");
                Directory.CreateDirectory(importsDir);

                var policy = await settings.GetAsync("Import:CommitPolicy", ct) ?? "AllOrNothing";

                await using var conn = await connections.OpenAsync(ct);
                var batchId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                    """
                    INSERT INTO import_batches
                        (file_name, stored_path, uploaded_by, uploaded_at, status, commit_policy)
                    VALUES (@fileName, @storedPath, @userId, UTC_TIMESTAMP(3), 'Uploaded', @policy);
                    SELECT LAST_INSERT_ID();
                    """,
                    new
                    {
                        fileName = Path.GetFileName(file.FileName),
                        storedPath = Path.Combine(importsDir, $"batch-pending.xlsx"),
                        userId,
                        policy,
                    }, cancellationToken: ct));

                // Store under the batch id, then fix the path — the id names the file.
                var storedPath = Path.Combine(importsDir, $"batch-{batchId}.xlsx");
                await using (var target = File.Create(storedPath))
                {
                    await file.CopyToAsync(target, ct);
                }
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE import_batches SET stored_path = @storedPath WHERE id = @batchId",
                    new { storedPath, batchId }, cancellationToken: ct));

                await queue.Writer.WriteAsync(batchId, ct);
                return Results.Accepted($"{ApiRoutes.Imports.Base}/{batchId}",
                    new ImportAcceptedResponse(batchId));
            })
            .RequirePermission(PermissionCodes.ProductImport)
            .DisableAntiforgery();

        group.MapGet("/recent", async (ClaimsPrincipal user, ImportsQuery query, CancellationToken ct) =>
                Results.Ok(await query.RecentAsync(
                    long.Parse(user.FindFirstValue(AppClaimTypes.UserId)!), ct)))
            .RequirePermission(PermissionCodes.ProductImport);

        group.MapGet("/{id:long}", async (long id, ImportsQuery query, CancellationToken ct) =>
                await query.GetAsync(id, ct) is { } dto ? Results.Ok(dto) : throw new BarcodePrinter.Domain.NotFoundException("Import batch", id))
            .RequirePermission(PermissionCodes.ProductImport);

        group.MapGet("/{id:long}/errors.xlsx", async (
                long id, ErrorReportBuilder builder, CancellationToken ct) =>
                await builder.BuildAsync(id, ct) is { } bytes
                    ? Results.File(bytes,
                        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                        $"import-{id}-errors.xlsx")
                    : throw new BarcodePrinter.Domain.NotFoundException("Import batch", id))
            .RequirePermission(PermissionCodes.ProductImport);

        group.MapPost("/{id:long}/cancel", async (
                long id, IDbConnectionFactory connections, CancellationToken ct) =>
            {
                await using var conn = await connections.OpenAsync(ct);
                var changed = await conn.ExecuteAsync(new CommandDefinition(
                    """
                    UPDATE import_batches SET status = 'Cancelled', finished_at = UTC_TIMESTAMP(3)
                    WHERE id = @id AND status IN ('Uploaded','Validating')
                    """, new { id }, cancellationToken: ct));
                return changed > 0 ? Results.NoContent() : Results.Conflict();
            })
            .RequirePermission(PermissionCodes.ProductImport);

        app.MapGet(ApiRoutes.Products.Export, async (ProductExport export, CancellationToken ct) =>
                Results.File(await export.BuildAsync(ct),
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    $"products-{DateTime.UtcNow:yyyyMMdd-HHmm}.xlsx"))
            .RequirePermission(PermissionCodes.ProductExport);
    }
}

/// <summary>UOM codes for the template dropdown.</summary>
public sealed class ProductsTemplateData(IDbConnectionFactory connections)
{
    public async Task<IReadOnlyList<string>> UomCodesAsync(CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        return (await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT code FROM uoms WHERE is_active = 1 ORDER BY code",
            cancellationToken: ct))).ToList();
    }
}
