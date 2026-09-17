using Npgsql;

namespace Rcs.Infrastructure.Migrations;

internal sealed record AppliedMigrationRecord(int MigrationId, string Name, string Checksum);

/// <summary>How a database's history relates to a release.</summary>
/// <param name="Mismatches">Edited or renamed applied migrations, or gaps in history. Always fatal.</param>
/// <param name="NotInRelease">Applied migrations this release does not contain: the database is ahead.</param>
/// <param name="Pending">Release migrations not yet applied: the database is behind.</param>
internal sealed record HistoryComparison(
    IReadOnlyList<string> Mismatches,
    IReadOnlyList<AppliedMigrationRecord> NotInRelease,
    IReadOnlyList<Migration> Pending,
    int DatabaseVersion);

internal static class MigrationHistory
{
    private const string SelectHistorySql =
        "SELECT migration_id, name, checksum_sha256 FROM rcs.schema_migration ORDER BY migration_id";

    internal static async Task<IReadOnlyList<AppliedMigrationRecord>> ReadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var history = new List<AppliedMigrationRecord>();
        await using var command = new NpgsqlCommand(SelectHistorySql, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            history.Add(new AppliedMigrationRecord(reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
        }

        return history;
    }

    internal static HistoryComparison Compare(IReadOnlyList<AppliedMigrationRecord> history, MigrationSet release)
    {
        var mismatches = new List<string>();
        var notInRelease = new List<AppliedMigrationRecord>();
        var releaseById = release.Migrations.ToDictionary(migration => migration.Id);

        for (var index = 0; index < history.Count; index++)
        {
            var applied = history[index];
            if (applied.MigrationId != index + 1)
            {
                mismatches.Add($"migration history is not contiguous: position {index + 1} holds migration {applied.MigrationId:D4}.");
                break;
            }

            if (!releaseById.TryGetValue(applied.MigrationId, out var migration))
            {
                notInRelease.Add(applied);
                continue;
            }

            if (!string.Equals(migration.Name, applied.Name, StringComparison.Ordinal))
            {
                mismatches.Add($"migration {applied.MigrationId:D4} was applied as '{applied.Name}' but this release names it '{migration.Name}'. Applied migrations are never renamed.");
            }

            if (!string.Equals(migration.Checksum, applied.Checksum, StringComparison.Ordinal))
            {
                mismatches.Add($"migration {migration.FileName} was modified after it was applied (applied checksum {applied.Checksum}, release checksum {migration.Checksum}). Applied migrations are immutable: write a new migration instead.");
            }
        }

        var appliedIds = history.Select(record => record.MigrationId).ToHashSet();
        var pending = release.Migrations.Where(migration => !appliedIds.Contains(migration.Id)).ToArray();
        var databaseVersion = history.Count == 0 ? 0 : history[^1].MigrationId;

        return new HistoryComparison(mismatches, notInRelease, pending, databaseVersion);
    }
}
