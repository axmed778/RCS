using Rcs.Application.Cases;
using Rcs.Application.Common;
using Rcs.Application.Organizations;
using Rcs.Application.Workflow;
using Rcs.Domain.Progress;
using Rcs.Domain.Vocabulary;
using Rcs.Infrastructure.Development;
using Rcs.IntegrationTests.TestSupport;

namespace Rcs.IntegrationTests.Workflow;

/// <summary>
/// The Review MVP vertical slice, end to end against a real database: organization → case → request → response →
/// requirement → child request → fulfilment, with the derived progress and the workspace graph that result.
/// </summary>
public sealed class VerticalSliceTests : IAsyncLifetime
{
    private SliceFixture fixture = null!;

    public async ValueTask InitializeAsync() => fixture = await SliceFixture.CreateAsync();

    public async ValueTask DisposeAsync() => await fixture.DisposeAsync();

    private async Task<Guid> RegisterCaseAsync(string letterNumber, string title = "Nümunə iş", Guid? responsible = null)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return SliceFixture.Succeeded(await fixture.Cases.CreateAsync(fixture.Chief, new CreateCaseCommand(
            fixture.NewOperation(),
            DemoData.RegionalDevelopmentOfficeId,
            title,
            "Sintetik test məlumatı",
            letterNumber,
            null,
            today.AddDays(-20),
            today.AddDays(-19),
            responsible ?? DemoData.WorkerAUserId,
            null)));
    }

    private Task<Guid> RegisterRequestAsync(Guid caseId, Guid target, string letterNumber, int sentDaysAgo = 15, int? dueInDays = 10, Guid? sourceRequirementId = null)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return RegisterAsync(fixture.Workflow.RegisterRequestAsync(fixture.Chief, new RegisterRequestCommand(
            fixture.NewOperation(), caseId, target, "Rəy sorğusu", letterNumber,
            today.AddDays(-sentDaysAgo), dueInDays is { } days ? today.AddDays(days) : null, sourceRequirementId, null)));
    }

    private Task<Guid> RegisterResponseAsync(Guid caseId, Guid requestId, string letterNumber, string type, string outcome, bool conclusive)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return RegisterAsync(fixture.Workflow.RegisterResponseAsync(fixture.Chief, new RegisterResponseCommand(
            fixture.NewOperation(), caseId, requestId, letterNumber, null, today.AddDays(-3), today.AddDays(-2),
            type, outcome, conclusive, "Sintetik cavab")));
    }

    private static async Task<Guid> RegisterAsync(Task<CommandResult<Guid>> command) => SliceFixture.Succeeded(await command);

    // 1 -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task OrganizationIsCreatedAsMasterDataAndBecomesSelectable()
    {
        var before = await fixture.Organizations.ListActiveExternalAsync();

        var id = SliceFixture.Succeeded(await fixture.Organizations.CreateAsync(fixture.Chief,
            new CreateOrganizationCommand("Nümunə Su Təchizatı Orqanı (sintetik)", "Su Orqanı", "UTILITY_COMPANY", "TEST-0001", null)));

        var after = await fixture.Organizations.ListActiveExternalAsync();
        Assert.Equal(before.Count + 1, after.Count);
        Assert.Contains(after, organization => organization.Id == id && organization.Name == "Su Orqanı");
        Assert.Equal(1L, await fixture.ScalarAsync<long>(
            $"SELECT count(*) FROM rcs.audit_event WHERE entity_type = 'organization' AND entity_id = '{id}' AND action_code = 'CREATE'"));
    }

    // 2 -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CaseIsRegisteredWithItsIncomingLetterAndAResponsibleAssignment()
    {
        var caseId = await RegisterCaseAsync("TEST-IN-1/2026", "Torpaq sorğusu (test)");

        var workspace = await fixture.WorkspaceAsync(caseId);
        Assert.Equal(CaseLifecycleState.Registered, workspace.Header.State);
        Assert.Equal("Əməkdaş A (sintetik)", workspace.Header.Responsible?.DisplayName);
        Assert.Equal("TEST-IN-1/2026", workspace.InitiatingLetter?.LetterNumber);
        Assert.Equal(CorrespondenceDirection.Incoming, workspace.InitiatingLetter?.Direction);

        // Registration alone is not progress: no request has been issued yet (ladder row P3).
        Assert.Equal("P3", workspace.Progress.Headline.Code);
        Assert.Equal(1L, await fixture.ScalarAsync<long>(
            $"SELECT count(*) FROM rcs.case_state_change WHERE case_id = '{caseId}' AND from_state IS NULL AND to_state = 'REGISTERED'"));
    }

    // 3 -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RegisteringTheOutgoingLetterIssuesTheRequestAndActivatesTheCase()
    {
        var caseId = await RegisterCaseAsync("TEST-IN-2/2026");

        var requestId = await RegisterRequestAsync(caseId, DemoData.ArchitectureAuthorityId, "TEST-OUT-2/2026");

        var workspace = await fixture.WorkspaceAsync(caseId);
        var request = workspace.FindRequest(requestId)!;
        Assert.Equal(RequestStatus.Sent, request.Status);                    // R1b: SENT from birth, no send action
        Assert.NotNull(request.DispatchLetter);
        Assert.Equal(CorrespondenceDirection.Outgoing, request.DispatchLetter!.Direction);
        Assert.Equal(CaseLifecycleState.Active, workspace.Header.State);     // T2, a system consequence
        Assert.Equal(1L, await fixture.ScalarAsync<long>(
            $"SELECT count(*) FROM rcs.audit_event WHERE case_id = '{caseId}' AND entity_type = 'case' AND action_code = 'STATE_CHANGE' AND actor_kind = 'SYSTEM'"));
    }

    [Fact]
    public async Task OneOutgoingLetterMayCarrySeveralRequests()
    {
        var caseId = await RegisterCaseAsync("TEST-IN-3/2026");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var first = await RegisterRequestAsync(caseId, DemoData.ArchitectureAuthorityId, "TEST-OUT-3/2026");
        var second = SliceFixture.Succeeded(await fixture.Workflow.RegisterRequestAsync(fixture.Chief, new RegisterRequestCommand(
            fixture.NewOperation(), caseId, DemoData.ArchitectureAuthorityId, "İkinci sorğu", "TEST-OUT-3/2026",
            today.AddDays(-15), today.AddDays(10), null, null)));

        var workspace = await fixture.WorkspaceAsync(caseId);
        Assert.Equal(
            workspace.FindRequest(first)!.DispatchLetter!.Id,
            workspace.FindRequest(second)!.DispatchLetter!.Id);
    }

    // 4 -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RegisteringAConclusiveResponseAnswersTheRequestWithoutASeparateAction()
    {
        var caseId = await RegisterCaseAsync("TEST-IN-4/2026");
        var requestId = await RegisterRequestAsync(caseId, DemoData.ArchitectureAuthorityId, "TEST-OUT-4/2026");

        var responseId = await RegisterResponseAsync(caseId, requestId, "TEST-RESP-4/2026", "OPINION", ResponseOutcomeCodes.Approved, conclusive: true);

        var workspace = await fixture.WorkspaceAsync(caseId);
        Assert.Equal(RequestStatus.Answered, workspace.FindRequest(requestId)!.Status);   // R3
        var response = workspace.FindResponse(responseId)!;
        Assert.Equal("OPINION", response.TypeCode);
        Assert.Equal(ResponseOutcomeCodes.Approved, response.OutcomeCode);                // type and outcome stay separate
        Assert.True(response.IsConclusive);
        Assert.Equal(RequestProgressState.AnsweredCloseable, workspace.Progress.Requests[requestId].State);
    }

    [Fact]
    public async Task ANonConclusiveResponseLeavesTheRequestWaiting()
    {
        var caseId = await RegisterCaseAsync("TEST-IN-5/2026");
        var requestId = await RegisterRequestAsync(caseId, DemoData.EmergencyAuthorityId, "TEST-OUT-5/2026");

        await RegisterResponseAsync(caseId, requestId, "TEST-RESP-5/2026", "ACKNOWLEDGEMENT", ResponseOutcomeCodes.NotApplicable, conclusive: false);

        var workspace = await fixture.WorkspaceAsync(caseId);
        Assert.Equal(RequestStatus.Sent, workspace.FindRequest(requestId)!.Status);
        Assert.Equal(RequestProgressState.PartiallyAnswered, workspace.Progress.Requests[requestId].State);
    }

    // 5, 6, 7, 8, 9 -------------------------------------------------------------------------------------

    [Fact]
    public async Task TheNestedChainRunsFromRequirementToChildRequestToFulfilment()
    {
        var caseId = await RegisterCaseAsync("TEST-IN-6/2026", "İç-içə tələb zənciri (test)");
        var architecture = await RegisterRequestAsync(caseId, DemoData.ArchitectureAuthorityId, "TEST-OUT-6/2026");
        var conditional = await RegisterResponseAsync(caseId, architecture, "TEST-RESP-6/2026", "ADDITIONAL_REQUIREMENT", ResponseOutcomeCodes.Conditional, conclusive: false);

        // 5 — a requirement raised by that response, blocking, addressed to another authority.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var requirementId = SliceFixture.Succeeded(await fixture.Workflow.CreateRequirementAsync(fixture.Chief, new CreateRequirementCommand(
            fixture.NewOperation(), caseId, conditional, "Kommunikasiya xəritəsi", "Test təsviri", true, today.AddDays(5), DemoData.UtilityAuthorityId)));

        var afterRequirement = await fixture.WorkspaceAsync(caseId);
        var requirement = afterRequirement.FindRequirement(requirementId)!;
        Assert.Equal(RequirementStatus.Open, requirement.Status);
        Assert.Equal(conditional, requirement.SourceResponseId);                         // why it exists
        Assert.Equal(DemoData.ArchitectureAuthorityId, requirement.RaisedBy?.Id);
        Assert.True(requirement.IsBlocking);
        // 8a — an open blocking requirement with no child request out: the department owes the next move (P8).
        Assert.Equal("P8", afterRequirement.Progress.Headline.Code);

        // 6 — the child request that satisfies it; issuing it starts the requirement (Q2, a system consequence).
        var utility = await RegisterRequestAsync(caseId, DemoData.UtilityAuthorityId, "TEST-OUT-6B/2026", sentDaysAgo: 10, dueInDays: 12, sourceRequirementId: requirementId);

        var afterChild = await fixture.WorkspaceAsync(caseId);
        Assert.Equal(RequirementStatus.InProgress, afterChild.FindRequirement(requirementId)!.Status);
        Assert.Equal(requirementId, afterChild.FindRequest(utility)!.SourceRequirementId);
        // 8b — now an authority owes us the map (P12).
        Assert.Equal("P12", afterChild.Progress.Headline.Code);

        // 7 — the child request's answer is the evidence that fulfils the requirement.
        var utilityResponse = await RegisterResponseAsync(caseId, utility, "TEST-RESP-6B/2026", "INFORMATION", ResponseOutcomeCodes.NotApplicable, conclusive: true);
        var beforeFulfil = await fixture.WorkspaceAsync(caseId);
        SliceFixture.Succeeded(await fixture.Workflow.FulfillRequirementAsync(fixture.Chief, new FulfillRequirementCommand(
            caseId, requirementId, beforeFulfil.FindRequirement(requirementId)!.RowVersion, utilityResponse, "Xəritə alındı (test)")));

        // 9 — the workspace returns the whole graph: request → response → requirement → child request → response.
        var workspace = await fixture.WorkspaceAsync(caseId);
        var topLevel = Assert.Single(workspace.TopLevelRequests, request => request.Id == architecture);
        var response = Assert.Single(topLevel.Responses, r => r.Id == conditional);
        var nested = Assert.Single(response.Requirements, q => q.Id == requirementId);
        var child = Assert.Single(nested.ChildRequests, r => r.Id == utility);
        Assert.Single(child.Responses, r => r.Id == utilityResponse);

        Assert.Equal(RequirementStatus.Fulfilled, nested.Status);
        Assert.Equal(RequirementProgressState.Fulfilled, workspace.Progress.Requirements[requirementId].State);
        var evidence = Assert.Single(nested.Evidence);
        Assert.Equal(utilityResponse, evidence.ResponseId);

        // The parent request is not closed automatically — it is surfaced as ready to close (WORKFLOW.md §5.6).
        Assert.Equal(RequestStatus.Sent, workspace.FindRequest(architecture)!.Status);
    }

    // 8 -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DerivedProgressFollowsTheWorkAndIsNeverStored()
    {
        var caseId = await RegisterCaseAsync("TEST-IN-7/2026");
        Assert.Equal("P3", (await fixture.WorkspaceAsync(caseId)).Progress.Headline.Code);

        var requestId = await RegisterRequestAsync(caseId, DemoData.PropertyAuthorityId, "TEST-OUT-7/2026");
        var waiting = await fixture.WorkspaceAsync(caseId);
        Assert.Equal("P13", waiting.Progress.Headline.Code);                                  // waiting for one authority
        Assert.Equal("Əmlak Orqanı", waiting.Progress.Headline.Arguments[0]);
        Assert.Equal(1, waiting.Progress.Summary.RequestsConsidered);
        Assert.Equal(0, waiting.Progress.Summary.RequestsAnswered);

        await RegisterResponseAsync(caseId, requestId, "TEST-RESP-7/2026", "OPINION", ResponseOutcomeCodes.Approved, conclusive: true);
        var answered = await fixture.WorkspaceAsync(caseId);
        Assert.Equal(1, answered.Progress.Summary.RequestsAnswered);

        SliceFixture.Succeeded(await fixture.Workflow.CloseRequestAsync(fixture.Chief, new CloseRequestCommand(
            caseId, requestId, answered.FindRequest(requestId)!.RowVersion, "Test bağlanması")));

        var closed = await fixture.WorkspaceAsync(caseId);
        Assert.Equal("P16", closed.Progress.Headline.Code);                                   // ready for the final result
        Assert.True(closed.Progress.IsReadyForFinalResult);

        // Nothing of this is a stored column: no progress text anywhere in the case row.
        Assert.Equal(0L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'rcs' AND table_name = 'case_record' AND column_name IN ('progress', 'current_progress', 'progress_text')"));
    }

    // The four terminal outcomes stay distinct ------------------------------------------------------------

    [Fact]
    public async Task ARequirementThatNoLongerAppliesIsVoidedAndOneThatIsReleasedIsWaived()
    {
        var caseId = await RegisterCaseAsync("TEST-IN-8/2026");
        var requestId = await RegisterRequestAsync(caseId, DemoData.ArchitectureAuthorityId, "TEST-OUT-8/2026");
        var responseId = await RegisterResponseAsync(caseId, requestId, "TEST-RESP-8/2026", "ADDITIONAL_REQUIREMENT", ResponseOutcomeCodes.Conditional, conclusive: false);

        var waived = await CreateRequirementAsync(caseId, responseId, "Güzəşt ediləcək tələb");
        var voided = await CreateRequirementAsync(caseId, responseId, "Etibarsız sayılacaq tələb");
        var failed = await CreateRequirementAsync(caseId, responseId, "Alınmayan tələb");

        var workspace = await fixture.WorkspaceAsync(caseId);
        SliceFixture.Succeeded(await fixture.Workflow.WaiveRequirementAsync(fixture.Chief, new WaiveRequirementCommand(
            caseId, waived, workspace.FindRequirement(waived)!.RowVersion, "AUTHORISED_TO_PROCEED", "Səlahiyyətli qərar (test)")));
        SliceFixture.Succeeded(await fixture.Workflow.VoidRequirementAsync(fixture.Chief, new VoidRequirementCommand(
            caseId, voided, workspace.FindRequirement(voided)!.RowVersion, VoidReasonCodes.NoLongerRequired, "Artıq tələb olunmur (test)", responseId)));
        SliceFixture.Succeeded(await fixture.Workflow.FailRequirementAsync(fixture.Chief, new FailRequirementCommand(
            caseId, failed, workspace.FindRequirement(failed)!.RowVersion, "Əldə edilə bilmədi (test)")));

        var resolved = await fixture.WorkspaceAsync(caseId);
        Assert.Equal(RequirementStatus.Waived, resolved.FindRequirement(waived)!.Status);
        Assert.Equal(RequirementStatus.Void, resolved.FindRequirement(voided)!.Status);
        Assert.Equal(RequirementStatus.Failed, resolved.FindRequirement(failed)!.Status);
        Assert.Equal("AUTHORISED_TO_PROCEED", resolved.FindRequirement(waived)!.WaiverReasonCode);
        Assert.Equal(VoidReasonCodes.NoLongerRequired, resolved.FindRequirement(voided)!.VoidReasonCode);
        Assert.Equal(responseId, resolved.FindRequirement(voided)!.VoidSourceResponseId);

        // A failed blocking requirement is what the case headline must show: it needs a decision (P5).
        Assert.Equal("P5", resolved.Progress.Headline.Code);
        Assert.Equal(1, resolved.Progress.Summary.Waived);
        Assert.Equal(1, resolved.Progress.Summary.Failed);
    }

    [Fact]
    public async Task AWaiverWithoutAReasonIsRefused()
    {
        var caseId = await RegisterCaseAsync("TEST-IN-9/2026");
        var requestId = await RegisterRequestAsync(caseId, DemoData.ArchitectureAuthorityId, "TEST-OUT-9/2026");
        var responseId = await RegisterResponseAsync(caseId, requestId, "TEST-RESP-9/2026", "ADDITIONAL_REQUIREMENT", ResponseOutcomeCodes.Conditional, conclusive: false);
        var requirementId = await CreateRequirementAsync(caseId, responseId, "Tələb");
        var workspace = await fixture.WorkspaceAsync(caseId);

        var result = await fixture.Workflow.WaiveRequirementAsync(fixture.Chief, new WaiveRequirementCommand(
            caseId, requirementId, workspace.FindRequirement(requirementId)!.RowVersion, string.Empty, "Qeyd"));

        Assert.False(result.Succeeded);
        Assert.Equal(CommandErrorKind.RuleViolation, result.Error!.Kind);
        Assert.Equal("requirement.waiver_reason_required", result.Error.Code);
    }

    // Closure guard --------------------------------------------------------------------------------------

    [Fact]
    public async Task ARequestCannotCloseWhileABlockingRequirementItRaisedIsOpen()
    {
        var caseId = await RegisterCaseAsync("TEST-IN-10/2026");
        var requestId = await RegisterRequestAsync(caseId, DemoData.ArchitectureAuthorityId, "TEST-OUT-10/2026");
        var responseId = await RegisterResponseAsync(caseId, requestId, "TEST-RESP-10/2026", "OPINION", ResponseOutcomeCodes.Conditional, conclusive: true);
        await CreateRequirementAsync(caseId, responseId, "Bloklayıcı tələb");

        var workspace = await fixture.WorkspaceAsync(caseId);
        var result = await fixture.Workflow.CloseRequestAsync(fixture.Chief, new CloseRequestCommand(
            caseId, requestId, workspace.FindRequest(requestId)!.RowVersion, null));

        Assert.False(result.Succeeded);
        Assert.Equal("request.blocking_requirements_open", result.Error!.Code);
    }

    // Concurrency and retries -----------------------------------------------------------------------------

    [Fact]
    public async Task AStaleRowVersionIsAConflictAndNotASilentOverwrite()
    {
        var caseId = await RegisterCaseAsync("TEST-IN-11/2026");
        var requestId = await RegisterRequestAsync(caseId, DemoData.ArchitectureAuthorityId, "TEST-OUT-11/2026");
        var responseId = await RegisterResponseAsync(caseId, requestId, "TEST-RESP-11/2026", "ADDITIONAL_REQUIREMENT", ResponseOutcomeCodes.Conditional, conclusive: false);
        var requirementId = await CreateRequirementAsync(caseId, responseId, "Tələb");
        var stale = (await fixture.WorkspaceAsync(caseId)).FindRequirement(requirementId)!.RowVersion;

        SliceFixture.Succeeded(await fixture.Workflow.StartRequirementAsync(fixture.Chief, new StartRequirementCommand(caseId, requirementId, stale)));
        var second = await fixture.Workflow.FulfillRequirementAsync(fixture.Chief, new FulfillRequirementCommand(
            caseId, requirementId, stale, null, "Köhnəlmiş versiya ilə cəhd"));

        Assert.False(second.Succeeded);
        Assert.Equal(CommandErrorKind.Conflict, second.Error!.Kind);
        Assert.Equal(RequirementStatus.InProgress, (await fixture.WorkspaceAsync(caseId)).FindRequirement(requirementId)!.Status);
    }

    [Fact]
    public async Task AResubmittedFormWithTheSameOperationIdCreatesOneCase()
    {
        var operation = fixture.NewOperation();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var command = new CreateCaseCommand(operation, DemoData.LandCommissionId, "Təkrar göndərmə (test)", null,
            "TEST-IN-12/2026", null, today.AddDays(-2), null, null, null);

        var first = SliceFixture.Succeeded(await fixture.Cases.CreateAsync(fixture.Chief, command));
        var second = SliceFixture.Succeeded(await fixture.Cases.CreateAsync(fixture.Chief, command));

        Assert.Equal(first, second);
        Assert.Equal(1L, await fixture.ScalarAsync<long>(
            "SELECT count(*) FROM rcs.correspondence WHERE letter_number = 'TEST-IN-12/2026'"));
    }

    // Authorization --------------------------------------------------------------------------------------

    [Fact]
    public async Task AWorkerMayNotWaiveARequirementAndTheRefusalIsAudited()
    {
        var caseId = await RegisterCaseAsync("TEST-IN-13/2026", responsible: DemoData.WorkerAUserId);
        var requestId = await RegisterRequestAsync(caseId, DemoData.ArchitectureAuthorityId, "TEST-OUT-13/2026");
        var responseId = await RegisterResponseAsync(caseId, requestId, "TEST-RESP-13/2026", "ADDITIONAL_REQUIREMENT", ResponseOutcomeCodes.Conditional, conclusive: false);
        var requirementId = await CreateRequirementAsync(caseId, responseId, "Tələb");
        var workspace = await fixture.WorkspaceAsync(caseId);
        var worker = new ActorContext(DemoData.WorkerAUserId, "integration-test");

        var result = await fixture.Workflow.WaiveRequirementAsync(worker, new WaiveRequirementCommand(
            caseId, requirementId, workspace.FindRequirement(requirementId)!.RowVersion, "AUTHORISED_TO_PROCEED", "Qeyd"));

        Assert.False(result.Succeeded);
        Assert.Equal(CommandErrorKind.Forbidden, result.Error!.Kind);
        Assert.Equal(RequirementStatus.Open, (await fixture.WorkspaceAsync(caseId)).FindRequirement(requirementId)!.Status);
        Assert.Equal(1L, await fixture.ScalarAsync<long>(
            $"SELECT count(*) FROM rcs.audit_event WHERE action_code = 'PERMISSION_DENIED' AND entity_id = '{requirementId}' AND actor_user_id = '{DemoData.WorkerAUserId}'"));
    }

    [Fact]
    public async Task AnAssignedWorkerMayStillFulfilTheirOwnCaseWork()
    {
        var caseId = await RegisterCaseAsync("TEST-IN-14/2026", responsible: DemoData.WorkerAUserId);
        var requestId = await RegisterRequestAsync(caseId, DemoData.ArchitectureAuthorityId, "TEST-OUT-14/2026");
        var responseId = await RegisterResponseAsync(caseId, requestId, "TEST-RESP-14/2026", "ADDITIONAL_REQUIREMENT", ResponseOutcomeCodes.Conditional, conclusive: false);
        var requirementId = await CreateRequirementAsync(caseId, responseId, "Tələb");
        var workspace = await fixture.WorkspaceAsync(caseId);
        var worker = new ActorContext(DemoData.WorkerAUserId, "integration-test");

        var result = await fixture.Workflow.FulfillRequirementAsync(worker, new FulfillRequirementCommand(
            caseId, requirementId, workspace.FindRequirement(requirementId)!.RowVersion, null, "Sənəd təqdim edildi (test)"));

        Assert.True(result.Succeeded, result.Error?.Code);
        Assert.Equal(RequirementStatus.Fulfilled, (await fixture.WorkspaceAsync(caseId)).FindRequirement(requirementId)!.Status);
    }

    // Demo data -------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheDemoSeedIsSafeToRerun()
    {
        var casesBefore = await fixture.ScalarAsync<long>("SELECT count(*) FROM rcs.case_record");
        var organizationsBefore = await fixture.ScalarAsync<long>("SELECT count(*) FROM rcs.organization");

        var created = await fixture.Seeder.SeedAsync(isDevelopmentEnvironment: true);

        Assert.False(created);
        Assert.Equal(casesBefore, await fixture.ScalarAsync<long>("SELECT count(*) FROM rcs.case_record"));
        Assert.Equal(organizationsBefore, await fixture.ScalarAsync<long>("SELECT count(*) FROM rcs.organization"));
    }

    [Fact]
    public async Task TheDemoSeedRefusesToRunOutsideDevelopment()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Seeder.SeedAsync(isDevelopmentEnvironment: false));
        Assert.Contains("Development", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheNestedDemoCaseShowsTheWholeChainImmediately()
    {
        var cases = await fixture.Queries.ListAsync(fixture.Chief);
        var nested = Assert.Single(cases, item => item.Title.Contains("Torpaq sahəsinin", StringComparison.Ordinal));

        var workspace = await fixture.WorkspaceAsync(nested.Id);
        var architecture = Assert.Single(workspace.TopLevelRequests);
        var conditional = Assert.Single(architecture.Responses, response => response.OutcomeCode == ResponseOutcomeCodes.Conditional);
        var requirement = Assert.Single(conditional.Requirements);
        var child = Assert.Single(requirement.ChildRequests);

        Assert.Equal(RequirementStatus.Fulfilled, requirement.Status);
        Assert.Equal(RequestStatus.Closed, child.Status);
        Assert.Single(child.Responses);
        Assert.NotEmpty(workspace.Activity);
    }

    private async Task<Guid> CreateRequirementAsync(Guid caseId, Guid responseId, string title) =>
        SliceFixture.Succeeded(await fixture.Workflow.CreateRequirementAsync(fixture.Chief, new CreateRequirementCommand(
            fixture.NewOperation(), caseId, responseId, title, null, true, null, DemoData.UtilityAuthorityId)));
}
