using Npgsql;
using NpgsqlTypes;

namespace Rcs.Infrastructure.Persistence;

/// <summary>
/// Small helpers for hand-written, parameterised SQL. Values are always parameters — never concatenated
/// (SECURITY.md §10.7). Instants are written as UTC, which Npgsql requires for <c>timestamptz</c>.
/// </summary>
internal static class Sql
{
    public static NpgsqlCommand Command(this PostgresUnitOfWork unitOfWork, string sql) =>
        new(sql, unitOfWork.Connection, unitOfWork.Transaction);

    public static NpgsqlCommand With(this NpgsqlCommand command, string name, Guid value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Uuid) { Value = value });
        return command;
    }

    public static NpgsqlCommand With(this NpgsqlCommand command, string name, Guid? value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Uuid) { Value = value.HasValue ? value.Value : DBNull.Value });
        return command;
    }

    public static NpgsqlCommand With(this NpgsqlCommand command, string name, string? value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = value is null ? DBNull.Value : value });
        return command;
    }

    public static NpgsqlCommand With(this NpgsqlCommand command, string name, bool value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Boolean) { Value = value });
        return command;
    }

    public static NpgsqlCommand With(this NpgsqlCommand command, string name, int value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Integer) { Value = value });
        return command;
    }

    public static NpgsqlCommand WithLong(this NpgsqlCommand command, string name, long value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Bigint) { Value = value });
        return command;
    }

    public static NpgsqlCommand With(this NpgsqlCommand command, string name, int? value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Integer) { Value = value.HasValue ? value.Value : DBNull.Value });
        return command;
    }

    public static NpgsqlCommand With(this NpgsqlCommand command, string name, DateTimeOffset value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.TimestampTz) { Value = value.ToUniversalTime() });
        return command;
    }

    public static NpgsqlCommand With(this NpgsqlCommand command, string name, DateTimeOffset? value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.TimestampTz) { Value = value.HasValue ? value.Value.ToUniversalTime() : DBNull.Value });
        return command;
    }

    public static NpgsqlCommand With(this NpgsqlCommand command, string name, DateOnly? value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Date) { Value = value.HasValue ? value.Value : DBNull.Value });
        return command;
    }

    public static NpgsqlCommand WithIds(this NpgsqlCommand command, string name, IReadOnlyCollection<Guid> values)
    {
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = values.ToArray() });
        return command;
    }

    public static NpgsqlCommand WithJson(this NpgsqlCommand command, string name, string? json)
    {
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Jsonb) { Value = json is null ? DBNull.Value : json });
        return command;
    }

    public static NpgsqlCommand WithTextArray(this NpgsqlCommand command, string name, IReadOnlyCollection<string>? values)
    {
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = values is null ? DBNull.Value : values.ToArray() });
        return command;
    }

    public static async Task<int> ExecuteAsync(this NpgsqlCommand command, CancellationToken cancellationToken)
    {
        await using (command)
        {
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public static async Task<T?> ScalarAsync<T>(this NpgsqlCommand command, CancellationToken cancellationToken)
    {
        await using (command)
        {
            var value = await command.ExecuteScalarAsync(cancellationToken);
            return value is null or DBNull ? default : (T)value;
        }
    }

    /// <summary>Reads every row with <paramref name="map"/>.</summary>
    public static async Task<List<T>> ListAsync<T>(this NpgsqlCommand command, Func<NpgsqlDataReader, T> map, CancellationToken cancellationToken)
    {
        await using (command)
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var rows = new List<T>();
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(map(reader));
            }

            return rows;
        }
    }

    public static async Task<T?> SingleOrDefaultAsync<T>(this NpgsqlCommand command, Func<NpgsqlDataReader, T> map, CancellationToken cancellationToken)
        where T : class
    {
        var rows = await command.ListAsync(map, cancellationToken);
        return rows.Count switch
        {
            0 => null,
            1 => rows[0],
            _ => throw new InvalidOperationException("The query returned more than one row."),
        };
    }

    public static Guid Uuid(this NpgsqlDataReader reader, string column) => reader.GetFieldValue<Guid>(reader.GetOrdinal(column));

    public static Guid? UuidOrNull(this NpgsqlDataReader reader, string column) => reader.IsDBNull(reader.GetOrdinal(column)) ? null : reader.GetFieldValue<Guid>(reader.GetOrdinal(column));

    public static string Text(this NpgsqlDataReader reader, string column) => reader.GetString(reader.GetOrdinal(column));

    public static string? TextOrNull(this NpgsqlDataReader reader, string column) => reader.IsDBNull(reader.GetOrdinal(column)) ? null : reader.GetString(reader.GetOrdinal(column));

    public static bool Bool(this NpgsqlDataReader reader, string column) => reader.GetBoolean(reader.GetOrdinal(column));

    public static bool? BoolOrNull(this NpgsqlDataReader reader, string column) => reader.IsDBNull(reader.GetOrdinal(column)) ? null : reader.GetBoolean(reader.GetOrdinal(column));

    public static int Int(this NpgsqlDataReader reader, string column) => reader.GetInt32(reader.GetOrdinal(column));

    public static long Long(this NpgsqlDataReader reader, string column) => reader.GetInt64(reader.GetOrdinal(column));

    public static DateTimeOffset Instant(this NpgsqlDataReader reader, string column) => reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal(column));

    public static DateTimeOffset? InstantOrNull(this NpgsqlDataReader reader, string column) => reader.IsDBNull(reader.GetOrdinal(column)) ? null : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal(column));

    public static DateOnly? DateOrNull(this NpgsqlDataReader reader, string column) => reader.IsDBNull(reader.GetOrdinal(column)) ? null : reader.GetFieldValue<DateOnly>(reader.GetOrdinal(column));

    public static string[] TextArray(this NpgsqlDataReader reader, string column) => reader.IsDBNull(reader.GetOrdinal(column)) ? [] : reader.GetFieldValue<string[]>(reader.GetOrdinal(column));
}
