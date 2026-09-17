using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Rcs.Infrastructure.Migrations;

namespace Rcs.IntegrationTests.TestSupport;

/// <summary>
/// The PostgreSQL server the integration tests use. It must be a disposable DEVELOPMENT cluster, never a
/// production one: the tests create cluster roles and create and drop databases.
/// </summary>
internal static class PostgresTestServer
{
    public const string AdminConnectionVariable = "RCS_TEST_ADMIN_CONNECTION";

    private static readonly SemaphoreSlim RolesGate = new(1, 1);
    private static bool rolesEnsured;

    /// <summary>A superuser connection string to the development test cluster.</summary>
    public static string AdminConnectionString =>
        Environment.GetEnvironmentVariable(AdminConnectionVariable) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"Integration tests need a real PostgreSQL 17+ development server. Set {AdminConnectionVariable} to a superuser connection string for a disposable development cluster (never production), e.g. \"Host=localhost;Port=5432;Username=postgres;Password=...\". See README.md.");

    /// <summary>Runs the real database/roles/roles.sql once per test run.</summary>
    public static async Task EnsureRolesAsync()
    {
        await RolesGate.WaitAsync();
        try
        {
            if (rolesEnsured)
            {
                return;
            }

            await ExecuteAdminScriptAsync("postgres", await File.ReadAllTextAsync(RepositoryRoot.Combine("database", "roles", "roles.sql")));
            rolesEnsured = true;
        }
        finally
        {
            RolesGate.Release();
        }
    }

    public static async Task ExecuteAdminScriptAsync(string database, string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString(database));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// A connection to <paramref name="database"/>. With <paramref name="role"/>, the session runs as that role
    /// (PostgreSQL's <c>role</c> startup option), so every privilege check is the role's own — without the
    /// tests needing the role's password.
    /// </summary>
    public static string ConnectionString(string database, string? role = null)
    {
        var builder = new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = database };
        builder.Options = role is null ? null : $"-c role={role}";
        return builder.ConnectionString;
    }
}

/// <summary>A uniquely named database for one test, dropped afterwards.</summary>
internal sealed class TestDatabase : IAsyncDisposable
{
    private TestDatabase(string name) => Name = name;

    public string Name { get; }

    /// <summary>Superuser connection — for inspection only, never to exercise the application's privileges.</summary>
    public string AdminConnectionString => PostgresTestServer.ConnectionString(Name);

    public string MigrationConnectionString => PostgresTestServer.ConnectionString(Name, "rcs_migrate");

    public string RuntimeConnectionString => PostgresTestServer.ConnectionString(Name, "rcs_app");

    /// <summary>
    /// Creates the database exactly like database/dev/create_dev_database.sql: owned by rcs_migrate, builtin
    /// C.UTF-8 locale. That locale is a development convenience and NOT the production collation decision (PS-1).
    /// </summary>
    public static async Task<TestDatabase> CreateAsync(bool ownedByMigrationRole = true)
    {
        await PostgresTestServer.EnsureRolesAsync();

        var name = $"rcs_it_{DateTime.UtcNow:yyyyMMddHHmmss}_{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}";
        var owner = ownedByMigrationRole ? "OWNER rcs_migrate" : string.Empty;
        await PostgresTestServer.ExecuteAdminScriptAsync(
            "postgres",
            $"CREATE DATABASE \"{name}\" {owner} TEMPLATE template0 ENCODING 'UTF8' LOCALE 'C' LOCALE_PROVIDER builtin BUILTIN_LOCALE 'C.UTF-8'");

        return new TestDatabase(name);
    }

    public async Task<MigrationRunResult> MigrateAsync(MigrationSet? release = null, TimeSpan? lockTimeout = null) =>
        await new MigrationRunner(NullLogger<MigrationRunner>.Instance).RunAsync(
            MigrationConnectionString,
            release ?? MigrationSet.LoadEmbedded(),
            new MigrationRunnerOptions { LockTimeout = lockTimeout ?? TimeSpan.FromSeconds(30) });

    public async Task<T> ScalarAsync<T>(string sql, string? connectionString = null)
    {
        await using var connection = new NpgsqlConnection(connectionString ?? AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default! : (T)value;
    }

    public async Task ExecuteAsync(string sql, string? connectionString = null)
    {
        await using var connection = new NpgsqlConnection(connectionString ?? AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await PostgresTestServer.ExecuteAdminScriptAsync("postgres", $"DROP DATABASE IF EXISTS \"{Name}\" WITH (FORCE)");
    }
}

/// <summary>Builds release variants from the real migration files plus test-only additions.</summary>
internal static class Releases
{
    public static IReadOnlyList<(string FileName, string Content)> RealFiles() =>
        Directory.GetFiles(RepositoryRoot.Combine("database", "migrations"), "*.sql")
            .Order(StringComparer.Ordinal)
            .Select(path => (Path.GetFileName(path), File.ReadAllText(path)))
            .ToArray();

    public static MigrationSet RealPlus(params (string FileName, string Content)[] extra) =>
        MigrationSet.Create(RealFiles().Concat(extra));

    public static MigrationSet WithRealFileEdited(string fileName, Func<string, string> edit) =>
        MigrationSet.Create(RealFiles().Select(file => file.FileName == fileName ? (file.FileName, edit(file.Content)) : file));

    public static (string FileName, string Content) Migration(int id, string name, string sql, bool transactional = true) =>
        ($"{id:D4}_{name}.sql", $"-- description: test-only {name}\n{(transactional ? string.Empty : "-- transactional: false\n")}{sql}\n");
}

internal static class RepositoryRoot
{
    private static readonly Lazy<string> Root = new(() =>
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Rcs.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Rcs.sln was not found above the test output directory.");
    });

    public static string Combine(params string[] parts) => Path.Combine([Root.Value, .. parts]);
}
