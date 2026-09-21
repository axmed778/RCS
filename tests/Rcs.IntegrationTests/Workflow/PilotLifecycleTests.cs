using Rcs.Application.Cases;
using Rcs.Application.Common;
using Rcs.Application.Documents;
using Rcs.Application.Lifecycle;
using Rcs.Application.Organizations;
using Rcs.Application.Workflow;
using Rcs.Domain.Documents;
using Rcs.Domain.Vocabulary;
using Rcs.IntegrationTests.TestSupport;

namespace Rcs.IntegrationTests.Workflow;

/// <summary>
/// The pilot employee's whole working life in one test, on a clean database with no demo data: sign in, register a
/// case from an incoming letter, send a request, record the answer, raise and fulfil a requirement backed by a real
/// document, decide, close, reopen. The point is not that each step works — other tests cover that — but that ONE real
/// account holding Worker + Chief + Head does all of it through the ordinary authorization checks, and that every row
/// carries that person's identity rather than a review actor's.
/// </summary>
public sealed class PilotLifecycleTests : IAsyncLifetime
{
    private const string Password = "kifayət qədər uzun keçid ifadəsi";
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);
    private SliceFixture fixture = null!;
    private Guid pilotId;

    public async ValueTask InitializeAsync()
    {
        fixture = await SliceFixture.CreateAsync(seedDemoData: false);
        await fixture.Bootstrap.CreateAdministratorAsync("tech.admin", "Texniki inzibatçı", null, Password);
        var pilot = await fixture.Bootstrap.CreateUserAsync("pilot.user", "Pilot İstifadəçi", null, "Baş mütəxəssis", Password, "tech.admin");
        pilotId = pilot.Value!.UserId;

        // A clean database does not know which organization the department itself is; installation records it once.
        Assert.True((await fixture.Department.SetOwnOrganizationAsync(
            "Bələdiyyə Şəhərsalma Şöbəsi (pilot)", "Şəhərsalma Şöbəsi", "MUNICIPAL_DEPARTMENT", "tech.admin")).Succeeded);

        // The three roles for the 10 days, granted the ordinary way: the TechAdmin performs the first Head grant
        // (PERMISSIONS.md §25.2 bootstrap), and the Head then grants the rest.
        await fixture.Bootstrap.GrantRoleAsync("pilot.user", BusinessRole.Head, "tech.admin");
        await fixture.Bootstrap.GrantRoleAsync("pilot.user", BusinessRole.Chief, "pilot.user");
        await fixture.Bootstrap.GrantRoleAsync("pilot.user", BusinessRole.Worker, "pilot.user");
    }

    public async ValueTask DisposeAsync() => await fixture.DisposeAsync();

    private ActorContext Pilot => new(pilotId, "10.0.0.5");

    /// <summary>The workspace as the pilot user sees it; the fixture helper reads as the demo Chief, who does not exist here.</summary>
    private async Task<CaseWorkspace> WorkspaceAsync(Guid caseId)
    {
        var result = await fixture.Queries.GetWorkspaceAsync(Pilot, caseId);
        Assert.True(result.Succeeded, result.Error?.Code);
        return result.Value!;
    }

    [Fact]
    public async Task OnePilotUserCanRunTheWholeCaseLifecycleAndEveryRecordIsTheirs()
    {
        // 0. A real sign-in. Everything below is done by the account that signed in.
        var signIn = await fixture.Authentication.SignInAsync("pilot.user", Password, "10.0.0.5");
        Assert.True(signIn.Succeeded, signIn.Outcome.ToString());
        Assert.Equal(new[] { BusinessRole.Worker, BusinessRole.Chief, BusinessRole.Head }.ToHashSet(), signIn.Profile!.Roles);

        // 1. The organizations this dossier involves.
        var applicant = SliceFixture.Succeeded(await fixture.Organizations.CreateAsync(Pilot,
            new CreateOrganizationCommand("Pilot Sifarişçi MMC (sintetik)", "Sifarişçi", "LEGAL_ENTITY", "PILOT-0001", null)));
        var authority = SliceFixture.Succeeded(await fixture.Organizations.CreateAsync(Pilot,
            new CreateOrganizationCommand("Pilot Kommunal İdarəsi (sintetik)", "Kommunal", "UTILITY_COMPANY", "PILOT-0002", null)));

        // 2. The case, registered from the incoming letter that started it. The pilot user is the responsible person.
        var caseId = SliceFixture.Succeeded(await fixture.Cases.CreateAsync(Pilot, new CreateCaseCommand(
            fixture.NewOperation(), applicant, "Pilot işi — yol çəkilişi", "Pilot üçün sınaq işi",
            "PILOT-IN-1/2026", null, Today.AddDays(-20), Today.AddDays(-19), pilotId, null)));

        var registered = await WorkspaceAsync(caseId);
        Assert.Equal(CaseLifecycleState.Registered, registered.Header.State);
        Assert.Equal("Pilot İstifadəçi", registered.Header.Responsible?.DisplayName);

        // 3. A request to the authority, carried by an outgoing letter.
        var requestId = SliceFixture.Succeeded(await fixture.Workflow.RegisterRequestAsync(Pilot, new RegisterRequestCommand(
            fixture.NewOperation(), caseId, authority, "Texniki şərtlər soruşulur", "PILOT-OUT-1/2026",
            Today.AddDays(-15), Today.AddDays(-5), null, null)));

        // 4. The answer, and the condition it imposes.
        var responseId = SliceFixture.Succeeded(await fixture.Workflow.RegisterResponseAsync(Pilot, new RegisterResponseCommand(
            fixture.NewOperation(), caseId, requestId, "PILOT-IN-2/2026", null, Today.AddDays(-3), Today.AddDays(-2),
            "ADDITIONAL_REQUIREMENT", ResponseOutcomeCodes.Conditional, true, "Şərtlərlə razılıq")));

        var requirementId = SliceFixture.Succeeded(await fixture.Workflow.CreateRequirementAsync(Pilot, new CreateRequirementCommand(
            fixture.NewOperation(), caseId, responseId, "Kabel planı təqdim edilsin", null, true, null, authority)));

        // 5. A real document, uploaded as the evidence that satisfies the requirement.
        using var bytes = new MemoryStream("%PDF-1.7\nPilot kabel planı\n%%EOF"u8.ToArray());
        var upload = await fixture.Documents.UploadDocumentAsync(Pilot, new UploadDocumentCommand(
            fixture.NewOperation(), caseId, new DocumentTarget(DocumentTargetKind.Requirement, requirementId),
            DocumentLinkRoleCodes.RequirementEvidence, "Kabel planı", DocumentKindCodes.Other, null, null, null,
            Note: "Pilot sənədi"), new UploadedContent(bytes, "kabel-plani.pdf", "application/pdf"));
        Assert.True(upload.Succeeded, upload.Error?.Code);

        // The bytes come back byte-for-byte through the authorized download path, for this exact version.
        var download = await fixture.DocumentQueries.OpenDownloadAsync(Pilot, caseId, upload.Value!.LinkId!.Value, upload.Value.VersionId);
        Assert.True(download.Succeeded, download.Error?.Code);
        await using (var content = download.Value!)
        {
            using var buffer = new MemoryStream();
            await content.Content.CopyToAsync(buffer);
            Assert.Equal("%PDF-1.7\nPilot kabel planı\n%%EOF"u8.ToArray(), buffer.ToArray());
        }

        var requirement = (await WorkspaceAsync(caseId)).AllRequirements.Single(item => item.Id == requirementId);
        SliceFixture.Succeeded(await fixture.Workflow.FulfillRequirementAsync(Pilot,
            new FulfillRequirementCommand(caseId, requirementId, requirement.RowVersion, null, "Sənəd təqdim edildi")));

        // 6. The request is finished with, so the case has nothing outstanding (closure guard D1 / G2).
        var answered = (await WorkspaceAsync(caseId)).AllRequests.Single(node => node.Id == requestId);
        SliceFixture.Succeeded(await fixture.Workflow.CloseRequestAsync(Pilot,
            new CloseRequestCommand(caseId, requestId, answered.RowVersion, "Cavab alındı, şərt yerinə yetirildi")));

        // 7. The decision: drafted and issued by the same person, because they really hold Head too (ADR-040).
        var resultId = SliceFixture.Succeeded(await fixture.Lifecycle.DraftFinalResultAsync(Pilot, new DraftFinalResultCommand(
            fixture.NewOperation(), caseId, "APPROVAL", "Razılıq verilir", "Bütün şərtlər yerinə yetirildi", null)));
        using var decision = new MemoryStream("%PDF-1.7\nPilot qərarı\n%%EOF"u8.ToArray());
        var decisionUpload = await fixture.Documents.UploadDocumentAsync(Pilot, new UploadDocumentCommand(
            fixture.NewOperation(), caseId, new DocumentTarget(DocumentTargetKind.FinalResult, resultId),
            DocumentLinkRoleCodes.FinalResultDocument, "İmzalanmış qərar", DocumentKindCodes.Other, null, null, null),
            new UploadedContent(decision, "qerar.pdf", "application/pdf"));
        Assert.True(decisionUpload.Succeeded, decisionUpload.Error?.Code);
        SliceFixture.Succeeded(await fixture.Lifecycle.IssueFinalResultAsync(Pilot,
            new IssueFinalResultCommand(caseId, resultId, 1, null)));

        // 8. Close, then reopen — the exceptional path the pilot is meant to exercise.
        var beforeClose = await WorkspaceAsync(caseId);
        SliceFixture.Succeeded(await fixture.Lifecycle.CloseCaseAsync(Pilot, new CloseCaseCommand(
            caseId, beforeClose.Header.RowVersion, "COMPLETED", "İş tamamlandı", true, null)));
        var closed = await WorkspaceAsync(caseId);
        Assert.Equal(CaseLifecycleState.Closed, closed.Header.State);

        SliceFixture.Succeeded(await fixture.Lifecycle.ReopenCaseAsync(Pilot,
            new ReopenCaseCommand(caseId, closed.Header.RowVersion, "Gec gələn məktub işi yenidən açır.")));
        Assert.Equal(CaseLifecycleState.Active, (await WorkspaceAsync(caseId)).Header.State);

        // Every user-attributed event in this case is the pilot employee's, and no review actor exists here at all.
        Assert.Equal(0L, await fixture.ScalarAsync<long>(
            $"SELECT count(*) FROM rcs.audit_event WHERE case_id = '{caseId}' AND actor_kind = 'USER' AND actor_user_id <> '{pilotId}'"));
        Assert.True(await fixture.ScalarAsync<long>(
            $"SELECT count(*) FROM rcs.audit_event WHERE case_id = '{caseId}' AND actor_user_id = '{pilotId}'") > 10);
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT count(*) FROM rcs.app_user WHERE username LIKE 'review.%'"));

        // And the roles that made it possible are recorded on every event, so the audit answers "as what?" later.
        Assert.Equal(0L, await fixture.ScalarAsync<long>(
            $"SELECT count(*) FROM rcs.audit_event WHERE actor_user_id = '{pilotId}' AND NOT ('HEAD' = ANY(actor_roles_snapshot))"));
    }
}
