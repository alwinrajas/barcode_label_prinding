using BarcodePrinter.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace BarcodePrinter.Api.Middleware;

/// <summary>
/// Rejects clients older than <c>MinimumClientVersion</c> (§16).
///
/// This is the mechanism that makes sharing the <c>Contracts</c> assembly across
/// the tiers safe: a breaking change raises the minimum and stale clients stop
/// with a clear "update required" instead of silently mis-reading a payload —
/// which, on a print screen, means labels that are wrong rather than absent.
///
/// Deliberately permissive about the header being ABSENT: health checks,
/// monitoring and curl have no client version and must keep working. It is only
/// a version that is present and too old that is refused.
/// </summary>
public sealed class ClientVersionMiddleware(
    RequestDelegate next, IConfiguration configuration, ILogger<ClientVersionMiddleware> logger)
{
    public const string Header = "X-Client-Version";

    private readonly Version _minimum =
        Version.TryParse(configuration["MinimumClientVersion"], out var configured)
            ? configured
            : new Version(1, 0, 0);

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(Header, out var raw) &&
            Version.TryParse(raw.ToString(), out var client) &&
            client < _minimum)
        {
            logger.LogWarning(
                "Rejected client {ClientVersion}; minimum is {Minimum} ({Path})",
                client, _minimum, context.Request.Path);

            context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = StatusCodes.Status426UpgradeRequired,
                Title = "This version of the application is out of date.",
                Detail = $"This workstation is running {client}. Version {_minimum} or later is " +
                         "required. Ask IT to install the current version.",
                Extensions =
                {
                    ["code"] = ErrorCodes.ClientUpdateRequired,
                    ["minimumClientVersion"] = _minimum.ToString(),
                    ["correlationId"] = context.Items[CorrelationIdMiddleware.Header] as string,
                },
            });
            return;
        }

        await next(context);
    }
}
