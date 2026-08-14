using BarcodePrinter.Infrastructure.Dashboard;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BarcodePrinter.Application.Tests;

/// <summary>
/// The backup warning is the only thing standing between "backups stopped two
/// weeks ago" and finding out during a recovery, so every state it can report
/// is pinned here — especially the ones that must NOT raise a false alarm.
/// </summary>
public class BackupStatusReaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bp-backup-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void A_recent_successful_backup_is_not_stale()
    {
        var status = Read(WriteStatus($$"""
            {
              "lastFullSuccessUtc": "{{DateTime.UtcNow.AddHours(-6):o}}",
              "lastResult": "Success",
              "lastFullBytes": 5242880
            }
            """));

        status.Configured.Should().BeTrue();
        status.IsStale.Should().BeFalse();
        status.SizeBytes.Should().Be(5_242_880);
    }

    [Fact]
    public void A_backup_older_than_48_hours_is_stale()
    {
        var status = Read(WriteStatus($$"""
            {"lastFullSuccessUtc": "{{DateTime.UtcNow.AddHours(-49):o}}", "lastResult": "Success"}
            """));

        status.IsStale.Should().BeTrue("§16 sets the warning threshold at 48 hours");
    }

    [Fact]
    public void Exactly_at_the_threshold_is_not_yet_stale()
    {
        Read(WriteStatus($$"""
            {"lastFullSuccessUtc": "{{DateTime.UtcNow.AddHours(-47.5):o}}", "lastResult": "Success"}
            """)).IsStale.Should().BeFalse();
    }

    /// <summary>The most dangerous state: scheduled, never succeeded. It looks
    /// like a working system right up to the moment it is needed.</summary>
    [Fact]
    public void A_configured_backup_that_never_succeeded_is_stale()
    {
        var status = Read(WriteStatus("""
            {"lastRunUtc": "2026-08-13T01:30:00Z", "lastResult": "Failed",
             "lastError": "mysqldump exited with 2."}
            """));

        status.Configured.Should().BeTrue();
        status.IsStale.Should().BeTrue();
        status.LastSuccessUtc.Should().BeNull();
        status.LastError.Should().Contain("mysqldump");
    }

    [Fact]
    public void A_missing_status_file_means_no_backup_has_ever_run()
    {
        var status = Read(Path.Combine(_dir, "does-not-exist.json"));

        status.Configured.Should().BeTrue();
        status.IsStale.Should().BeTrue();
        status.LastSuccessUtc.Should().BeNull();
    }

    /// <summary>Development and test hosts have no backup schedule. Warning
    /// there would train everyone to ignore the warning.</summary>
    [Fact]
    public void No_configured_path_reports_not_configured_and_raises_nothing()
    {
        var status = Read(path: null);

        status.Configured.Should().BeFalse();
        status.IsStale.Should().BeFalse();
    }

    /// <summary>A corrupt file must not take the dashboard down — it is read on
    /// the landing page of every user at shift start.</summary>
    [Fact]
    public void A_corrupt_status_file_degrades_instead_of_throwing()
    {
        var status = Read(WriteStatus("{ this is not json"));

        status.Configured.Should().BeTrue();
        status.IsStale.Should().BeTrue("an unreadable status is not evidence of a good backup");
        status.LastSuccessUtc.Should().BeNull();
    }

    [Fact]
    public void The_status_is_cached_so_the_file_is_not_read_on_every_dashboard_refresh()
    {
        var path = WriteStatus($$"""
            {"lastFullSuccessUtc": "{{DateTime.UtcNow.AddHours(-1):o}}", "lastResult": "Success"}
            """);
        var reader = Build(path);

        var first = reader.Read();
        File.Delete(path);
        var second = reader.Read();

        second.Should().BeEquivalentTo(first, "the second call must come from cache");
    }

    private string WriteStatus(string json)
    {
        var path = Path.Combine(_dir, $"backup-status-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static BackupStatus Read(string? path) => Build(path).Read();

    private static BackupStatusReader Build(string? path)
    {
        var settings = new Dictionary<string, string?>();
        if (path is not null)
        {
            settings["Backup:StatusFile"] = path;
        }

        return new BackupStatusReader(
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<BackupStatusReader>.Instance);
    }
}
