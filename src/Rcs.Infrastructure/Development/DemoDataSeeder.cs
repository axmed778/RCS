using Microsoft.Extensions.Logging;
using Rcs.Application.Cases;
using Rcs.Application.Common;
using Rcs.Application.Idempotency;
using Rcs.Application.Persistence;
using Rcs.Application.Workflow;
using Rcs.Domain.Vocabulary;
using Rcs.Infrastructure.Audit;
using Rcs.Infrastructure.Persistence;

namespace Rcs.Infrastructure.Development;

/// <summary>The identities the Review build's synthetic data uses. Fixed ids keep the seed deterministic and re-runnable.</summary>
public static class DemoData
{
    public const string ReviewActorUsername = "review.demo";

    /// <summary>
    /// The second synthetic identity of the review build: a Head, so the acts reserved to the highest business
    /// authority — approving a final result, overriding a closure guard — can be demonstrated as themselves rather
    /// than by widening what a Chief may do (ADR-040; PERMISSIONS.md §23).
    /// </summary>
    public const string ReviewHeadUsername = "review.head";

    public static readonly Guid ReviewActorUserId = new("01995c10-0001-7000-8000-000000000001");
    public static readonly Guid WorkerAUserId = new("01995c10-0001-7000-8000-000000000002");
    public static readonly Guid WorkerBUserId = new("01995c10-0001-7000-8000-000000000003");
    public static readonly Guid ReviewHeadUserId = new("01995c10-0001-7000-8000-000000000004");

    public static readonly Guid OwnOrganizationId = new("01995c10-0002-7000-8000-000000000001");
    public static readonly Guid ArchitectureAuthorityId = new("01995c10-0002-7000-8000-000000000002");
    public static readonly Guid EmergencyAuthorityId = new("01995c10-0002-7000-8000-000000000003");
    public static readonly Guid PropertyAuthorityId = new("01995c10-0002-7000-8000-000000000004");
    public static readonly Guid UtilityAuthorityId = new("01995c10-0002-7000-8000-000000000005");
    public static readonly Guid InfrastructureAuthorityId = new("01995c10-0002-7000-8000-000000000006");
    public static readonly Guid RegionalDevelopmentOfficeId = new("01995c10-0002-7000-8000-000000000007");
    public static readonly Guid LandCommissionId = new("01995c10-0002-7000-8000-000000000008");
}

/// <summary>
/// DEVELOPMENT ONLY. Creates synthetic organizations, synthetic users and two demonstration cases — one with three
/// parallel authority branches, one with a nested requirement and its child request — by driving the real application
/// services, so every row, consequence and audit event is produced the way the application produces them.
/// </summary>
/// <remarks>
/// Deterministic and safe to re-run: each case is keyed by its incoming letter number and is skipped when already
/// present. It never runs by itself: the caller must prove it is a Development environment (SECURITY.md §18.4 — the
/// data is synthetic, and no production database may ever be seeded).
/// </remarks>
public sealed class DemoDataSeeder(
    IUnitOfWorkFactory unitOfWorkFactory,
    ICaseService cases,
    ICaseQueries caseQueries,
    IWorkflowService workflow,
    AuditWriter audit,
    Rcs.Application.Identifiers.IIdGenerator ids,
    BusinessCalendar calendar,
    ILogger<DemoDataSeeder> logger)
{
    private sealed record OrganizationSeed(Guid Id, string OfficialName, string ShortName, string TypeCode, (string Alias, string Type, string? Language)[] Aliases);

    public async Task<bool> SeedAsync(bool isDevelopmentEnvironment, CancellationToken cancellationToken = default)
    {
        if (!isDevelopmentEnvironment)
        {
            throw new InvalidOperationException(
                "The demo seed is Development-only synthetic data and must never run in another environment (SECURITY.md §18.4).");
        }

        await SeedMasterDataAsync(cancellationToken);

        var actor = new ActorContext(DemoData.ReviewActorUserId, "demo-seed");
        var seededAnything = false;
        seededAnything |= await SeedParallelBranchCaseAsync(actor, cancellationToken);
        seededAnything |= await SeedNestedRequirementCaseAsync(actor, cancellationToken);
        seededAnything |= await SeedOursToActCaseAsync(actor, cancellationToken);
        seededAnything |= await SeedReadyForDecisionCaseAsync(actor, cancellationToken);

        logger.LogInformation(seededAnything ? "Demo data seeded." : "Demo data was already present; nothing was created.");
        return seededAnything;
    }

    // -------------------------------------------------------------- master data

    private async Task SeedMasterDataAsync(CancellationToken cancellationToken)
    {
        await using var unitOfWork = (PostgresUnitOfWork)await unitOfWorkFactory.BeginAsync(cancellationToken);
        var correlationId = ids.NewId();

        async Task User(Guid id, string username, string displayName, string jobTitle, string roleCode)
        {
            var inserted = await unitOfWork.Command("""
                    INSERT INTO rcs.app_user (id, username, full_name, display_name, job_title, status, auth_source)
                    VALUES (@id, @username, @name, @name, @job_title, 'ACTIVE', 'LOCAL')
                    ON CONFLICT (id) DO NOTHING
                    """)
                .With("id", id).With("username", username).With("name", displayName).With("job_title", jobTitle)
                .ExecuteAsync(cancellationToken);
            if (inserted == 0)
            {
                return;
            }

            await unitOfWork.Command("""
                    INSERT INTO rcs.user_role (id, user_id, role_id, valid_from)
                    VALUES (@id, @user, (SELECT id FROM rcs.role WHERE code = @role), @from)
                    """)
                .With("id", ids.NewId()).With("user", id).With("role", roleCode).With("from", calendar.Now.AddYears(-1))
                .ExecuteAsync(cancellationToken);

            await audit.WriteJobAsync(unitOfWork, correlationId, new AuditEntry(
                AuditActionCodes.Create, AuditEntityTypes.User, id, 1, null,
                After: new { username, display_name = displayName, status = "ACTIVE", role = roleCode }), cancellationToken);
        }

        await User(DemoData.ReviewActorUserId, DemoData.ReviewActorUsername, "Nümayiş istifadəçisi (sintetik)", "Şöbə rəisi", "CHIEF");
        await User(DemoData.WorkerAUserId, "emekdas.a", "Əməkdaş A (sintetik)", "Baş mütəxəssis", "WORKER");
        await User(DemoData.WorkerBUserId, "emekdas.b", "Əməkdaş B (sintetik)", "Aparıcı mütəxəssis", "WORKER");
        await User(DemoData.ReviewHeadUserId, DemoData.ReviewHeadUsername, "Nümayiş rəhbəri (sintetik)", "İdarə rəisi", "HEAD");

        OrganizationSeed[] organizations =
        [
            new(DemoData.OwnOrganizationId, "Bələdiyyə Şəhərsalma Şöbəsi (sintetik)", "Şəhərsalma Şöbəsi", "MUNICIPAL_DEPARTMENT", []),
            new(DemoData.ArchitectureAuthorityId, "Memarlıq Orqanı (sintetik)", "Memarlıq Orqanı", "STATE_AUTHORITY",
                [("MO", "ABBREVIATION", "az"), ("Архитектурный орган", "TRANSLITERATION", "ru")]),
            new(DemoData.EmergencyAuthorityId, "Fövqəladə Hallar Orqanı (sintetik)", "FH Orqanı", "STATE_AUTHORITY",
                [("FHO", "ABBREVIATION", "az")]),
            new(DemoData.PropertyAuthorityId, "Əmlak Orqanı (sintetik)", "Əmlak Orqanı", "STATE_AUTHORITY", []),
            new(DemoData.UtilityAuthorityId, "Kommunal Xidmətlər Orqanı (sintetik)", "Kommunal Orqan", "UTILITY_COMPANY",
                [("Kommunal Təsərrüfat İdarəsi", "FORMER_NAME", "az"), ("Коммунальный орган", "TRANSLITERATION", "ru")]),
            new(DemoData.InfrastructureAuthorityId, "İnfrastruktur Orqanı (sintetik)", "İnfrastruktur Orqanı", "STATE_AUTHORITY", []),
            new(DemoData.RegionalDevelopmentOfficeId, "Regional İnkişaf İdarəsi (sintetik)", "Regional İdarə", "STATE_AUTHORITY", []),
            new(DemoData.LandCommissionId, "Torpaq Məsələləri Komissiyası (sintetik)", "Torpaq Komissiyası", "STATE_AUTHORITY", []),
        ];

        foreach (var organization in organizations)
        {
            var inserted = await unitOfWork.Command("""
                    INSERT INTO rcs.organization (id, official_name, short_name, organization_type_id, is_own_organization, created_by_user_id)
                    VALUES (@id, @official_name, @short_name, (SELECT id FROM rcs.organization_type WHERE code = @type), @is_own, @actor)
                    ON CONFLICT (id) DO NOTHING
                    """)
                .With("id", organization.Id)
                .With("official_name", organization.OfficialName)
                .With("short_name", organization.ShortName)
                .With("type", organization.TypeCode)
                .With("is_own", organization.Id == DemoData.OwnOrganizationId)
                .With("actor", DemoData.ReviewActorUserId)
                .ExecuteAsync(cancellationToken);
            if (inserted == 0)
            {
                continue;
            }

            foreach (var (alias, aliasType, language) in organization.Aliases)
            {
                await unitOfWork.Command("""
                        INSERT INTO rcs.organization_alias (id, organization_id, alias, alias_type, alias_language, created_by_user_id)
                        VALUES (@id, @organization, @alias, @type, @language, @actor)
                        """)
                    .With("id", ids.NewId()).With("organization", organization.Id).With("alias", alias)
                    .With("type", aliasType).With("language", language).With("actor", DemoData.ReviewActorUserId)
                    .ExecuteAsync(cancellationToken);
            }

            await audit.WriteJobAsync(unitOfWork, correlationId, new AuditEntry(
                AuditActionCodes.Create, AuditEntityTypes.Organization, organization.Id, 1, null,
                After: new { official_name = organization.OfficialName, short_name = organization.ShortName, type = organization.TypeCode }), cancellationToken);
        }

        await unitOfWork.CommitAsync(cancellationToken);
    }

    // -------------------------------------------------------------- demo cases

    /// <summary>Demo case 1 — one incoming request, three parallel authority branches, two of them answered.</summary>
    private async Task<bool> SeedParallelBranchCaseAsync(ActorContext actor, CancellationToken cancellationToken)
    {
        const string incomingLetter = "Rİİ-118/2026";
        if (await CaseExistsAsync(incomingLetter, cancellationToken))
        {
            return false;
        }

        var today = calendar.Today;
        var caseId = await Required(await cases.CreateAsync(actor, new CreateCaseCommand(
            NewOperation(),
            DemoData.RegionalDevelopmentOfficeId,
            "Yaşayış binasının yenidən qurulmasına rəy (nümunə)",
            "Nümunə ünvan 12 — mövcud yaşayış binasının yenidən qurulması üçün şəhərsalma rəyinin verilməsi.",
            incomingLetter,
            "DAX-401/2026",
            today.AddDays(-30),
            today.AddDays(-29),
            DemoData.WorkerAUserId,
            "Sintetik nümayiş məlumatı."), cancellationToken));

        var architecture = await Required(await workflow.RegisterRequestAsync(actor, new RegisterRequestCommand(
            NewOperation(), caseId, DemoData.ArchitectureAuthorityId, "Memarlıq-planlaşdırma rəyi",
            "ŞŞ-214/2026", today.AddDays(-27), today.AddDays(-7), null, null), cancellationToken));

        var emergency = await Required(await workflow.RegisterRequestAsync(actor, new RegisterRequestCommand(
            NewOperation(), caseId, DemoData.EmergencyAuthorityId, "Yanğın təhlükəsizliyi rəyi",
            "ŞŞ-215/2026", today.AddDays(-27), today.AddDays(-7), null, null), cancellationToken));

        await Required(await workflow.RegisterRequestAsync(actor, new RegisterRequestCommand(
            NewOperation(), caseId, DemoData.PropertyAuthorityId, "Ərazi üzrə əmlak məlumatı",
            "ŞŞ-216/2026", today.AddDays(-27), today.AddDays(5), null, null), cancellationToken));

        await Required(await workflow.RegisterResponseAsync(actor, new RegisterResponseCommand(
            NewOperation(), caseId, architecture, "MO-88/2026", null, today.AddDays(-11), today.AddDays(-10),
            "OPINION", ResponseOutcomeCodes.Approved, true,
            "Təqdim olunan layihəyə memarlıq baxımından razılıq verilir."), cancellationToken));

        await Required(await workflow.RegisterResponseAsync(actor, new RegisterResponseCommand(
            NewOperation(), caseId, emergency, "FH-12/2026", null, today.AddDays(-25), today.AddDays(-24),
            "ACKNOWLEDGEMENT", ResponseOutcomeCodes.NotApplicable, false,
            "Sorğunun qəbul edilməsi barədə məlumat."), cancellationToken));

        await Required(await workflow.RegisterResponseAsync(actor, new RegisterResponseCommand(
            NewOperation(), caseId, emergency, "FH-40/2026", null, today.AddDays(-10), today.AddDays(-9),
            "OPINION", ResponseOutcomeCodes.Approved, true,
            "Yanğın təhlükəsizliyi tələbləri baxımından etiraz yoxdur."), cancellationToken));

        return true;
    }

    /// <summary>Demo case 2 — a response imposes a requirement, a child request satisfies it, and the requirement is fulfilled.</summary>
    private async Task<bool> SeedNestedRequirementCaseAsync(ActorContext actor, CancellationToken cancellationToken)
    {
        const string incomingLetter = "TMK-77/2026";
        if (await CaseExistsAsync(incomingLetter, cancellationToken))
        {
            return false;
        }

        var today = calendar.Today;
        var caseId = await Required(await cases.CreateAsync(actor, new CreateCaseCommand(
            NewOperation(),
            DemoData.LandCommissionId,
            "Torpaq sahəsinin ayrılmasına dair rəy (nümunə sahə 14)",
            "Nümunə sahə 14 — torpaq sahəsinin ayrılması üçün şəhərsalma rəyinin verilməsi.",
            incomingLetter,
            "DAX-388/2026",
            today.AddDays(-45),
            today.AddDays(-44),
            DemoData.WorkerBUserId,
            "Sintetik nümayiş məlumatı — iç-içə tələb zənciri."), cancellationToken));

        var architecture = await Required(await workflow.RegisterRequestAsync(actor, new RegisterRequestCommand(
            NewOperation(), caseId, DemoData.ArchitectureAuthorityId, "Torpaq ayrılmasına memarlıq rəyi",
            "ŞŞ-198/2026", today.AddDays(-42), today.AddDays(-20), null, null), cancellationToken));

        var conditional = await Required(await workflow.RegisterResponseAsync(actor, new RegisterResponseCommand(
            NewOperation(), caseId, architecture, "MO-61/2026", null, today.AddDays(-36), today.AddDays(-35),
            "ADDITIONAL_REQUIREMENT", ResponseOutcomeCodes.Conditional, false,
            "Rəy verilməzdən əvvəl ərazinin kommunikasiya xəritəsi tələb olunur."), cancellationToken));

        var requirement = await Required(await workflow.CreateRequirementAsync(actor, new CreateRequirementCommand(
            NewOperation(), caseId, conditional, "Kommunikasiya xəritəsi",
            "Ərazidəki mövcud kommunikasiya xətlərini əks etdirən xəritə tələb olunur.",
            true, today.AddDays(-10), DemoData.UtilityAuthorityId), cancellationToken));

        var utility = await Required(await workflow.RegisterRequestAsync(actor, new RegisterRequestCommand(
            NewOperation(), caseId, DemoData.UtilityAuthorityId, "Kommunikasiya xəritəsinin təqdim edilməsi",
            "ŞŞ-203/2026", today.AddDays(-30), today.AddDays(-12), requirement, null), cancellationToken));

        var utilityResponse = await Required(await workflow.RegisterResponseAsync(actor, new RegisterResponseCommand(
            NewOperation(), caseId, utility, "KXO-155/2026", null, today.AddDays(-15), today.AddDays(-14),
            "INFORMATION", ResponseOutcomeCodes.NotApplicable, true,
            "Ərazinin kommunikasiya xəritəsi təqdim edilir."), cancellationToken));

        var workspace = (await caseQueries.GetWorkspaceAsync(actor, caseId, cancellationToken)).Value
            ?? throw new InvalidOperationException("The seeded case could not be read back.");
        var requirementNode = workspace.FindRequirement(requirement)
            ?? throw new InvalidOperationException("The seeded requirement could not be read back.");
        var utilityNode = workspace.FindRequest(utility)
            ?? throw new InvalidOperationException("The seeded child request could not be read back.");

        await Required(await workflow.FulfillRequirementAsync(actor, new FulfillRequirementCommand(
            caseId, requirement, requirementNode.RowVersion, utilityResponse,
            "Kommunikasiya xəritəsi alındı və işə əlavə edildi (sintetik nümunə)."), cancellationToken));

        await Required(await workflow.CloseRequestAsync(actor, new CloseRequestCommand(
            caseId, utility, utilityNode.RowVersion, "Tələb olunan xəritə alındı, sorğu bağlanır."), cancellationToken));

        return true;
    }

    /// <summary>Demo case 3 — an open requirement the department itself owes the next move on.</summary>
    private async Task<bool> SeedOursToActCaseAsync(ActorContext actor, CancellationToken cancellationToken)
    {
        const string incomingLetter = "Rİİ-140/2026";
        if (await CaseExistsAsync(incomingLetter, cancellationToken))
        {
            return false;
        }

        var today = calendar.Today;
        var caseId = await Required(await cases.CreateAsync(actor, new CreateCaseCommand(
            NewOperation(),
            DemoData.RegionalDevelopmentOfficeId,
            "Ticarət obyektinin yerləşdirilməsinə rəy (nümunə)",
            "Nümunə ünvan 3 — ticarət obyektinin yerləşdirilməsi üçün rəy sorğusu.",
            incomingLetter,
            "DAX-455/2026",
            today.AddDays(-12),
            today.AddDays(-11),
            DemoData.WorkerAUserId,
            "Sintetik nümayiş məlumatı — açıq tələb."), cancellationToken));

        var emergency = await Required(await workflow.RegisterRequestAsync(actor, new RegisterRequestCommand(
            NewOperation(), caseId, DemoData.EmergencyAuthorityId, "Yanğın təhlükəsizliyi rəyi",
            "ŞŞ-231/2026", today.AddDays(-10), today.AddDays(10), null, null), cancellationToken));

        var conditional = await Required(await workflow.RegisterResponseAsync(actor, new RegisterResponseCommand(
            NewOperation(), caseId, emergency, "FH-77/2026", null, today.AddDays(-6), today.AddDays(-5),
            "ADDITIONAL_REQUIREMENT", ResponseOutcomeCodes.Conditional, false,
            "Obyektin yanğın təhlükəsizliyi sertifikatı təqdim edilməlidir."), cancellationToken));

        await Required(await workflow.CreateRequirementAsync(actor, new CreateRequirementCommand(
            NewOperation(), caseId, conditional, "Yanğın təhlükəsizliyi sertifikatı",
            "Sertifikat müraciət edən təşkilatdan tələb olunur.",
            true, today.AddDays(7), DemoData.RegionalDevelopmentOfficeId), cancellationToken));

        return true;
    }

    /// <summary>
    /// A case whose work is finished — its one request answered and closed — so the decision can be drafted, issued
    /// and the case closed without building anything first. It keeps one <b>non-blocking</b> requirement open, which
    /// holds nothing up but makes the closure screen's warning real (WORKFLOW.md §9.2.1, ADR-012).
    /// </summary>
    private async Task<bool> SeedReadyForDecisionCaseAsync(ActorContext actor, CancellationToken cancellationToken)
    {
        const string incomingLetter = "Rİİ-162/2026";
        if (await CaseExistsAsync(incomingLetter, cancellationToken))
        {
            return false;
        }

        var today = calendar.Today;
        var caseId = await Required(await cases.CreateAsync(actor, new CreateCaseCommand(
            NewOperation(),
            DemoData.RegionalDevelopmentOfficeId,
            "Anbar binasının tikintisinə rəy (nümunə)",
            "Nümunə ünvan 4 — anbar binasının tikintisi üçün rəy sorğusu.",
            incomingLetter,
            "DAX-501/2026",
            today.AddDays(-30),
            today.AddDays(-29),
            DemoData.WorkerBUserId,
            "Sintetik nümayiş məlumatı — qərar mərhələsi."), cancellationToken));

        var architecture = await Required(await workflow.RegisterRequestAsync(actor, new RegisterRequestCommand(
            NewOperation(), caseId, DemoData.ArchitectureAuthorityId, "Memarlıq rəyi",
            "ŞŞ-262/2026", today.AddDays(-25), null, null, null), cancellationToken));

        var approval = await Required(await workflow.RegisterResponseAsync(actor, new RegisterResponseCommand(
            NewOperation(), caseId, architecture, "MO-198/2026", null, today.AddDays(-8), today.AddDays(-7),
            "OPINION", ResponseOutcomeCodes.Approved, true,
            "Layihə memarlıq tələblərinə uyğundur."), cancellationToken));

        // The request is closed before the requirement is raised: a blocking requirement would prevent closure
        // (§3.4), and a non-blocking one raised afterwards leaves it closed (§9.2, one definition of blocking).
        var rowVersion = await RequestRowVersionAsync(architecture, cancellationToken);
        await Required(await workflow.CloseRequestAsync(
            actor, new CloseRequestCommand(caseId, architecture, rowVersion, "Rəy alındı, sorğu bağlanır."), cancellationToken));

        await Required(await workflow.CreateRequirementAsync(actor, new CreateRequirementCommand(
            NewOperation(), caseId, approval, "Yenilənmiş situasiya planı",
            "Arxivə əlavə edilmək üçün; qərarın verilməsini dayandırmır.",
            false, today.AddDays(20), DemoData.RegionalDevelopmentOfficeId), cancellationToken));

        return true;
    }

    // ----------------------------------------------------------------- helpers

    private async Task<int> RequestRowVersionAsync(Guid requestId, CancellationToken cancellationToken)
    {
        await using var unitOfWork = (PostgresUnitOfWork)await unitOfWorkFactory.BeginAsync(cancellationToken);
        return await unitOfWork.Command("SELECT row_version FROM rcs.request WHERE id = @id")
            .With("id", requestId)
            .ScalarAsync<int>(cancellationToken);
    }

    private async Task<bool> CaseExistsAsync(string incomingLetterNumber, CancellationToken cancellationToken)
    {
        await using var unitOfWork = (PostgresUnitOfWork)await unitOfWorkFactory.BeginAsync(cancellationToken);
        return await unitOfWork.Command("""
                SELECT EXISTS (
                    SELECT 1 FROM rcs.correspondence
                    WHERE letter_number = @number
                      AND correspondence_kind_id = (SELECT id FROM rcs.correspondence_kind WHERE code = 'INITIATING'))
                """)
            .With("number", incomingLetterNumber)
            .ScalarAsync<bool>(cancellationToken);
    }

    private OperationId NewOperation() => new(ids.NewId());

    private static Task<Guid> Required(CommandResult<Guid> result) =>
        result.Succeeded
            ? Task.FromResult(result.Value)
            : throw new InvalidOperationException($"The demo seed step failed: {result.Error!.Kind} {result.Error.Code} {result.Error.Field}");
}
