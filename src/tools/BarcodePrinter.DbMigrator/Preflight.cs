using System.Text;
using MySqlConnector;

namespace BarcodePrinter.DbMigrator;

/// <summary>
/// Server-level checks that must pass before any schema is created.
///
/// Every one of these produces a confusing downstream failure if it is left to
/// be discovered later:
///  * MariaDB accepts CREATE DATABASE and the first few scripts, then fails on
///    partitioning / ngram FULLTEXT with an error that points at a migration
///    script instead of at the wrong server.
///  * ngram_token_size is baked into the FULLTEXT index when it is built.
///    Getting it wrong here means product search silently finds nothing on
///    mid-code fragments, with no error anywhere.
///  * local_infile off breaks the bulk import path only when a user first
///    uploads a workbook — typically days after the install "succeeded".
///  * READ-COMMITTED is what stops concurrent carton allocation deadlocking.
/// </summary>
internal static class Preflight
{
    /// <summary>Returns null when the server is usable, or a description of every problem found.</summary>
    public static async Task<string?> RunAsync(string connectionString)
    {
        // Connect to the server, not to the database: at install time the
        // database usually does not exist yet, and "unknown database" would
        // mask the checks we actually came here to run.
        var serverOnly = new MySqlConnectionStringBuilder(connectionString) { Database = "" }.ConnectionString;

        await using var conn = new MySqlConnection(serverOnly);
        try
        {
            await conn.OpenAsync();
        }
        catch (MySqlException ex)
        {
            var target = new MySqlConnectionStringBuilder(connectionString) { Password = "***" };
            return $"cannot connect to {target.Server}:{target.Port} as '{target.UserID}' — {ex.Message}";
        }

        var problems = new List<string>();

        var version = await ScalarAsync(conn, "SELECT VERSION()") ?? "";
        Console.WriteLine($"Server version: {version}");

        if (version.Contains("MariaDB", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add(
                $"the server is MariaDB ({version}), not MySQL. The schema uses MySQL 8 table " +
                "partitioning, ngram FULLTEXT and utf8mb4_0900_ai_ci, none of which MariaDB has. " +
                "Install MySQL 8.4 Community Server and point the connection string at it. " +
                "(XAMPP and WAMP ship MariaDB under the name 'MySQL' — check, do not assume.)");
        }
        else if (!Version.TryParse(MajorMinor(version), out var parsed) || parsed < new Version(8, 0))
        {
            problems.Add($"MySQL 8.0 or later is required; this server reports '{version}'.");
        }
        else
        {
            // Only worth checking on a server that is actually MySQL 8.
            await CheckVariableAsync(conn, problems, "ngram_token_size", "2",
                "product search would be built against the wrong token size and would silently " +
                "fail to match mid-code fragments. It CANNOT be corrected after the FULLTEXT " +
                "index is built without rebuilding it.");

            await CheckVariableAsync(conn, problems, "local_infile", "ON",
                "the bulk product import runs on LOAD DATA LOCAL INFILE and would fail.");

            await CheckVariableAsync(conn, problems, "transaction_isolation", "READ-COMMITTED",
                "concurrent carton-number allocation deadlocks under REPEATABLE-READ.");
        }

        if (problems.Count == 0)
        {
            return null;
        }

        var report = new StringBuilder();
        report.AppendLine($"{problems.Count} problem(s) with the MySQL server:");
        foreach (var problem in problems)
        {
            report.AppendLine($"  - {problem}");
        }
        report.AppendLine();
        report.AppendLine("Settings live in my.ini under [mysqld]; MySQL must be restarted after changing them:");
        report.AppendLine("    ngram_token_size      = 2");
        report.AppendLine("    local_infile          = 1");
        report.AppendLine("    transaction_isolation = READ-COMMITTED");
        report.AppendLine("The deploy\\mysql\\barcodeprinter.cnf file in this package carries the full set.");
        return report.ToString();
    }

    /// <summary>
    /// True when the target database already exists and the application user can
    /// open it.
    ///
    /// This exists so the migrator does not have to ask DbUp's EnsureDatabase.
    /// EnsureDatabase connects to the server's `mysql` schema to decide whether
    /// to create the database, and the account the runbook provisions is granted
    /// rights on its OWN schema only — so that check is refused with "Access
    /// denied ... to database 'mysql'" even when the database is sitting right
    /// there. The database is created by hand during MySQL setup; creating it is
    /// not this tool's job when it already exists.
    /// </summary>
    public static async Task<bool> DatabaseExistsAsync(string connectionString)
    {
        await using var conn = new MySqlConnection(connectionString);
        try
        {
            await conn.OpenAsync();
            return true;
        }
        catch (MySqlException ex) when (
            ex.ErrorCode == MySqlErrorCode.UnknownDatabase ||
            ex.ErrorCode == MySqlErrorCode.DatabaseAccessDenied)
        {
            // MySQL deliberately does not tell an unprivileged account whether a
            // database exists: a missing database and one it simply has no grant
            // on both come back as 1044 "access denied". Either way this account
            // cannot use it, and the caller's message covers both causes.
            return false;
        }
    }

    private static async Task CheckVariableAsync(
        MySqlConnection conn, List<string> problems, string name, string expected, string consequence)
    {
        var actual = await ScalarAsync(conn, $"SELECT @@{name}");

        // MySQL reports booleans as 0/1 and enums as text depending on the
        // variable, so compare both spellings rather than trusting one.
        var normalised = actual switch
        {
            "1" => "ON",
            "0" => "OFF",
            _ => actual,
        };

        if (!string.Equals(normalised, expected, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add($"{name} is '{actual}', expected '{expected}' — {consequence}");
        }
    }

    private static async Task<string?> ScalarAsync(MySqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (await cmd.ExecuteScalarAsync())?.ToString();
    }

    /// <summary>"8.4.0-log" -> "8.4.0"; version strings carry suffixes that Version.TryParse rejects.</summary>
    private static string MajorMinor(string version)
    {
        var parts = version.Split('-')[0].Split('.');
        return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : version;
    }
}
