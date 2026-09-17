using Rcs.Infrastructure.Migrations;

namespace Rcs.UnitTests.Infrastructure;

public sealed class MigrationParserTests
{
    private const string ValidContent = "-- description: Creates a probe table\nCREATE TABLE rcs.probe (id integer);\n";

    [Fact]
    public void ParsesNumberNameDescriptionAndDefaultTransactionalFlag()
    {
        var migration = MigrationParser.Parse("0007_add_probe_table.sql", ValidContent);

        Assert.Equal(7, migration.Id);
        Assert.Equal("add_probe_table", migration.Name);
        Assert.Equal("Creates a probe table", migration.Description);
        Assert.True(migration.Transactional);
        Assert.Matches("^[0-9a-f]{64}$", migration.Checksum);
    }

    [Fact]
    public void TransactionalFalseIsTheExplicitEscapeHatch()
    {
        var migration = MigrationParser.Parse(
            "0002_index_probe.sql",
            "-- description: Index concurrently\n-- transactional: false\nCREATE INDEX CONCURRENTLY IF NOT EXISTS probe_idx ON rcs.probe (id);\n");

        Assert.False(migration.Transactional);
    }

    [Theory]
    [InlineData("1_foundation.sql")]
    [InlineData("0001-foundation.sql")]
    [InlineData("0001_Foundation.sql")]
    [InlineData("0001_foundation.SQL")]
    [InlineData("0001__foundation.sql")]
    [InlineData("0000_zero.sql")]
    public void RejectsFileNamesOutsideTheConvention(string fileName)
    {
        Assert.Throws<MigrationValidationException>(() => MigrationParser.Parse(fileName, ValidContent));
    }

    [Fact]
    public void RequiresADescription()
    {
        var exception = Assert.Throws<MigrationValidationException>(() =>
            MigrationParser.Parse("0001_foundation.sql", "-- just a comment\nSELECT 1;\n"));
        Assert.Contains(exception.Problems, problem => problem.Contains("description", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsAnUnknownTransactionalValue()
    {
        Assert.Throws<MigrationValidationException>(() =>
            MigrationParser.Parse("0001_foundation.sql", "-- description: x\n-- transactional: maybe\nSELECT 1;\n"));
    }

    [Fact]
    public void RejectsAFileWithoutSql()
    {
        Assert.Throws<MigrationValidationException>(() =>
            MigrationParser.Parse("0001_foundation.sql", "-- description: nothing here\n\n-- still nothing\n"));
    }

    [Theory]
    [InlineData("COMMIT;")]
    [InlineData("  rollback;")]
    [InlineData("BEGIN;")]
    [InlineData("begin transaction;")]
    [InlineData("START TRANSACTION;")]
    [InlineData("END TRANSACTION;")]
    public void RejectsTransactionControlInTransactionalMigrations(string statement)
    {
        var exception = Assert.Throws<MigrationValidationException>(() =>
            MigrationParser.Parse("0003_bad.sql", $"-- description: bad\nCREATE TABLE rcs.t (id integer);\n{statement}\n"));
        Assert.Contains(exception.Problems, problem => problem.Contains("line 3", StringComparison.Ordinal));
    }

    [Fact]
    public void AcceptsPlpgsqlBlocksAndCommentsThatMentionTransactions()
    {
        const string content = """
            -- description: A DO block
            -- COMMIT is only mentioned in this comment.
            DO $$
            BEGIN
                RAISE NOTICE 'hello';
            END
            $$;
            COMMENT ON SCHEMA rcs IS 'not a commit';
            """;

        Assert.True(MigrationParser.Parse("0004_do_block.sql", content).Transactional);
    }

    [Fact]
    public void ChecksumIgnoresLineEndingsAndByteOrderMarkButNothingElse()
    {
        var lf = MigrationChecksum.Compute("-- description: x\nSELECT 1;\n");

        Assert.Equal(lf, MigrationChecksum.Compute("-- description: x\r\nSELECT 1;\r\n"));
        Assert.Equal(lf, MigrationChecksum.Compute("﻿-- description: x\nSELECT 1;\n"));
        Assert.NotEqual(lf, MigrationChecksum.Compute("-- description: x\nSELECT 1; \n"));
        Assert.NotEqual(lf, MigrationChecksum.Compute("-- description: x\n-- edited comment\nSELECT 1;\n"));
    }
}

public sealed class MigrationSetTests
{
    private static (string, string) File(string name) => (name, "-- description: probe\nSELECT 1;\n");

    [Fact]
    public void OrdersMigrationsByNumberAndReportsTheLatestVersion()
    {
        var set = MigrationSet.Create([File("0002_second.sql"), File("0001_first.sql")]);

        Assert.Equal([1, 2], set.Migrations.Select(migration => migration.Id));
        Assert.Equal(2, set.LatestVersion);
    }

    [Fact]
    public void AnEmptySetIsVersionZero()
    {
        Assert.Equal(0, MigrationSet.Create([]).LatestVersion);
    }

    [Fact]
    public void RejectsGapsInNumbering()
    {
        var exception = Assert.Throws<MigrationValidationException>(() => MigrationSet.Create([File("0001_first.sql"), File("0003_third.sql")]));
        Assert.Contains(exception.Problems, problem => problem.Contains("contiguous", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsDuplicateNumbers()
    {
        var exception = Assert.Throws<MigrationValidationException>(() => MigrationSet.Create([File("0001_first.sql"), File("0001_other.sql")]));
        Assert.Contains(exception.Problems, problem => problem.Contains("more than one file", StringComparison.Ordinal));
    }

    [Fact]
    public void ReportsProblemsFromEveryInvalidFile()
    {
        var exception = Assert.Throws<MigrationValidationException>(() =>
            MigrationSet.Create([("bad-name.sql", "SELECT 1;"), ("0001_first.sql", "SELECT 1;")]));
        Assert.True(exception.Problems.Count >= 2);
    }
}

public sealed class MigrationHistoryComparisonTests
{
    private static readonly MigrationSet Release = MigrationSet.Create(
    [
        ("0001_first.sql", "-- description: first\nSELECT 1;\n"),
        ("0002_second.sql", "-- description: second\nSELECT 2;\n"),
    ]);

    private static AppliedMigrationRecord Applied(int index) =>
        new(Release.Migrations[index].Id, Release.Migrations[index].Name, Release.Migrations[index].Checksum);

    [Fact]
    public void IdenticalHistoryHasNothingPending()
    {
        var comparison = MigrationHistory.Compare([Applied(0), Applied(1)], Release);

        Assert.Empty(comparison.Mismatches);
        Assert.Empty(comparison.NotInRelease);
        Assert.Empty(comparison.Pending);
        Assert.Equal(2, comparison.DatabaseVersion);
    }

    [Fact]
    public void AppliedPrefixLeavesTheRestPending()
    {
        var comparison = MigrationHistory.Compare([Applied(0)], Release);

        Assert.Equal([2], comparison.Pending.Select(migration => migration.Id));
        Assert.Equal(1, comparison.DatabaseVersion);
    }

    [Fact]
    public void EditedAppliedMigrationIsAMismatch()
    {
        var edited = Applied(0) with { Checksum = new string('0', 64) };
        Assert.Contains("modified after it was applied", Assert.Single(MigrationHistory.Compare([edited], Release).Mismatches), StringComparison.Ordinal);
    }

    [Fact]
    public void RenamedAppliedMigrationIsAMismatch()
    {
        var renamed = Applied(0) with { Name = "renamed" };
        Assert.Single(MigrationHistory.Compare([renamed], Release).Mismatches);
    }

    [Fact]
    public void UnknownAppliedMigrationMeansTheDatabaseIsAhead()
    {
        var comparison = MigrationHistory.Compare([Applied(0), Applied(1), new AppliedMigrationRecord(3, "third", new string('a', 64))], Release);

        Assert.Empty(comparison.Mismatches);
        Assert.Equal(3, Assert.Single(comparison.NotInRelease).MigrationId);
    }

    [Fact]
    public void GapInHistoryIsAMismatch()
    {
        var comparison = MigrationHistory.Compare([Applied(1)], Release);
        Assert.Contains("not contiguous", Assert.Single(comparison.Mismatches), StringComparison.Ordinal);
    }
}
