namespace BarcodePrinter.Domain;

/// <summary>
/// Business-rule violation carrying a stable error code (blueprint §22.2).
/// The exception middleware maps it to 400/ProblemDetails; the code — never
/// the message — drives the client's user-facing text.
/// </summary>
public class DomainException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class NotFoundException(string entity, object key)
    : DomainException("NOT_FOUND", $"{entity} '{key}' was not found.");

/// <summary>Optimistic-concurrency conflict → 409; the client offers
/// Reload / View-their-changes, never a silent overwrite (§11.1 Rev A).</summary>
public sealed class ConcurrencyException(string entity)
    : DomainException("CONCURRENCY_CONFLICT",
        $"This {entity} was changed by another user while you were editing.");
