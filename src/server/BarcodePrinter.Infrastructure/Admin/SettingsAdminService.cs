using System.Text.Json;
using BarcodePrinter.Application.Abstractions;
using BarcodePrinter.Contracts;
using BarcodePrinter.Domain;
using BarcodePrinter.Infrastructure.Services;
using Dapper;
using Microsoft.Extensions.Caching.Memory;

namespace BarcodePrinter.Infrastructure.Admin;

/// <summary>
/// Settings writes with validation, cache invalidation and audit. Changing a
/// setting must take effect on the next operation without a restart, so the
/// cached entry is evicted on write (§23.2).
/// </summary>
public sealed class SettingsAdminService(
    IDbConnectionFactory connections, IAuditWriter audit, IMemoryCache cache)
{
    public async Task SaveAsync(
        IReadOnlyDictionary<string, string?> values, ActorInfo actor, CancellationToken ct)
    {
        if (values.Count == 0)
        {
            return;
        }

        await using var conn = await connections.OpenAsync(ct);

        var known = (await conn.QueryAsync<(string Key, string ValueType, bool IsSecret, string? Value)>(
            new CommandDefinition(
                "SELECT setting_key, value_type, is_secret, setting_value FROM app_settings WHERE scope = 'Global'",
                cancellationToken: ct)))
            .ToDictionary(r => r.Key, r => r, StringComparer.Ordinal);

        var changes = new Dictionary<string, object?>();
        await using var tx = await conn.BeginTransactionAsync(ct);

        foreach (var (key, value) in values)
        {
            if (!known.TryGetValue(key, out var existing))
            {
                throw new DomainException(ErrorCodes.ValidationFailed, $"Unknown setting '{key}'.");
            }
            // A secret submitted empty means "leave unchanged" — the UI never
            // receives the current value, so it cannot echo it back.
            if (existing.IsSecret && string.IsNullOrEmpty(value))
            {
                continue;
            }

            Validate(key, value, existing.ValueType);

            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE app_settings
                SET setting_value = @value, updated_at = UTC_TIMESTAMP(3), updated_by = @UserId
                WHERE setting_key = @key AND scope = 'Global'
                """, new { key, value, actor.UserId }, transaction: tx, cancellationToken: ct));

            changes[key] = existing.IsSecret ? "***" : value;
        }

        await tx.CommitAsync(ct);

        foreach (var key in changes.Keys)
        {
            cache.Remove($"setting:{key}");
        }

        if (changes.Count > 0)
        {
            await audit.WriteAsync(new AuditEntry("SettingsChanged", "Security",
                actor.UserId, actor.Username, "Settings", null,
                AfterJson: JsonSerializer.Serialize(changes),
                CorrelationId: actor.CorrelationId), ct);
        }
    }

    private static void Validate(string key, string? value, string valueType)
    {
        switch (valueType)
        {
            case "Int" when !int.TryParse(value, out _):
                throw new DomainException(ErrorCodes.ValidationFailed,
                    $"'{key}' must be a whole number.");
            case "Bool" when !bool.TryParse(value, out _):
                throw new DomainException(ErrorCodes.ValidationFailed,
                    $"'{key}' must be true or false.");
            case "Decimal" when !decimal.TryParse(value, out _):
                throw new DomainException(ErrorCodes.ValidationFailed,
                    $"'{key}' must be a number.");
        }

        // The feedback URL ends up encoded into every printed QR code, so a bad
        // value here is expensive to discover — validate at the point of entry.
        if (key == "Label:FeedbackFormUrl" && !string.IsNullOrWhiteSpace(value))
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https"))
            {
                throw new DomainException(ErrorCodes.ValidationFailed,
                    "The feedback form URL must be a full http(s) address.");
            }
        }

        if (key.EndsWith("Format", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(value))
        {
            try
            {
                _ = DateTime.UtcNow.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (FormatException)
            {
                throw new DomainException(ErrorCodes.ValidationFailed,
                    $"'{value}' is not a valid date/time format string.");
            }
        }
    }
}
