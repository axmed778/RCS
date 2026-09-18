using Npgsql;
using Rcs.Application.Lookups;
using Rcs.Infrastructure.Persistence;

namespace Rcs.Infrastructure.Lookups;

/// <summary>Lookup vocabularies, addressed only through the <see cref="LookupKind"/> allowlist — table names are never user input.</summary>
internal sealed class PostgresLookupQueries(NpgsqlDataSource dataSource) : ILookupQueries
{
    public async Task<IReadOnlyList<LookupItem>> ListActiveAsync(LookupKind kind, CancellationToken cancellationToken = default)
    {
        var (table, flag) = Describe(kind);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT code, label, {flag ?? "NULL::boolean"} AS flag FROM rcs.{table} WHERE is_active ORDER BY sort_order, code",
            connection);
        return await command.ListAsync(reader => new LookupItem(reader.Text("code"), reader.Text("label"), reader.BoolOrNull("flag")), cancellationToken);
    }

    /// <summary>The id of an ACTIVE lookup row, or null when the code is unknown or deactivated.</summary>
    public static Task<Guid?> ActiveIdAsync(PostgresUnitOfWork unitOfWork, LookupKind kind, string code, CancellationToken cancellationToken) =>
        unitOfWork.Command($"SELECT id FROM rcs.{Describe(kind).Table} WHERE code = @code AND is_active")
            .With("code", code)
            .ScalarAsync<Guid?>(cancellationToken);

    private static (string Table, string? Flag) Describe(LookupKind kind) => kind switch
    {
        LookupKind.OrganizationType => ("organization_type", null),
        LookupKind.ResponseType => ("response_type", "is_conclusive_default"),
        LookupKind.ResponseOutcome => ("response_outcome", "is_verdict"),
        LookupKind.VoidReason => ("void_reason", "counts_as_business_outcome"),
        LookupKind.WaiverReason => ("waiver_reason", null),
        LookupKind.DeadlineBasis => ("deadline_basis", null),
        LookupKind.DecisionType => ("decision_type", null),

        // produces_decision carries closure guard G3 (WORKFLOW.md §9.2), so the closure form can say which types
        // need an issued result without a second query.
        LookupKind.ClosureType => ("closure_type", "produces_decision"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown lookup."),
    };
}
