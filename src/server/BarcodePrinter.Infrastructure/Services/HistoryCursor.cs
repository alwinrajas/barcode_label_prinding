namespace BarcodePrinter.Infrastructure.Services;

/// <summary>
/// Keyset cursor for the <c>(requested_at DESC, id DESC)</c> ordering shared by
/// print history and the report detail views (§11.2 / B-12).
///
/// BOTH parts are required. Ids are not monotonic in <c>requested_at</c> — a
/// reprint of an old job, an Oracle backfill, or a clock correction all produce
/// a high id with an early timestamp — so a cursor of "id &lt; last id" both
/// skips rows the user never sees and re-serves rows they already saw, while
/// looking perfectly healthy. The pair is the only thing that identifies a
/// position in this ordering.
/// </summary>
public static class HistoryCursor
{
    public static string Encode(DateTime requestedAt, long id) => $"{requestedAt.Ticks}_{id}";

    public static bool TryDecode(string? cursor, out DateTime requestedAt, out long id)
    {
        requestedAt = default;
        id = 0;

        if (string.IsNullOrWhiteSpace(cursor))
        {
            return false;
        }

        var separator = cursor.IndexOf('_');
        if (separator <= 0 ||
            !long.TryParse(cursor[..separator], out var ticks) ||
            !long.TryParse(cursor[(separator + 1)..], out id) ||
            ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
        {
            id = 0;
            return false;
        }

        requestedAt = new DateTime(ticks, DateTimeKind.Utc);
        return true;
    }

    /// <summary>The SQL predicate for "strictly after this position" in a
    /// descending walk. The leading comparison is a plain range on
    /// <c>requested_at</c>, so the date index still drives the scan.</summary>
    public const string Predicate =
        " AND (j.requested_at < @afterAt OR (j.requested_at = @afterAt AND j.id < @afterId))";
}
