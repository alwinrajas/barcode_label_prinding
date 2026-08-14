using BarcodePrinter.Contracts;
using BarcodePrinter.Domain;
using Microsoft.AspNetCore.Mvc;
using Serilog.Context;

namespace BarcodePrinter.Api.Middleware;

/// <summary>Flows X-Correlation-Id (client-generated per user action) through
/// logs and responses; generates one when absent (blueprint §21.2).</summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string Header = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers.TryGetValue(Header, out var v) &&
                            !string.IsNullOrWhiteSpace(v)
            ? v.ToString()
            : Guid.NewGuid().ToString();

        context.Items[Header] = correlationId;
        context.Response.Headers[Header] = correlationId;

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await next(context);
        }
    }
}

/// <summary>
/// Exception → ProblemDetails mapping (blueprint §22.2). The stable `code`
/// extension drives client-side messages; stack traces and SQL never cross
/// the wire.
/// </summary>
public sealed class ExceptionMappingMiddleware(
    RequestDelegate next, ILogger<ExceptionMappingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Client went away — nothing to report.
        }
        catch (NotFoundException ex)
        {
            await WriteProblemAsync(context, StatusCodes.Status404NotFound, ex.Code, ex.Message);
        }
        catch (BadHttpRequestException ex)
        {
            // Malformed query string or body: the caller's fault, not ours.
            await WriteProblemAsync(context, StatusCodes.Status400BadRequest,
                ErrorCodes.ValidationFailed, ex.Message);
        }
        catch (ConcurrencyException ex)
        {
            await WriteProblemAsync(context, StatusCodes.Status409Conflict, ex.Code, ex.Message);
        }
        catch (DomainException ex)
        {
            await WriteProblemAsync(context, StatusCodes.Status400BadRequest, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            var correlationId = context.Items[CorrelationIdMiddleware.Header] as string;
            logger.LogError(ex, "Unhandled exception (correlation {CorrelationId})", correlationId);
            await WriteProblemAsync(context, StatusCodes.Status500InternalServerError,
                ErrorCodes.Unexpected, "An unexpected error occurred.");
        }
    }

    private static Task WriteProblemAsync(HttpContext context, int status, string code, string detail)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        var problem = new ProblemDetails
        {
            Status = status,
            Title = code,
            Detail = detail,
            Extensions =
            {
                ["code"] = code,
                ["correlationId"] = context.Items[CorrelationIdMiddleware.Header] as string,
            },
        };
        return context.Response.WriteAsJsonAsync(problem);
    }
}
