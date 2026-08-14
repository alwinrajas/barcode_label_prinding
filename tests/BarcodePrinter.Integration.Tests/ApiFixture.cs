extern alias migrator;
using DbUp;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Testcontainers.MySql;
using Xunit;

namespace BarcodePrinter.Integration.Tests;

/// <summary>
/// One MySQL 8.4 container per test collection, configured exactly like the
/// deployment runbook (READ-COMMITTED, local_infile, ngram), migrated with the
/// REAL DbUp scripts, seeded with one user per role.
/// </summary>
public sealed class ApiFixture : IAsyncLifetime
{
    public const string AdminPassword = "Admin@Test1!";
    public const string ManagerPassword = "Manager@Test1!";
    public const string UserPassword = "User@Test1!";

    private readonly MySqlContainer _mysql = new MySqlBuilder()
        .WithImage("mysql:8.4")
        .WithDatabase("barcodeprinter")
        .WithUsername("root")
        .WithPassword("testroot")
        .WithCommand(
            "--local-infile=1",
            "--transaction-isolation=READ-COMMITTED",
            "--ngram-token-size=2",
            "--character-set-server=utf8mb4")
        .Build();

    public string ConnectionString { get; private set; } = string.Empty;
    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public HttpClient CreateClient() => Factory.CreateClient();

    public async Task InitializeAsync()
    {
        await _mysql.StartAsync();
        ConnectionString = _mysql.GetConnectionString() + ";AllowLoadLocalInfile=true";

        // Real production scripts — never a lookalike schema.
        var upgrade = DeployChanges.To
            .MySqlDatabase(ConnectionString)
            .WithScriptsEmbeddedInAssembly(migrator::BarcodePrinter.DbMigrator.MigratorScripts.Assembly)
            .WithTransactionPerScript()
            .Build()
            .PerformUpgrade();
        if (!upgrade.Successful)
        {
            throw new InvalidOperationException($"Migration failed: {upgrade.Error}");
        }

        await SeedUserAsync("it-admin", AdminPassword, "Admin");
        await SeedUserAsync("it-manager", ManagerPassword, "Manager");
        await SeedUserAsync("it-user", UserPassword, "User");

        // UseSetting (not ConfigureAppConfiguration): with minimal hosting the
        // entry point reads builder.Configuration inline before Build, and only
        // host settings are guaranteed to be visible there.
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("environment", "Testing");
            builder.UseSetting("ConnectionStrings:BarcodePrinter", ConnectionString);
            builder.UseSetting("Jwt:Issuer", "BarcodePrinter");
            builder.UseSetting("Jwt:Audience", "BarcodePrinter");
            builder.UseSetting("Jwt:SigningKey", "integration-test-signing-key-0123456789abcdef");
            builder.UseSetting("Jwt:AccessTokenMinutes", "15");
            builder.UseSetting("MinimumClientVersion", "1.0.0");
            // All WAF clients share one IP partition; production default (10/min)
            // would rate-limit the suite itself.
            builder.UseSetting("RateLimit:LoginPerMinute", "1000");
        });
    }

    /// <summary>Evicts the cached security stamp so revocation tests do not
    /// have to wait out the 60 s cache window.</summary>
    public void EvictSecurityStamp(long userId)
    {
        var cache = Factory.Services.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>();
        cache.Remove($"sstamp:{userId}");
    }

    public async Task<MySqlConnection> OpenDbAsync()
    {
        var conn = new MySqlConnection(ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    private async Task SeedUserAsync(string username, string password, string roleCode)
    {
        var hash = new PasswordHasher<object>().HashPassword(null!, password);
        await using var conn = await OpenDbAsync();
        await using var cmd = new MySqlCommand(
            """
            INSERT INTO users (username, full_name, password_hash, security_stamp,
                               is_active, must_change_password, concurrency_stamp, created_at)
            VALUES (@u, @u, @h, UUID(), 1, 0, UUID(), UTC_TIMESTAMP(3));
            INSERT INTO user_roles (user_id, role_id)
            SELECT u.id, r.id FROM users u JOIN roles r ON r.code = @r WHERE u.username = @u;
            """, conn);
        cmd.Parameters.AddWithValue("@u", username);
        cmd.Parameters.AddWithValue("@h", hash);
        cmd.Parameters.AddWithValue("@r", roleCode);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (Factory is not null)
        {
            await Factory.DisposeAsync();
        }
        await _mysql.DisposeAsync().AsTask();
    }
}

[CollectionDefinition("api")]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>;
