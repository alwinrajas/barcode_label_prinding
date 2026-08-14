using System.Security.Claims;
using BarcodePrinter.Api.Auth;
using BarcodePrinter.Api.Middleware;
using BarcodePrinter.Application.Abstractions;
using BarcodePrinter.Application.Products;
using BarcodePrinter.Contracts;
using BarcodePrinter.Contracts.Products;
using BarcodePrinter.Infrastructure.Queries;
using Microsoft.AspNetCore.Mvc;

namespace BarcodePrinter.Api.Endpoints;

public static class ProductEndpoints
{
    private const long MaxImageBytes = 5 * 1024 * 1024;

    public static void MapProductEndpoints(this WebApplication app)
    {
        var group = app.MapGroup(ApiRoutes.Products.Base);

        group.MapGet("/", async (
                [FromQuery] string? q, [FromQuery] string? cursor,
                [FromQuery] int? pageSize, [FromQuery] bool? includeInactive,
                ProductsQuery query, CancellationToken ct) =>
            Results.Ok(await query.ListAsync(q, includeInactive ?? false,
                cursor, pageSize ?? 50, ct)))
            .RequirePermission(PermissionCodes.ProductView);

        group.MapGet("/{id:long}", async (long id, ProductsQuery query, CancellationToken ct) =>
                await query.GetDetailAsync(id, ct) is { } detail
                    ? Results.Ok(detail)
                    : throw new BarcodePrinter.Domain.NotFoundException("Product", id))
            .RequirePermission(PermissionCodes.ProductView);

        group.MapPost("/", async (
                SaveProductRequest request, ProductService service,
                ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
            {
                var id = await service.CreateAsync(request, Actor(user, http), ct);
                return Results.Created($"{ApiRoutes.Products.Base}/{id}", new { id });
            })
            .RequirePermission(PermissionCodes.ProductAdd);

        group.MapPut("/{id:long}", async (
                long id, SaveProductRequest request, ProductService service,
                ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
            {
                await service.UpdateAsync(id, request, Actor(user, http), ct);
                return Results.NoContent();
            })
            .RequirePermission(PermissionCodes.ProductEdit);

        // Deactivate — master data is never physically deleted (A-10).
        group.MapDelete("/{id:long}", async (
                long id, ProductService service,
                ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
            {
                await service.SetActiveAsync(id, false, Actor(user, http), ct);
                return Results.NoContent();
            })
            .RequirePermission(PermissionCodes.ProductDelete);

        group.MapPost("/{id:long}/activate", async (
                long id, ProductService service,
                ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
            {
                await service.SetActiveAsync(id, true, Actor(user, http), ct);
                return Results.NoContent();
            })
            .RequirePermission(PermissionCodes.ProductEdit);

        // ---- Images (§9.4: served only through the API, content-addressed) ----

        group.MapPost("/{id:long}/image", async (
                long id, IFormFile file, ProductService service,
                ClaimsPrincipal user, HttpContext http, CancellationToken ct) =>
            {
                if (file.Length is 0 or > MaxImageBytes)
                {
                    return Results.Problem(statusCode: 400, title: "IMAGE_TOO_LARGE",
                        detail: "The image must be between 1 byte and 5 MB.");
                }
                await using var stream = file.OpenReadStream();
                var hash = await service.SetImageAsync(id, stream, file.FileName, Actor(user, http), ct);
                return Results.Ok(new { hash });
            })
            .RequirePermission(PermissionCodes.ProductEdit)
            .DisableAntiforgery();

        group.MapGet("/{id:long}/image", async (
                long id, [FromQuery] string? variant,
                ProductsQuery query, IProductImageStore store, HttpContext http,
                CancellationToken ct) =>
            {
                var hash = await query.GetImageHashAsync(id, ct);
                if (hash is null)
                {
                    throw new BarcodePrinter.Domain.NotFoundException("Product image", id);
                }

                // ETag = content hash: unchanged images are a 304, and the
                // client disk cache keys on the same value (§11.2).
                if (http.Request.Headers.IfNoneMatch.ToString().Trim('"') == hash)
                {
                    return Results.StatusCode(StatusCodes.Status304NotModified);
                }

                var v = string.Equals(variant, "thumb", StringComparison.OrdinalIgnoreCase)
                    ? ImageVariant.Thumb : ImageVariant.Full;
                var stream = await store.OpenAsync(hash, v, ct);
                if (stream is null)
                {
                    throw new BarcodePrinter.Domain.NotFoundException("Product image", id);
                }

                http.Response.Headers.ETag = $"\"{hash}\"";
                http.Response.Headers.CacheControl = "private, max-age=86400";
                return Results.Stream(stream, "image/jpeg");
            })
            .RequirePermission(PermissionCodes.ProductView);

        // ---- Lookups ----

        app.MapGet(ApiRoutes.Products.Uoms, async (ProductsQuery query, CancellationToken ct) =>
                Results.Ok(await query.UomsAsync(ct)))
            .RequirePermission(PermissionCodes.ProductView);

        app.MapGet(ApiRoutes.Products.Categories, async (ProductsQuery query, CancellationToken ct) =>
                Results.Ok(await query.CategoriesAsync(ct)))
            .RequirePermission(PermissionCodes.ProductView);
    }

    private static ActorInfo Actor(ClaimsPrincipal user, HttpContext http) => new(
        long.Parse(user.FindFirstValue(AppClaimTypes.UserId) ?? "0"),
        user.FindFirstValue(AppClaimTypes.Username) ?? "",
        http.Items[CorrelationIdMiddleware.Header] as string,
        http.Request.Headers["X-Workstation"].FirstOrDefault());
}
