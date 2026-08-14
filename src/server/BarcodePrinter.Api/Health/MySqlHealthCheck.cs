using Microsoft.Extensions.Diagnostics.HealthChecks;
using MySqlConnector;

namespace BarcodePrinter.Api.Health;

/// <summary>
/// Connectivity check plus the blueprint's deployment-config assertions (R15):
/// the import pipeline (MySqlBulkCopy → LOAD DATA LOCAL INFILE) and the
/// concurrency design (READ-COMMITTED) and product search (ngram_token_size=2)
/// silently misbehave if the server is misconfigured, so this fails loudly at
/// deployment time instead.
/// </summary>
public sealed class MySqlHealthCheck(IConfiguration configuration) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var connectionString = configuration.GetConnectionString("BarcodePrinter");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return HealthCheckResult.Unhealthy("Connection string 'BarcodePrinter' is not configured.");
        }

        try
        {
            await using var conn = new MySqlConnection(connectionString);
            await conn.OpenAsync(cancellationToken);

            var problems = new List<string>();

            await using (var cmd = new MySqlCommand(
                """
                SELECT @@local_infile, @@transaction_isolation, @@ngram_token_size,
                       @@character_set_server
                """, conn))
            await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
            {
                await reader.ReadAsync(cancellationToken);

                if (reader.GetInt32(0) != 1)
                    problems.Add("local_infile is OFF — bulk Excel import (MySqlBulkCopy) will fail (R15).");

                var isolation = reader.GetString(1);
                if (!string.Equals(isolation, "READ-COMMITTED", StringComparison.OrdinalIgnoreCase))
                    problems.Add($"transaction_isolation is {isolation}; blueprint requires READ-COMMITTED (§9 concurrency).");

                if (reader.GetInt32(2) != 2)
                    problems.Add($"ngram_token_size is {reader.GetInt32(2)}; product substring search requires 2 (§9.3).");

                var charset = reader.GetString(3);
                if (!charset.StartsWith("utf8mb4", StringComparison.OrdinalIgnoreCase))
                    problems.Add($"character_set_server is {charset}; expected utf8mb4.");
            }

            return problems.Count == 0
                ? HealthCheckResult.Healthy("MySQL reachable; deployment configuration verified.")
                : HealthCheckResult.Degraded(string.Join(" | ", problems));
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("MySQL unreachable.", ex);
        }
    }
}
