using Npgsql;
using Rcs.IntegrationTests.TestSupport;

namespace Rcs.IntegrationTests.Database;

public sealed class DatabaseSecurityTests : IAsyncLifetime
{
    private TestDatabase database = null!;

    public async ValueTask InitializeAsync() => database = await TestDatabase.CreateAsync();

    public async ValueTask DisposeAsync() => await database.DisposeAsync();

    [Fact]
    public async Task RoleScriptIsIdempotentAndNoApplicationRoleIsPrivileged()
    {
        // Re-running the real script must succeed and re-assert the attributes.
        await PostgresTestServer.ExecuteAdminScriptAsync("postgres", await File.ReadAllTextAsync(RepositoryRoot.Combine("database", "roles", "roles.sql")));

        const string sql = """
            SELECT count(*) FROM pg_roles
            WHERE rolname IN ('rcs_migrate', 'rcs_app', 'rcs_backup')
              AND (rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls)
            """;
        Assert.Equal(0L, await database.ScalarAsync<long>(sql));
        Assert.True(await database.ScalarAsync<bool>("SELECT rolcanlogin FROM pg_roles WHERE rolname = 'rcs_app'"));
        Assert.False(await database.ScalarAsync<bool>("SELECT rolcanlogin FROM pg_roles WHERE rolname = 'rcs_backup'"));
        Assert.False(await database.ScalarAsync<bool>("SELECT pg_has_role('rcs_app', 'rcs_migrate', 'MEMBER')"));
    }

    [Fact]
    public async Task BtreeGistIsInstalledOnlyThroughTheMigrationAndPgTrgmIsNot()
    {
        Assert.Equal(0L, await database.ScalarAsync<long>("SELECT count(*) FROM pg_extension WHERE extname = 'btree_gist'"));

        await database.MigrateAsync();

        Assert.Equal("rcs", await database.ScalarAsync<string>("SELECT extnamespace::regnamespace::text FROM pg_extension WHERE extname = 'btree_gist'"));
        Assert.Equal("rcs_migrate", await database.ScalarAsync<string>("SELECT extowner::regrole::text FROM pg_extension WHERE extname = 'btree_gist'"));
        Assert.Equal(0L, await database.ScalarAsync<long>("SELECT count(*) FROM pg_extension WHERE extname = 'pg_trgm'"));
    }

    [Fact]
    public async Task BtreeGistSupportsTheHalfOpenPerScopeExclusionThatAssignmentsWillUse()
    {
        await database.MigrateAsync();
        await using var connection = new NpgsqlConnection(database.MigrationConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        async Task Execute(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            await command.ExecuteNonQueryAsync();
        }

        // Capability probe only (DOMAIN_MODEL.md §2.7 shape); rolled back, never a real table.
        await Execute("""
            CREATE TABLE rcs.it_exclusion_probe (
                scope_id uuid NOT NULL,
                valid_from timestamptz NOT NULL,
                valid_until timestamptz,
                CHECK (valid_until IS NULL OR valid_until > valid_from),
                EXCLUDE USING gist (scope_id WITH =, tstzrange(valid_from, valid_until, '[)') WITH &&)
            )
            """);
        await Execute("INSERT INTO rcs.it_exclusion_probe VALUES ('01923b6e-5c7a-7d1e-9a4b-3c2d1e0f9a8b', '2026-01-01T00:00:00Z', '2026-02-01T00:00:00Z')");
        await Execute("INSERT INTO rcs.it_exclusion_probe VALUES ('01923b6e-5c7a-7d1e-9a4b-3c2d1e0f9a8b', '2026-02-01T00:00:00Z', NULL)"); // handover at the same instant

        await Execute("SAVEPOINT overlap");
        var overlap = await Assert.ThrowsAsync<PostgresException>(() =>
            Execute("INSERT INTO rcs.it_exclusion_probe VALUES ('01923b6e-5c7a-7d1e-9a4b-3c2d1e0f9a8b', '2026-01-15T00:00:00Z', '2026-01-20T00:00:00Z')"));
        Assert.Equal(PostgresErrorCodes.ExclusionViolation, overlap.SqlState);

        await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData("CREATE TABLE rcs.it_runtime_table (id integer)")]
    [InlineData("INSERT INTO rcs.schema_migration (migration_id, name, description, checksum_sha256, transactional, execution_ms, runner_version) VALUES (99, 'x', 'x', repeat('0', 64), true, 0, 'x')")]
    [InlineData("UPDATE rcs.schema_migration SET description = 'changed'")]
    [InlineData("DELETE FROM rcs.schema_migration")]
    [InlineData("CREATE EXTENSION pg_trgm WITH SCHEMA rcs")]
    [InlineData("CREATE TEMP TABLE it_runtime_temp (id integer)")]
    [InlineData("CREATE SCHEMA it_runtime_schema")]
    public async Task RuntimeRoleCannotChangeTheSchemaOrMigrationHistory(string sql)
    {
        await database.MigrateAsync();

        var denied = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(sql, database.RuntimeConnectionString));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
    }

    [Fact]
    public async Task RuntimeRoleCanReadMigrationHistory()
    {
        await database.MigrateAsync();
        Assert.True(await database.ScalarAsync<long>("SELECT count(*) FROM rcs.schema_migration", database.RuntimeConnectionString) > 0);
    }

    [Fact]
    public async Task PublicHasNoPrivilegeOnTheDatabaseOrThePublicSchema()
    {
        await database.MigrateAsync();

        Assert.Equal(0L, await database.ScalarAsync<long>(
            $"SELECT count(*) FROM pg_database AS d, aclexplode(d.datacl) AS acl WHERE d.datname = '{database.Name}' AND acl.grantee = 0"));
        Assert.Equal(0L, await database.ScalarAsync<long>(
            "SELECT count(*) FROM pg_namespace AS n, aclexplode(n.nspacl) AS acl WHERE n.nspname = 'public' AND acl.grantee = 0"));
        Assert.True(await database.ScalarAsync<bool>($"SELECT has_database_privilege('rcs_app', '{database.Name}', 'CONNECT')"));
    }
}
