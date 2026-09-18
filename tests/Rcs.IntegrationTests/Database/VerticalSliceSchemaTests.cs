using Npgsql;
using Rcs.Application.Cases;
using Rcs.Infrastructure.Development;
using Rcs.IntegrationTests.TestSupport;

namespace Rcs.IntegrationTests.Database;

/// <summary>
/// The guarantees the database itself must hold for the vertical slice, proved against a real PostgreSQL: the audit
/// grant, the absence of DELETE, the per-scope assignment exclusion, and the invariants the frozen model names.
/// </summary>
public sealed class VerticalSliceSchemaTests : IAsyncLifetime
{
    private SliceFixture fixture = null!;

    public async ValueTask InitializeAsync() => fixture = await SliceFixture.CreateAsync();

    public async ValueTask DisposeAsync() => await fixture.DisposeAsync();

    private async Task<PostgresException> RefusedAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.Database.RuntimeConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
    }

    private async Task<Guid> SeededCaseIdAsync() =>
        (await fixture.Queries.ListAsync(fixture.Chief)).First().Id;

    [Fact]
    public async Task TheRuntimeRoleCanAppendAuditHistoryButNeverChangeIt()
    {
        Assert.True(await fixture.ScalarAsync<long>("SELECT count(*) FROM rcs.audit_event") > 0);

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await RefusedAsync("UPDATE rcs.audit_event SET reason_note = 'rewritten'")).SqlState);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await RefusedAsync("DELETE FROM rcs.audit_event")).SqlState);
    }

    [Fact]
    public async Task NoBusinessTableGrantsDeleteToTheRuntimeRole()
    {
        var deletable = await fixture.ScalarAsync<long>("""
            SELECT count(*) FROM information_schema.role_table_grants
            WHERE grantee = 'rcs_app' AND table_schema = 'rcs' AND privilege_type = 'DELETE'
            """);

        Assert.Equal(0L, deletable);
    }

    [Fact]
    public async Task ResponsibilityCannotOverlapOnOneCase()
    {
        var caseId = await SeededCaseIdAsync();

        // A second RESPONSIBLE assignment overlapping the existing one, even for another person, is refused by the
        // per-scope EXCLUDE constraint (DOMAIN_MODEL.md §2.7, amendment A-7).
        var refused = await RefusedAsync($"""
            INSERT INTO rcs.assignment (id, scope, case_id, assignee_user_id, assignment_role_id, assigned_by_user_id, valid_from, status, created_by_user_id)
            VALUES ('01995cff-0001-7000-8000-000000000001', 'CASE', '{caseId}', '{DemoData.WorkerBUserId}',
                    (SELECT id FROM rcs.assignment_role WHERE code = 'RESPONSIBLE'), '{DemoData.ReviewActorUserId}', now(), 'ACTIVE', '{DemoData.ReviewActorUserId}')
            """);

        Assert.Equal(PostgresErrorCodes.ExclusionViolation, refused.SqlState);
    }

    [Fact]
    public async Task ACaseHasAtMostOneInitiatingLetter()
    {
        var caseId = await SeededCaseIdAsync();

        var refused = await RefusedAsync($"""
            INSERT INTO rcs.correspondence (id, case_id, direction, correspondence_kind_id, sender_organization_id, recipient_organization_id,
                                            letter_number, letter_date, registered_by_user_id, received_at, status, created_by_user_id)
            VALUES ('01995cff-0002-7000-8000-000000000001', '{caseId}', 'IN',
                    (SELECT id FROM rcs.correspondence_kind WHERE code = 'INITIATING'),
                    '{DemoData.LandCommissionId}', '{DemoData.OwnOrganizationId}',
                    'SECOND-INITIATING/2026', current_date, '{DemoData.ReviewActorUserId}', now(), 'RECEIVED', '{DemoData.ReviewActorUserId}')
            """);

        Assert.Equal(PostgresErrorCodes.UniqueViolation, refused.SqlState);
    }

    [Fact]
    public async Task ATerminalRequirementCannotBeRecordedWithoutItsResolution()
    {
        var openRequirement = await fixture.ScalarAsync<Guid>("SELECT id FROM rcs.requirement WHERE status IN ('OPEN', 'IN_PROGRESS') LIMIT 1");
        Assert.NotEqual(Guid.Empty, openRequirement);

        // FULFILLED without resolved_at / resolved_by is refused by the database, not only by the application.
        var refused = await RefusedAsync($"UPDATE rcs.requirement SET status = 'FULFILLED' WHERE id = '{openRequirement}'");

        Assert.Equal(PostgresErrorCodes.CheckViolation, refused.SqlState);
    }

    [Fact]
    public async Task AChildRequestMustPointAtARequirementOfItsOwnCase()
    {
        var cases = await fixture.Queries.ListAsync(fixture.Chief);
        var otherCase = cases.First(item => item.Title.Contains("Yaşayış", StringComparison.Ordinal));
        var requirementFromAnotherCase = await fixture.ScalarAsync<Guid>("""
            SELECT q.id FROM rcs.requirement AS q
            JOIN rcs.case_record AS c ON c.id = q.case_id
            WHERE c.title LIKE 'Torpaq sahəsinin%'
            LIMIT 1
            """);

        var refused = await RefusedAsync($"""
            INSERT INTO rcs.request (id, case_id, request_number, target_organization_id, source_requirement_id, subject, status, created_by_user_id)
            VALUES ('01995cff-0003-7000-8000-000000000001', '{otherCase.Id}', 'TEST-CROSS-CASE', '{DemoData.UtilityAuthorityId}',
                    '{requirementFromAnotherCase}', 'Başqa işin tələbi üzrə sorğu', 'DRAFT', '{DemoData.ReviewActorUserId}')
            """);

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, refused.SqlState);
    }
}
