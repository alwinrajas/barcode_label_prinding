using System.Security.Claims;
using System.Text;
using BarcodePrinter.Api.Auth;
using BarcodePrinter.Api.Middleware;
using BarcodePrinter.Application.Abstractions;
using BarcodePrinter.Contracts;
using BarcodePrinter.Contracts.Templates;
using BarcodePrinter.Infrastructure.Templates;
using BarcodePrinter.Labels.Barcodes;
using BarcodePrinter.Labels.Binding;

namespace BarcodePrinter.Api.Endpoints;

public static class TemplateEndpoints
{
    private const long MaxArtifactBytes = 2 * 1024 * 1024;

    public static void MapTemplateEndpoints(this WebApplication app)
    {
        var group = app.MapGroup(ApiRoutes.Templates.Base);

        // Vocabulary for the mapping UI — the UI can only offer valid keys.
        group.MapGet("/vocabulary", () => Results.Ok(new TemplateVocabularyDto(
                TokenVocabulary.All,
                Enum.GetNames<FieldDataKind>(),
                Enum.GetNames<FieldTransform>(),
                Enum.GetNames<OverflowBehaviour>(),
                Enum.GetNames<BarcodeSymbology>(),
                TokenVocabulary.FeedbackUrlKey)))
            .RequirePermission(PermissionCodes.SettingsManageTemplates);

        group.MapGet("/", async (TemplatesQuery query, CancellationToken ct) =>
                Results.Ok(await query.ListAsync(ct)))
            .RequirePermission(PermissionCodes.SettingsView);

        group.MapGet("/{id:long}", async (long id, TemplatesQuery query, CancellationToken ct) =>
                await query.GetAsync(id, ct) is { } detail ? Results.Ok(detail) : throw new BarcodePrinter.Domain.NotFoundException("Template", id))
            .RequirePermission(PermissionCodes.SettingsView);

        group.MapGet("/{id:long}/artifact", async (long id, TemplatesQuery query, CancellationToken ct) =>
                await query.GetArtifactAsync(id, ct) is { } bytes
                    ? Results.File(bytes, "text/plain", $"template-{id}.zpl")
                    : throw new BarcodePrinter.Domain.NotFoundException("Template artifact", id))
            .RequirePermission(PermissionCodes.SettingsManageTemplates);

        // Register / re-version: upload the client's own template file.
        group.MapPost("/", async (
                HttpRequest http, TemplateService service,
                ClaimsPrincipal user, HttpContext context, CancellationToken ct) =>
            {
                var form = await http.ReadFormAsync(ct);
                var file = form.Files["file"];
                if (file is null || file.Length == 0 || file.Length > MaxArtifactBytes)
                {
                    throw new BarcodePrinter.Domain.DomainException(ErrorCodes.ValidationFailed,
                        "Attach the template file (max 2 MB).");
                }

                var request = new RegisterTemplateRequest(
                    Required(form, "code"), Required(form, "name"),
                    Optional(form, "description"),
                    Optional(form, "templateFormat") ?? "Zpl",
                    Decimal(form, "widthMm"), Decimal(form, "heightMm"),
                    Short(form, "dpi"), Decimal(form, "gapMm"),
                    Optional(form, "orientation"), Optional(form, "layoutType"),
                    Optional(form, "mediaType"), Optional(form, "mediaTracking"));

                using var ms = new MemoryStream();
                await file.CopyToAsync(ms, ct);

                var id = await service.RegisterAsync(
                    request, file.FileName, ms.ToArray(), Actor(user, context), ct);
                return Results.Created(ApiRoutes.Templates.ById(id), new { id });
            })
            .RequirePermission(PermissionCodes.SettingsManageTemplates)
            .DisableAntiforgery();

        group.MapPut("/{id:long}/fields", async (
                long id, SaveFieldMappingRequest request, TemplateService service,
                ClaimsPrincipal user, HttpContext context, CancellationToken ct) =>
            {
                await service.SaveFieldMappingAsync(id, request, Actor(user, context), ct);
                return Results.NoContent();
            })
            .RequirePermission(PermissionCodes.SettingsManageTemplates);

        group.MapPost("/{id:long}/activate", async (
                long id, bool? active, TemplateService service,
                ClaimsPrincipal user, HttpContext context, CancellationToken ct) =>
            {
                await service.SetActiveAsync(id, active ?? true, Actor(user, context), ct);
                return Results.NoContent();
            })
            .RequirePermission(PermissionCodes.SettingsManageTemplates);

        group.MapPost("/{id:long}/default", async (
                long id, TemplateService service,
                ClaimsPrincipal user, HttpContext context, CancellationToken ct) =>
            {
                await service.SetDefaultAsync(id, Actor(user, context), ct);
                return Results.NoContent();
            })
            .RequirePermission(PermissionCodes.SettingsManageTemplates);

        // Preview: render the current version with representative sample data.
        group.MapGet("/{id:long}/preview.zpl", async (
                long id, TemplateService service, CancellationToken ct) =>
            {
                var zpl = await service.RenderSampleAsync(id, ct);
                return Results.Text(zpl, "text/plain", Encoding.UTF8);
            })
            .RequirePermission(PermissionCodes.SettingsView);
    }

    private static ActorInfo Actor(ClaimsPrincipal user, HttpContext http) => new(
        long.Parse(user.FindFirstValue(AppClaimTypes.UserId) ?? "0"),
        user.FindFirstValue(AppClaimTypes.Username) ?? "",
        http.Items[CorrelationIdMiddleware.Header] as string);

    private static string Required(IFormCollection form, string key) =>
        form[key].FirstOrDefault() is { Length: > 0 } value
            ? value
            : throw new Domain.DomainException(ErrorCodes.ValidationFailed, $"'{key}' is required.");

    private static string? Optional(IFormCollection form, string key) =>
        form[key].FirstOrDefault() is { Length: > 0 } value ? value : null;

    private static decimal? Decimal(IFormCollection form, string key) =>
        decimal.TryParse(form[key].FirstOrDefault(), out var value) ? value : null;

    private static short? Short(IFormCollection form, string key) =>
        short.TryParse(form[key].FirstOrDefault(), out var value) ? value : null;
}
