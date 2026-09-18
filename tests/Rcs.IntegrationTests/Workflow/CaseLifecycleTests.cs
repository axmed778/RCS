using Rcs.Application.Cases;
using Rcs.Application.Common;
using Rcs.Application.Lifecycle;
using Rcs.Application.Workflow;
using Rcs.Domain.Progress;
using Rcs.Domain.Vocabulary;
using Rcs.Domain.Workflow;
using Rcs.Infrastructure.Development;
using Rcs.IntegrationTests.TestSupport;

namespace Rcs.IntegrationTests.Workflow;

/// <summary>
/// The end of the case, end to end against a real database: request deadlines (ADR-041), the final result and its
/// single Head approval (ADR-040), closure under guards G1–G5 with the non-blocking warning (ADR-012 / OB-4), the
/// Head's override, and reopening the same case (WORKFLOW.md §10).
/// </summary>
public sealed class CaseLifecycleTests : IAsyncLifetime
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private SliceFixture fixture = null!;

    public async ValueTask InitializeAsync() => fixture = await SliceFixture.CreateAsync();

    public async ValueTask DisposeAsync() => await fixture.DisposeAsync();

    // ------------------------------------------------------------------ deadlines

    [Fact]
    public async Task ARequestWithNoDeadlineGetsTheSentDatePlusTenCalendarDays()
    {
        var caseId = await RegisterCaseAsync("LIFE-IN-1/2026");
        var requestId = await RegisterRequestAsync(caseId, "LIFE-OUT-1/2026", sentDaysAgo: 3, dueDate: null);

        var request = (await fixture.WorkspaceAsync(caseId)).FindRequest(requestId)!;
        Assert.Equal(Today.AddDays(-3).AddDays(RequestDeadline.DefaultDays), DateOnly.FromDateTime(request.DueAt!.Value.UtcDateTime));

        // The rule is the department's own, so the basis is recorded as INTERNAL rather than left unstated.
        Assert.Equal("INTERNAL", await fixture.ScalarAsync<string>(
            $"SELECT b.code FROM rcs.request AS r JOIN rcs.deadline_basis AS b ON b.id = r.deadline_basis_id WHERE r.id = '{requestId}'"));
    }

    /// <summary>The default is a suggestion: what the person saved stands, and nothing recomputes it behind them.</summary>
    [Fact]
    public async Task ADeadlineTheUserChoseIsKeptAndItsBasisIsNotClaimedToBeTheDefault()
    {
        var caseId = await RegisterCaseAsync("LIFE-IN-2/2026");
        var chosen = Today.AddDays(30);
        var requestId = await RegisterRequestAsync(caseId, "LIFE-OUT-2/2026", sentDaysAgo: 3, dueDate: chosen);

        var request = (await fixture.WorkspaceAsync(caseId)).FindRequest(requestId)!;
        Assert.Equal(chosen, DateOnly.FromDateTime(request.DueAt!.Value.UtcDateTime));
        Assert.Null(await fixture.ScalarAsync<object>($"SELECT deadline_basis_id FROM rcs.request WHERE id = '{requestId}'"));
    }

    /// <summary>
    /// A back-dated registration is measured from the letter's actual date, so it is overdue immediately and
    /// truthfully — not given a fresh ten days because it was typed in today (ADR-041).
    /// </summary>
    [Fact]
    public async Task AHistoricalRegistrationIsOverdueStraightAway()
    {
        var caseId = await RegisterCaseAsync("LIFE-IN-3/2026");
        var requestId = await RegisterRequestAsync(caseId, "LIFE-OUT-3/2026", sentDaysAgo: 40, dueDate: null);

        var progress = (await fixture.WorkspaceAsync(caseId)).Progress.Requests[requestId];
        Assert.Equal(RequestProgressState.Overdue, progress.State);
        Assert.Equal(30, progress.OverdueDays);
    }

    [Fact]
    public async Task TheLastDayOfTheDeadlineIsNotYetOverdue()
    {
        var caseId = await RegisterCaseAsync("LIFE-IN-4/2026");
        var requestId = await RegisterRequestAsync(caseId, "LIFE-OUT-4/2026", sentDaysAgo: RequestDeadline.DefaultDays, dueDate: null);

        var progress = (await fixture.WorkspaceAsync(caseId)).Progress.Requests[requestId];
        Assert.Equal(RequestProgressState.DueToday, progress.State);
        Assert.Equal(0, progress.OverdueDays);
    }

    /// <summary>
    /// WORKFLOW.md §12.5: a hold is a statement about the department's work, not about the authority's clock. The
    /// display shows both facts; it does not suppress either.
    /// </summary>
    [Fact]
    public async Task AHoldDoesNotPauseARequestDeadline()
    {
        var caseId = await RegisterCaseAsync("LIFE-IN-5/2026");
        var requestId = await RegisterRequestAsync(caseId, "LIFE-OUT-5/2026", sentDaysAgo: 40, dueDate: null);
        await PutOnHoldAsync(caseId);

        var workspace = await fixture.WorkspaceAsync(caseId);
        Assert.Equal(CaseLifecycleState.OnHold, workspace.Header.State);
        Assert.Equal(30, workspace.Progress.Requests[requestId].OverdueDays);
        Assert.Equal("P2.Open", workspace.Progress.Headline.Code);    // the hold is the headline, the overdue count survives
    }

    /// <summary>An answered request is nobody's overdue work, however long the letter took (§12.2).</summary>
    [Fact]
    public async Task AnAnsweredRequestIsNoLongerOverdue()
    {
        var caseId = await RegisterCaseAsync("LIFE-IN-6/2026");
        var requestId = await RegisterRequestAsync(caseId, "LIFE-OUT-6/2026", sentDaysAgo: 40, dueDate: null);
        await RespondAsync(caseId, requestId, "LIFE-RESP-6/2026");

        var progress = (await fixture.WorkspaceAsync(caseId)).Progress.Requests[requestId];
        Assert.Equal(0, progress.OverdueDays);
        Assert.NotEqual(RequestProgressState.Overdue, progress.State);
    }

    [Fact]
    public async Task TheDashboardListsWaitingRequestsWithTheMostOverdueFirst()
    {
        var caseId = await RegisterCaseAsync("LIFE-IN-7/2026");
        await RegisterRequestAsync(caseId, "LIFE-OUT-7A/2026", sentDaysAgo: 12, dueDate: null);
        var worst = await RegisterRequestAsync(caseId, "LIFE-OUT-7B/2026", sentDaysAgo: 40, dueDate: null, target: DemoData.EmergencyAuthorityId);

        var dashboard = await fixture.Queries.GetDashboardAsync(fixture.Chief);
        var mine = dashboard.AwaitingResponse.Where(reminder => reminder.CaseId == caseId).ToArray();

        Assert.Equal(2, mine.Length);
        Assert.Equal(worst, mine[0].RequestId);
        Assert.Equal(30, mine[0].Progress.OverdueDays);
        Assert.False(mine[0].CaseIsOnHold);
    }

    // --------------------------------------------------------------- final result

    [Fact]
    public async Task AChiefMayDraftAFinalResultButOnlyTheHeadMayIssueIt()
    {
        var (caseId, _) = await ResolvedCaseAsync("LIFE-IN-8/2026", "LIFE-OUT-8/2026", "LIFE-RESP-8/2026");
        var resultId = await DraftAsync(caseId, "APPROVAL");

        var refused = await fixture.Lifecycle.IssueFinalResultAsync(fixture.Chief, new IssueFinalResultCommand(caseId, resultId, RowVersionOf(1), null));
        Assert.Equal(CommandErrorKind.Forbidden, refused.Error?.Kind);

        var issued = await fixture.Lifecycle.IssueFinalResultAsync(fixture.Head, new IssueFinalResultCommand(caseId, resultId, RowVersionOf(1), null));
        SliceFixture.Succeeded(issued);

        var result = (await fixture.LifecycleQueries.ListFinalResultsAsync(fixture.Chief, caseId)).Single();
        Assert.Equal(FinalResultStatus.Issued, result.Status);
        Assert.Equal("Nümayiş rəhbəri (sintetik)", result.ApprovedByName);
        Assert.NotNull(result.IssuedAt);

        // The refusal is recorded against the Chief who attempted it, not swallowed (ADR-018).
        Assert.Equal(1L, await fixture.ScalarAsync<long>(
            $"SELECT count(*) FROM rcs.audit_event WHERE case_id = '{caseId}' AND action_code = 'PERMISSION_DENIED' AND actor_user_id = '{DemoData.ReviewActorUserId}'"));
    }

    /// <summary>
    /// ADR-040: one approval. The Head may also be the decision-maker, so issuing records both acts and no four-eyes
    /// rule is imposed.
    /// </summary>
    [Fact]
    public async Task IssuingRecordsTheDecisionAndTheSingleApprovalTogether()
    {
        var (caseId, _) = await ResolvedCaseAsync("LIFE-IN-9/2026", "LIFE-OUT-9/2026", "LIFE-RESP-9/2026");
        var resultId = await IssuedResultAsync(caseId, "APPROVAL");

        var result = (await fixture.LifecycleQueries.ListFinalResultsAsync(fixture.Chief, caseId)).Single(item => item.Id == resultId);
        Assert.Equal(result.DecidedByName, result.ApprovedByName);
        Assert.NotNull(result.DecidedAt);
        Assert.NotNull(result.ApprovedAt);
    }

    [Fact]
    public async Task AResultIsNotIssuedOnAnUnreadyCaseWithoutTheHeadsReason()
    {
        var caseId = await RegisterCaseAsync("LIFE-IN-10/2026");
        await RegisterRequestAsync(caseId, "LIFE-OUT-10/2026", sentDaysAgo: 5, dueDate: null);   // still out: not ready
        var resultId = await DraftAsync(caseId, "REFUSAL");

        var refused = await fixture.Lifecycle.IssueFinalResultAsync(fixture.Head, new IssueFinalResultCommand(caseId, resultId, RowVersionOf(1), null));
        Assert.Equal("final_result.case_not_ready", refused.Error?.Code);

        SliceFixture.Succeeded(await fixture.Lifecycle.IssueFinalResultAsync(
            fixture.Head, new IssueFinalResultCommand(caseId, resultId, RowVersionOf(1), "Orqan cavab verməyəcəyini şifahi bildirdi.")));

        // The override's reason is part of the permanent record, and the request stays exactly as it was.
        Assert.Equal(1L, await fixture.ScalarAsync<long>(
            $"SELECT count(*) FROM rcs.audit_event WHERE entity_id = '{resultId}' AND action_code = 'STATE_CHANGE' AND reason_note IS NOT NULL"));
        Assert.Equal("SENT", await fixture.ScalarAsync<string>($"SELECT status FROM rcs.request WHERE case_id = '{caseId}'"));
    }

    [Fact]
    public async Task OnlyOneResultIsInForceAndAReplacementRetiresTheOneItNames()
    {
        var (caseId, _) = await ResolvedCaseAsync("LIFE-IN-11/2026", "LIFE-OUT-11/2026", "LIFE-RESP-11/2026");
        var first = await IssuedResultAsync(caseId, "PARTIAL_APPROVAL");

        var replacement = SliceFixture.Succeeded(await fixture.Lifecycle.DraftFinalResultAsync(fixture.Chief, new DraftFinalResultCommand(
            fixture.NewOperation(), caseId, "APPROVAL", "Düzəliş edilmiş qərar", "Əvvəlki qərarda texniki səhv olub.", first)));
        SliceFixture.Succeeded(await fixture.Lifecycle.IssueFinalResultAsync(
            fixture.Head, new IssueFinalResultCommand(caseId, replacement, RowVersionOf(1), null)));

        var results = await fixture.LifecycleQueries.ListFinalResultsAsync(fixture.Chief, caseId);
        Assert.Equal(FinalResultStatus.Superseded, results.Single(item => item.Id == first).Status);
        Assert.Equal(FinalResultStatus.Issued, results.Single(item => item.Id == replacement).Status);

        // The superseded result is retired, never deleted: it is what the requester acted on at the time.
        Assert.Equal(1L, await fixture.ScalarAsync<long>(
            $"SELECT count(*) FROM rcs.final_result WHERE case_id = '{caseId}' AND status = 'ISSUED'"));
        Assert.Equal(2L, await fixture.ScalarAsync<long>($"SELECT count(*) FROM rcs.final_result WHERE case_id = '{caseId}'"));
    }

    [Fact]
    public async Task AnIssuedResultIsRevokedOnlyByTheHeadAndOnlyWithAReason()
    {
        var (caseId, _) = await ResolvedCaseAsync("LIFE-IN-12/2026", "LIFE-OUT-12/2026", "LIFE-RESP-12/2026");
        var resultId = await IssuedResultAsync(caseId, "APPROVAL");

        Assert.Equal(CommandErrorKind.Forbidden,
            (await fixture.Lifecycle.RevokeFinalResultAsync(fixture.Chief, new RevokeFinalResultCommand(caseId, resultId, RowVersionOf(2), "səbəb"))).Error?.Kind);
        Assert.Equal("final_result.revocation_reason_required",
            (await fixture.Lifecycle.RevokeFinalResultAsync(fixture.Head, new RevokeFinalResultCommand(caseId, resultId, RowVersionOf(2), "  "))).Error?.Code);

        SliceFixture.Succeeded(await fixture.Lifecycle.RevokeFinalResultAsync(
            fixture.Head, new RevokeFinalResultCommand(caseId, resultId, RowVersionOf(2), "Məhkəmə qərarı ilə ləğv edilib.")));

        var result = (await fixture.LifecycleQueries.ListFinalResultsAsync(fixture.Chief, caseId)).Single();
        Assert.Equal(FinalResultStatus.Revoked, result.Status);
        Assert.Equal("Məhkəmə qərarı ilə ləğv edilib.", result.RevocationReasonNote);
    }

    // -------------------------------------------------------------------- closure

    [Fact]
    public async Task ACaseClosesNormallyWhenEveryGuardPasses()
    {
        var (caseId, _) = await ResolvedCaseAsync("LIFE-IN-13/2026", "LIFE-OUT-13/2026", "LIFE-RESP-13/2026");
        await IssuedResultAsync(caseId, "APPROVAL");

        var preview = await PreviewAsync(caseId, "COMPLETED");
        Assert.All(preview.Guards, guard => Assert.True(guard.Passed, guard.Code));

        SliceFixture.Succeeded(await CloseAsync(caseId, "COMPLETED"));

        var workspace = await fixture.WorkspaceAsync(caseId);
        Assert.Equal(CaseLifecycleState.Closed, workspace.Header.State);
        Assert.Equal("P1", workspace.Progress.Headline.Code);
        Assert.False(workspace.Progress.ClosedWithUnresolvedItems);
        Assert.Equal("COMPLETED", await fixture.ScalarAsync<string>(
            $"SELECT t.code FROM rcs.case_record AS c JOIN rcs.closure_type AS t ON t.id = c.closure_type_id WHERE c.id = '{caseId}'"));
        Assert.Equal(1L, await fixture.ScalarAsync<long>(
            $"SELECT count(*) FROM rcs.case_state_change WHERE case_id = '{caseId}' AND to_state = 'CLOSED' AND reason_code IS NULL"));
    }

    /// <summary>G3: a closure type that produces no decision does not need an issued result.</summary>
    [Fact]
    public async Task AWithdrawnCaseClosesWithoutAFinalResult()
    {
        var (caseId, _) = await ResolvedCaseAsync("LIFE-IN-14/2026", "LIFE-OUT-14/2026", "LIFE-RESP-14/2026");

        Assert.Contains(await PreviewAsync(caseId, "COMPLETED") is { } needsDecision ? needsDecision.Failing : [], guard => guard.Code == "G3");
        Assert.Empty((await PreviewAsync(caseId, "WITHDRAWN_BY_REQUESTER")).Failing);

        SliceFixture.Succeeded(await CloseAsync(caseId, "WITHDRAWN_BY_REQUESTER"));
        Assert.Equal(CaseLifecycleState.Closed, (await fixture.WorkspaceAsync(caseId)).Header.State);
    }

    /// <summary>
    /// OB-4 obligation 1: the person closing must have seen the open non-blocking requirements. Without the explicit
    /// confirmation the closure is refused, so the confirmation cannot be skipped by posting the form.
    /// </summary>
    [Fact]
    public async Task ClosingOverOpenNonBlockingWorkNeedsAnExplicitConfirmation()
    {
        var caseId = await CaseWithOpenNonBlockingRequirementAsync("LIFE-IN-15/2026", "LIFE-OUT-15/2026", "LIFE-RESP-15/2026");
        await IssuedResultAsync(caseId, "APPROVAL");

        var preview = await PreviewAsync(caseId, "COMPLETED");
        Assert.Single(preview.UnresolvedNonBlocking);
        Assert.Empty(preview.UnresolvedBlocking);
        Assert.All(preview.Guards, guard => Assert.True(guard.Passed, guard.Code));   // G1 counts blocking work only

        var refused = await CloseAsync(caseId, "COMPLETED", acknowledge: false);
        Assert.Equal("case.unresolved_non_blocking_not_acknowledged", refused.Error?.Code);

        SliceFixture.Succeeded(await CloseAsync(caseId, "COMPLETED", acknowledge: true));
    }

    /// <summary>OB-4 obligations 2 and 3: nothing is auto-resolved, and the closed case says what was left open.</summary>
    [Fact]
    public async Task AClosureLeavesNonBlockingRequirementsInTheirTrueStateAndSaysSo()
    {
        var caseId = await CaseWithOpenNonBlockingRequirementAsync("LIFE-IN-16/2026", "LIFE-OUT-16/2026", "LIFE-RESP-16/2026");
        await IssuedResultAsync(caseId, "APPROVAL");
        SliceFixture.Succeeded(await CloseAsync(caseId, "COMPLETED", acknowledge: true));

        var workspace = await fixture.WorkspaceAsync(caseId);
        var requirement = workspace.AllRequirements.Single();

        Assert.Equal(RequirementStatus.Open, requirement.Status);           // not auto-fulfilled, auto-voided or auto-failed
        Assert.Null(requirement.ResolvedAt);
        Assert.True(workspace.Progress.ClosedWithUnresolvedItems);
        Assert.Equal(1, workspace.Progress.UnresolvedAtClosure);
        Assert.Equal("P1.Unresolved", workspace.Progress.Headline.Code);
    }

    /// <summary>G1 is the failure mode this system exists to prevent: a Chief cannot bypass it by any route.</summary>
    [Fact]
    public async Task AnOpenBlockingRequirementStopsAChiefClosingTheCase()
    {
        var caseId = await CaseWithOpenBlockingRequirementAsync("LIFE-IN-17/2026", "LIFE-OUT-17/2026", "LIFE-RESP-17/2026");

        var preview = await PreviewAsync(caseId, "COMPLETED");
        Assert.Contains(preview.Failing, guard => guard.Code == "G1");
        Assert.Single(preview.UnresolvedBlocking);

        var refused = await CloseAsync(caseId, "COMPLETED", acknowledge: true);
        Assert.Equal(CommandErrorKind.Forbidden, refused.Error?.Kind);
        Assert.Equal(CaseLifecycleState.Active, (await fixture.WorkspaceAsync(caseId)).Header.State);
    }

    [Fact]
    public async Task TheHeadMayCloseOverAFailingGuardWithAReasonAndNothingIsRewritten()
    {
        var caseId = await CaseWithOpenBlockingRequirementAsync("LIFE-IN-18/2026", "LIFE-OUT-18/2026", "LIFE-RESP-18/2026");

        Assert.Equal("case.closure_override_reason_required",
            (await CloseAsync(caseId, "COMPLETED", acknowledge: true, actor: fixture.Head)).Error?.Code);

        SliceFixture.Succeeded(await CloseAsync(
            caseId, "COMPLETED", acknowledge: true, actor: fixture.Head, overrideNote: "Orqan ləğv edilib, tələb əldə edilə bilməz."));

        var workspace = await fixture.WorkspaceAsync(caseId);
        Assert.Equal(CaseLifecycleState.Closed, workspace.Header.State);
        Assert.Equal(RequirementStatus.Open, workspace.AllRequirements.Single(requirement => requirement.IsBlocking).Status);
        Assert.True(workspace.Progress.ClosedWithUnresolvedItems);

        // The override names the guards it passed over, and is attributed to the Head who did it.
        var note = await fixture.ScalarAsync<string>(
            $"SELECT note FROM rcs.case_state_change WHERE case_id = '{caseId}' AND reason_code = 'CLOSURE_GUARD_OVERRIDE'");
        Assert.Contains("G1", note, StringComparison.Ordinal);
        Assert.Equal(DemoData.ReviewHeadUserId, await fixture.ScalarAsync<Guid>(
            $"SELECT actor_user_id FROM rcs.case_state_change WHERE case_id = '{caseId}' AND reason_code = 'CLOSURE_GUARD_OVERRIDE'"));
    }

    [Fact]
    public async Task AClosedCaseTakesNoNewWork()
    {
        var (caseId, _) = await ResolvedCaseAsync("LIFE-IN-19/2026", "LIFE-OUT-19/2026", "LIFE-RESP-19/2026");
        await IssuedResultAsync(caseId, "APPROVAL");
        SliceFixture.Succeeded(await CloseAsync(caseId, "COMPLETED"));

        var refused = await fixture.Workflow.RegisterRequestAsync(fixture.Chief, new RegisterRequestCommand(
            fixture.NewOperation(), caseId, DemoData.EmergencyAuthorityId, "Yeni sorğu", "LIFE-OUT-19B/2026",
            Today, null, null, null));
        Assert.Equal("case.not_open_to_work", refused.Error?.Code);
    }

    // ------------------------------------------------------------------ reopening

    [Fact]
    public async Task ReopeningResumesTheSameCaseKeepsItsClosureAndItsResultAndAllowsNewWork()
    {
        var (caseId, _) = await ResolvedCaseAsync("LIFE-IN-20/2026", "LIFE-OUT-20/2026", "LIFE-RESP-20/2026");
        var resultId = await IssuedResultAsync(caseId, "APPROVAL");
        SliceFixture.Succeeded(await CloseAsync(caseId, "COMPLETED"));

        var closed = await fixture.WorkspaceAsync(caseId);
        SliceFixture.Succeeded(await fixture.Lifecycle.ReopenCaseAsync(
            fixture.Chief, new ReopenCaseCommand(caseId, closed.Header.RowVersion, "Gec gələn məktub yeni iş yaradır.")));

        var reopened = await fixture.WorkspaceAsync(caseId);
        Assert.Equal(CaseLifecycleState.Active, reopened.Header.State);
        Assert.Equal(closed.Header.CaseNumber, reopened.Header.CaseNumber);                      // the same dossier

        // Closure metadata is retained, not cleared, and the issued decision stays in force (§10.1).
        Assert.NotNull(await fixture.ScalarAsync<object>($"SELECT closed_at FROM rcs.case_record WHERE id = '{caseId}'"));
        Assert.NotNull(await fixture.ScalarAsync<object>($"SELECT closure_type_id FROM rcs.case_record WHERE id = '{caseId}'"));
        Assert.Equal(FinalResultStatus.Issued,
            (await fixture.LifecycleQueries.ListFinalResultsAsync(fixture.Chief, caseId)).Single(item => item.Id == resultId).Status);

        // New work is permitted again.
        SliceFixture.Succeeded(await fixture.Workflow.RegisterRequestAsync(fixture.Chief, new RegisterRequestCommand(
            fixture.NewOperation(), caseId, DemoData.EmergencyAuthorityId, "Əlavə sorğu", "LIFE-OUT-20B/2026",
            Today, null, null, null)));

        // Both closure episodes remain readable as history, and the reason is part of the record.
        Assert.Equal(1L, await fixture.ScalarAsync<long>(
            $"SELECT count(*) FROM rcs.case_state_change WHERE case_id = '{caseId}' AND to_state = 'CLOSED'"));
        Assert.Equal("Gec gələn məktub yeni iş yaradır.", await fixture.ScalarAsync<string>(
            $"SELECT note FROM rcs.case_state_change WHERE case_id = '{caseId}' AND reason_code = 'REOPENED'"));
    }

    [Fact]
    public async Task ReopeningNeedsAReasonAndAnOpenCaseCannotBeReopened()
    {
        var (caseId, _) = await ResolvedCaseAsync("LIFE-IN-21/2026", "LIFE-OUT-21/2026", "LIFE-RESP-21/2026");
        var open = await fixture.WorkspaceAsync(caseId);

        Assert.Equal("case.not_closed",
            (await fixture.Lifecycle.ReopenCaseAsync(fixture.Chief, new ReopenCaseCommand(caseId, open.Header.RowVersion, "səbəb"))).Error?.Code);

        await IssuedResultAsync(caseId, "APPROVAL");
        SliceFixture.Succeeded(await CloseAsync(caseId, "COMPLETED"));
        var closed = await fixture.WorkspaceAsync(caseId);

        Assert.Equal("case.reopen_reason_required",
            (await fixture.Lifecycle.ReopenCaseAsync(fixture.Chief, new ReopenCaseCommand(caseId, closed.Header.RowVersion, "   "))).Error?.Code);
    }

    // ------------------------------------------------------------------- fixtures

    /// <summary>The row version of a freshly created final result; every command re-checks it under the case lock.</summary>
    private static int RowVersionOf(int version) => version;

    private async Task<Guid> RegisterCaseAsync(string letterNumber) =>
        SliceFixture.Succeeded(await fixture.Cases.CreateAsync(fixture.Chief, new CreateCaseCommand(
            fixture.NewOperation(), DemoData.RegionalDevelopmentOfficeId, "Yekun mərhələ (test)", "Sintetik test məlumatı",
            letterNumber, null, Today.AddDays(-45), Today.AddDays(-44), DemoData.WorkerAUserId, null)));

    private async Task<Guid> RegisterRequestAsync(Guid caseId, string letterNumber, int sentDaysAgo, DateOnly? dueDate, Guid? target = null, Guid? sourceRequirementId = null) =>
        SliceFixture.Succeeded(await fixture.Workflow.RegisterRequestAsync(fixture.Chief, new RegisterRequestCommand(
            fixture.NewOperation(), caseId, target ?? DemoData.ArchitectureAuthorityId, "Rəy sorğusu", letterNumber,
            Today.AddDays(-sentDaysAgo), dueDate, sourceRequirementId, null)));

    private async Task<Guid> RespondAsync(Guid caseId, Guid requestId, string letterNumber, string outcome = "APPROVED") =>
        SliceFixture.Succeeded(await fixture.Workflow.RegisterResponseAsync(fixture.Chief, new RegisterResponseCommand(
            fixture.NewOperation(), caseId, requestId, letterNumber, null, Today.AddDays(-2), Today.AddDays(-1),
            "OPINION", outcome, true, "Sintetik cavab")));

    /// <summary>A case whose only request is answered and closed: ready for a final result (§7.4).</summary>
    private async Task<(Guid CaseId, Guid RequestId)> ResolvedCaseAsync(string incoming, string outgoing, string response)
    {
        var caseId = await RegisterCaseAsync(incoming);
        var requestId = await RegisterRequestAsync(caseId, outgoing, sentDaysAgo: 20, dueDate: null);
        await RespondAsync(caseId, requestId, response);

        var request = (await fixture.WorkspaceAsync(caseId)).FindRequest(requestId)!;
        SliceFixture.Succeeded(await fixture.Workflow.CloseRequestAsync(
            fixture.Chief, new CloseRequestCommand(caseId, requestId, request.RowVersion, "Cavab alındı.")));
        return (caseId, requestId);
    }

    private async Task<Guid> CaseWithOpenNonBlockingRequirementAsync(string incoming, string outgoing, string response) =>
        await CaseWithRequirementAsync(incoming, outgoing, response, blocking: false);

    private async Task<Guid> CaseWithOpenBlockingRequirementAsync(string incoming, string outgoing, string response) =>
        await CaseWithRequirementAsync(incoming, outgoing, response, blocking: true);

    /// <summary>
    /// A case with one answered, closed request that raised one still-open requirement. The request is closed first,
    /// which a blocking requirement would prevent (§3.4), so the requirement is raised afterwards — exactly the R9
    /// path that returns a closed request to ANSWERED when new blocking work appears.
    /// </summary>
    private async Task<Guid> CaseWithRequirementAsync(string incoming, string outgoing, string response, bool blocking)
    {
        var (caseId, requestId) = await ResolvedCaseAsync(incoming, outgoing, response);
        var responseId = (await fixture.WorkspaceAsync(caseId)).FindRequest(requestId)!.Responses.Single().Id;

        SliceFixture.Succeeded(await fixture.Workflow.CreateRequirementAsync(fixture.Chief, new CreateRequirementCommand(
            fixture.NewOperation(), caseId, responseId, "Əlavə sənəd", null, blocking, null, DemoData.UtilityAuthorityId)));

        if (blocking)
        {
            return caseId;
        }

        // A non-blocking requirement does not reopen its request, so nothing needs re-closing (§9.2, one definition
        // of blocking). Asserting it here keeps the fixture honest about what it built.
        Assert.Equal(RequestStatus.Closed, (await fixture.WorkspaceAsync(caseId)).FindRequest(requestId)!.Status);
        return caseId;
    }

    private async Task<Guid> DraftAsync(Guid caseId, string decisionType) =>
        SliceFixture.Succeeded(await fixture.Lifecycle.DraftFinalResultAsync(fixture.Chief, new DraftFinalResultCommand(
            fixture.NewOperation(), caseId, decisionType, "Sintetik qərar mətni", "Sintetik əsaslandırma", null)));

    private async Task<Guid> IssuedResultAsync(Guid caseId, string decisionType)
    {
        var resultId = await DraftAsync(caseId, decisionType);
        SliceFixture.Succeeded(await fixture.Lifecycle.IssueFinalResultAsync(
            fixture.Head, new IssueFinalResultCommand(caseId, resultId, RowVersionOf(1), null)));
        return resultId;
    }

    private async Task<ClosurePreview> PreviewAsync(Guid caseId, string closureType)
    {
        var preview = await fixture.LifecycleQueries.GetClosurePreviewAsync(fixture.Chief, caseId, closureType);
        Assert.True(preview.Succeeded, preview.Error?.Code);
        return preview.Value!;
    }

    private async Task<CommandResult<Guid>> CloseAsync(
        Guid caseId, string closureType, bool acknowledge = true, ActorContext? actor = null, string? overrideNote = null)
    {
        var workspace = await fixture.WorkspaceAsync(caseId);
        return await fixture.Lifecycle.CloseCaseAsync(actor ?? fixture.Chief, new CloseCaseCommand(
            caseId, workspace.Header.RowVersion, closureType, "Sintetik bağlanma qeydi", acknowledge, overrideNote));
    }

    private async Task PutOnHoldAsync(Guid caseId) =>
        await fixture.Database.ExecuteAsync($"""
            UPDATE rcs.case_record
            SET lifecycle_state = 'ON_HOLD', hold_reason_note = 'Müraciət edən məlumat təqdim etməlidir'
            WHERE id = '{caseId}'
            """);
}
