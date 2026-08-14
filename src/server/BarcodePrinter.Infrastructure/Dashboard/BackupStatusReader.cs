using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BarcodePrinter.Infrastructure.Dashboard;

/// <summary>
/// Reads the status file written by <c>Backup-BarcodePrinter.ps1</c> (§16).
///
/// Backups are scheduled by Windows and run whether or not this application is
/// healthy — which is exactly when they matter. The application therefore only
/// OBSERVES them: it reports the age of the last successful backup and never
/// offers a restore, because a restore should not be one click away from a
/// logged-in administrator.
///
/// A missing or unreadable file is itself the finding ("no backup has ever been
/// recorded"), never an exception on the dashboard's path.
/// </summary>
public sealed class BackupStatusReader(
    IConfiguration configuration,
    IMemoryCache cache,
    ILogger<BackupStatusReader> logger)
{
    /// <summary>Blueprint §16. Two nights of silence is the point at which
    /// somebody must look, not the point at which data is lost.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(48);

    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(2);
    private const string CacheKey = "backup:status";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public BackupStatus Read()
    {
        // The dashboard auto-refreshes every 30 s for every user; re-reading the
        // file each time buys nothing when the writer runs nightly.
        if (cache.TryGetValue(CacheKey, out BackupStatus? cached) && cached is not null)
        {
            return cached;
        }

        var status = ReadUncached();
        cache.Set(CacheKey, status, CacheFor);
        return status;
    }

    private BackupStatus ReadUncached()
    {
        var path = configuration["Backup:StatusFile"];
        if (string.IsNullOrWhiteSpace(path))
        {
            // Development and test hosts have no backup schedule. Saying so is
            // honest; claiming a stale backup would be a false alarm.
            return BackupStatus.NotConfigured;
        }

        if (!File.Exists(path))
        {
            return BackupStatus.NeverRun;
        }

        try
        {
            var file = JsonSerializer.Deserialize<StatusFile>(File.ReadAllText(path), JsonOptions);
            if (file?.LastFullSuccessUtc is null)
            {
                return BackupStatus.NeverRun with { LastError = file?.LastError };
            }

            var lastSuccess = file.LastFullSuccessUtc.Value.ToUniversalTime();
            return new BackupStatus(
                Configured: true,
                LastSuccessUtc: lastSuccess,
                IsStale: DateTime.UtcNow - lastSuccess > StaleAfter,
                LastResult: file.LastResult,
                LastError: file.LastError,
                SizeBytes: file.LastFullBytes);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Being unable to read the status is not the same as there being no
            // backup, and it must not take the dashboard down with it.
            logger.LogWarning(ex, "Backup status file at {Path} could not be read", path);
            return BackupStatus.Unreadable with { LastError = ex.Message };
        }
    }

    private sealed class StatusFile
    {
        [JsonPropertyName("lastFullSuccessUtc")] public DateTime? LastFullSuccessUtc { get; set; }
        [JsonPropertyName("lastFullBytes")] public long? LastFullBytes { get; set; }
        [JsonPropertyName("lastResult")] public string? LastResult { get; set; }
        [JsonPropertyName("lastError")] public string? LastError { get; set; }
    }
}

public sealed record BackupStatus(
    bool Configured,
    DateTime? LastSuccessUtc,
    bool IsStale,
    string? LastResult,
    string? LastError,
    long? SizeBytes)
{
    public static readonly BackupStatus NotConfigured =
        new(false, null, false, null, null, null);

    /// <summary>Configured but nothing has ever succeeded — the worst state,
    /// because it looks like a working system right up until it is needed.</summary>
    public static readonly BackupStatus NeverRun =
        new(true, null, true, null, null, null);

    public static readonly BackupStatus Unreadable =
        new(true, null, true, "Unknown", null, null);
}
