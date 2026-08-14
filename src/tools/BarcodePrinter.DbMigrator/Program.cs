using System.Reflection;
using BarcodePrinter.DbMigrator;
using DbUp;
using Microsoft.AspNetCore.Identity;
using MySqlConnector;

// ============================================================================
// BarcodePrinter.DbMigrator — applies the versioned SQL schema (DbUp) and
// seeds the initial admin user.
//
// Blueprint rules this tool embodies:
//  * The schema is owned by these scripts, never by the ORM (B-8 / R14).
//  * Migrations run as an explicit, logged deployment step — never
//    automatically at API startup (§16 deployment).
//  * Idempotent: DbUp journals executed scripts in `schemaversions`;
//    re-running is a no-op.
//
// Usage:
//   dotnet run -- "<connection string>"
//   or set BARCODEPRINTER_CONNECTIONSTRING
//   Optional: --seed-admin-password <pwd>  (default Admin@123!, forced change on first login)
// ============================================================================

var connectionString =
    args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))
    ?? Environment.GetEnvironmentVariable("BARCODEPRINTER_CONNECTIONSTRING")
    ?? "Server=127.0.0.1;Port=3306;Database=barcodeprinter;Uid=root;Pwd=;AllowLoadLocalInfile=true";

var adminPassword = "Admin@123!";
var pwdFlag = Array.IndexOf(args, "--seed-admin-password");
if (pwdFlag >= 0 && pwdFlag + 1 < args.Length)
{
    adminPassword = args[pwdFlag + 1];
}

Console.WriteLine("BarcodePrinter.DbMigrator");
Console.WriteLine($"Target: {new MySqlConnectionStringBuilder(connectionString) { Password = "***" }}");

// ---------------------------------------------------------------------------
// Preflight. This runs BEFORE EnsureDatabase, because every check below fails
// in a way that is hard to read once a database and half a schema exist:
// MariaDB accepts CREATE DATABASE happily and only chokes later on the
// partitioning and ngram FULLTEXT, by which point the error points at a
// migration script rather than at the real problem (the wrong server).
// --preflight-only lets the installer run this as a cheap gate before it
// creates accounts, certificates and services.
// ---------------------------------------------------------------------------
var preflightOnly = args.Contains("--preflight-only", StringComparer.Ordinal);
if (await Preflight.RunAsync(connectionString) is { } preflightError)
{
    Console.Error.WriteLine($"PREFLIGHT FAILED: {preflightError}");
    return 2;
}
if (preflightOnly)
{
    Console.WriteLine("Preflight passed.");
    return 0;
}

// Only create the database when it is genuinely absent. EnsureDatabase reaches
// into the server's `mysql` schema to make that decision, which the account the
// runbook provisions (rights on its own schema only, §2) is refused — so calling
// it unconditionally fails every least-privilege deployment, database present or
// not.
if (await Preflight.DatabaseExistsAsync(connectionString))
{
    Console.WriteLine("Database exists.");
}
else
{
    var target = new MySqlConnectionStringBuilder(connectionString);
    Console.WriteLine($"Database '{target.Database}' does not exist; creating it.");
    try
    {
        EnsureDatabase.For.MySqlDatabase(connectionString);
    }
    catch (MySqlException ex)
    {
        Console.Error.WriteLine(
            $"Cannot use database '{target.Database}' as '{target.UserID}': {ex.Message}\n" +
            "MySQL reports the same 'access denied' whether the database is missing or the\n" +
            "account simply has no grant on it, so check both. As an administrator:\n" +
            $"  CREATE DATABASE IF NOT EXISTS {target.Database} CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;\n" +
            "  GRANT SELECT, INSERT, UPDATE, DELETE, CREATE, ALTER, INDEX, DROP, REFERENCES\n" +
            $"    ON {target.Database}.* TO '{target.UserID}'@'{target.Server}';\n" +
            "  FLUSH PRIVILEGES;");
        return 3;
    }
}

var upgrader = DeployChanges.To
    .MySqlDatabase(connectionString)
    .WithScriptsEmbeddedInAssembly(Assembly.GetExecutingAssembly())
    .WithTransactionPerScript()
    .LogToConsole()
    .Build();

var result = upgrader.PerformUpgrade();
if (!result.Successful)
{
    Console.Error.WriteLine($"MIGRATION FAILED: {result.Error}");
    return 1;
}

Console.WriteLine("Schema is up to date.");

// ---------------------------------------------------------------------------
// Seed the initial admin user (C# because password hashing is not a SQL job).
// PasswordHasher<T> = PBKDF2-HMAC-SHA512, versioned format (B-11).
// Only runs when the users table is empty, so it never touches a live system.
// ---------------------------------------------------------------------------
await using (var conn = new MySqlConnection(connectionString))
{
    await conn.OpenAsync();

    await using (var check = new MySqlCommand("SELECT COUNT(*) FROM users", conn))
    {
        var userCount = Convert.ToInt64(await check.ExecuteScalarAsync());
        if (userCount > 0)
        {
            Console.WriteLine("Users already exist — admin seed skipped.");
            return 0;
        }
    }

    var hasher = new PasswordHasher<object>();
    var hash = hasher.HashPassword(null!, adminPassword);

    await using var tx = await conn.BeginTransactionAsync();

    await using (var insert = new MySqlCommand(
        """
        INSERT INTO users (username, full_name, password_hash, security_stamp,
                           is_active, must_change_password, concurrency_stamp, created_at)
        VALUES (@u, @n, @h, @ss, 1, 1, @cs, UTC_TIMESTAMP(3));
        """, conn, tx))
    {
        insert.Parameters.AddWithValue("@u", "admin");
        insert.Parameters.AddWithValue("@n", "Administrator");
        insert.Parameters.AddWithValue("@h", hash);
        insert.Parameters.AddWithValue("@ss", Guid.NewGuid().ToString());
        insert.Parameters.AddWithValue("@cs", Guid.NewGuid().ToString());
        await insert.ExecuteNonQueryAsync();
    }

    await using (var link = new MySqlCommand(
        """
        INSERT INTO user_roles (user_id, role_id)
        SELECT u.id, r.id FROM users u JOIN roles r ON r.code = 'Admin'
        WHERE u.username = 'admin';
        """, conn, tx))
    {
        await link.ExecuteNonQueryAsync();
    }

    await tx.CommitAsync();
}

Console.WriteLine("Seeded initial user 'admin' (password change forced on first login).");
return 0;
