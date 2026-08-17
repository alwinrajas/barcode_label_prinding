using BarcodePrinter.Domain;
using Dapper;
using MySqlConnector;

namespace BarcodePrinter.Infrastructure.Printing;

/// <summary>
/// Resolves which label template a print uses when the request does not name
/// one (§15 — operators never pick a template). Precedence: the product's
/// default, then the printer's default, then the global default template.
/// An explicitly requested id always wins, which keeps the API contract
/// backward-compatible for clients that still send one.
/// </summary>
public static class TemplateResolver
{
    public static async Task<long> ResolveAsync(
        MySqlConnection conn, long? requestedTemplateId, long productId, long? printerId,
        CancellationToken ct)
    {
        if (requestedTemplateId is > 0)
        {
            return requestedTemplateId.Value;
        }

        var resolved = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            """
            SELECT COALESCE(
                (SELECT CAST(t.id AS SIGNED) FROM products p
                 JOIN label_templates t ON t.id = p.default_template_id AND t.is_active = 1
                 WHERE p.id = @productId),
                (SELECT CAST(t.id AS SIGNED) FROM printers pr
                 JOIN label_templates t ON t.id = pr.default_template_id AND t.is_active = 1
                 WHERE pr.id = @printerId),
                (SELECT CAST(t.id AS SIGNED) FROM label_templates t
                 WHERE t.is_default = 1 AND t.is_active = 1
                 ORDER BY t.id LIMIT 1))
            """, new { productId, printerId }, cancellationToken: ct));

        return resolved
            ?? throw new DomainException("NO_TEMPLATE",
                "No label template is configured. Ask an administrator to set a default template.");
    }
}
