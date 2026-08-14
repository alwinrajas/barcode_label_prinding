using System.Collections.Concurrent;
using BarcodePrinter.Application.Printing;
using BarcodePrinter.Infrastructure.Services;
using Dapper;
using MySqlConnector;

namespace BarcodePrinter.Infrastructure.Printing;

/// <summary>
/// Atomic block allocation (blueprint §8.4). Two operators must never receive
/// the same carton number.
///
/// Lock discipline — this is the part that matters, and it was proven by the
/// 20-way concurrency test:
///
///   * The sequence row is CREATED in its own short transaction, on a separate
///     connection, before the job transaction touches it.
///   * The job transaction then does a pure UPDATE, which takes an exclusive
///     lock directly.
///
/// Both `INSERT IGNORE + SELECT … FOR UPDATE` and `INSERT … ON DUPLICATE KEY
/// UPDATE` deadlock here: each takes a SHARED lock for duplicate detection and
/// then upgrades to exclusive, so two concurrent allocations for the same scope
/// wait on each other forever. Do not "simplify" this back to a single upsert.
/// </summary>
public sealed class CartonSequenceAllocator(IDbConnectionFactory connections) : ICartonSequenceAllocator
{
    /// <summary>Scopes already known to exist. Bounded by the number of live
    /// products/batches, and only ever avoids a round trip.</summary>
    private readonly ConcurrentDictionary<string, byte> _known = new();

    public async Task<long> ReserveAsync(
        string scopeKey, string strategyCode, int count,
        System.Data.Common.DbTransaction tx, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        var conn = tx.Connection as MySqlConnection
            ?? throw new InvalidOperationException("Transaction has no MySQL connection.");

        await EnsureRowExistsAsync(scopeKey, strategyCode, ct);

        var updated = await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE carton_sequences
            SET current_value = current_value + @count, updated_at = UTC_TIMESTAMP(3)
            WHERE scope_key = @scopeKey
            """, new { scopeKey, count }, transaction: tx, cancellationToken: ct));

        if (updated == 0)
        {
            // Row vanished between ensure and update (manual cleanup); recreate
            // and retry once rather than failing the operator's print.
            _known.TryRemove(scopeKey, out _);
            await EnsureRowExistsAsync(scopeKey, strategyCode, ct);
            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE carton_sequences
                SET current_value = current_value + @count, updated_at = UTC_TIMESTAMP(3)
                WHERE scope_key = @scopeKey
                """, new { scopeKey, count }, transaction: tx, cancellationToken: ct));
        }

        // Reads our own uncommitted increment while holding the exclusive lock.
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT current_value FROM carton_sequences WHERE scope_key = @scopeKey",
            new { scopeKey }, transaction: tx, cancellationToken: ct));
    }

    /// <summary>Creates the sequence row on its own connection so the lock is
    /// released immediately and never joins the job transaction's lock set.</summary>
    private async Task EnsureRowExistsAsync(string scopeKey, string strategyCode, CancellationToken ct)
    {
        if (_known.ContainsKey(scopeKey))
        {
            return;
        }

        await using var conn = await connections.OpenAsync(ct);
        try
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO carton_sequences (scope_key, strategy_code, current_value, updated_at)
                VALUES (@scopeKey, @strategyCode, 0, UTC_TIMESTAMP(3))
                """, new { scopeKey, strategyCode }, cancellationToken: ct));
        }
        catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            // Another request created it first — exactly what we wanted.
        }
        _known.TryAdd(scopeKey, 0);
    }
}
