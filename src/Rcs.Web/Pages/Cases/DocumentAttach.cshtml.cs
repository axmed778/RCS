using Microsoft.AspNetCore.Mvc;
using Rcs.Application.Cases;
using Rcs.Application.Documents;
using Rcs.Application.Identifiers;
using Rcs.Application.Idempotency;
using Rcs.Application.Lifecycle;
using Rcs.Domain.Vocabulary;
using Rcs.Web.Documents;
using Rcs.Web.Review;
using Rcs.Web.Ui;

namespace Rcs.Web.Pages.Cases;

/// <summary>
/// Places a file that is already in the case into another of its contexts — an annex of an outgoing letter, the signed
/// decision of a final result, evidence for a requirement. The same bytes, the same version, a new placement pinned to
/// that version (DOCUMENT_MODEL.md §4.5): nothing is copied or re-uploaded.
/// </summary>
public sealed class DocumentAttachModel(
    ICaseQueries cases,
    ILifecycleQueries lifecycleQueries,
    IDocumentQueries documentQueries,
    IDocumentService documents,
    IIdGenerator ids,
    CurrentActor actor,
    UiText text) : ReviewPageModel(actor, text)
{
    [BindProperty(SupportsGet = true)]
    public Guid CaseId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Target { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Role { get; set; }

    /// <summary><c>sourceLinkId:versionId</c> of the chosen version.</summary>
    [BindProperty]
    public string? Selected { get; set; }

    [BindProperty]
    public string? Note { get; set; }

    [BindProperty]
    public Guid OperationId { get; set; }

    public DocumentContextOption? Context { get; private set; }

    public IReadOnlyList<AttachableVersion> Candidates { get; private set; } = [];

    public IReadOnlyList<DocumentContextOption> Contexts { get; private set; } = [];

    public bool IsEvidence => Role == DocumentLinkRoleCodes.RequirementEvidence;

    public async Task<IActionResult> OnGetAsync()
    {
        if (!HasActor)
        {
            return Page();
        }

        if (await LoadAsync() is { } failure)
        {
            return failure;
        }

        OperationId = ids.NewId();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!HasActor)
        {
            return Page();
        }

        if (await LoadAsync() is { } failure)
        {
            return failure;
        }

        var parts = (Selected ?? string.Empty).Split(':');
        if (parts.Length != 2 || !Guid.TryParse(parts[0], out var sourceLinkId) || !Guid.TryParse(parts[1], out var versionId))
        {
            ModelState.AddModelError(string.Empty, Text["Error.document.select_file"]);
            return Page();
        }

        var operation = new OperationId(OperationId == Guid.Empty ? ids.NewId() : OperationId);
        var result = IsEvidence
            ? await documents.RecordDocumentEvidenceAsync(Actor.Context, new RecordDocumentEvidenceCommand(
                operation, CaseId, Context!.Target.Id, sourceLinkId, versionId, Note), HttpContext.RequestAborted)
            : await documents.PlaceVersionAsync(Actor.Context, new PlaceVersionCommand(
                operation, CaseId, sourceLinkId, versionId, Context!.Target, Role!, Note), HttpContext.RequestAborted);

        if (!result.Succeeded)
        {
            ApplyError(result.Error);
            return Page();
        }

        return ToWorkspace(CaseId, Context.Anchor);
    }

    private async Task<IActionResult?> LoadAsync()
    {
        var workspace = await cases.GetWorkspaceAsync(Actor.Context, CaseId, HttpContext.RequestAborted);
        if (!workspace.Succeeded)
        {
            return NotFound();
        }

        var results = await lifecycleQueries.ListFinalResultsAsync(Actor.Context, CaseId, HttpContext.RequestAborted);
        Contexts = DocumentContexts.Of(workspace.Value!, results, Text);
        Context = DocumentTargetCodec.Parse(Target) is { } target ? Contexts.FirstOrDefault(option => option.Target == target) : null;
        if (Context is null || Role is null || !Context.Roles.Contains(Role))
        {
            return NotFound();
        }

        // Offer only versions not already placed in this context.
        var placedHere = (await documentQueries.GetCaseDocumentsAsync(Actor.Context, CaseId, HttpContext.RequestAborted)).Value?
            .For(Context.Target.Kind, Context.Target.Id)
            .Where(placement => placement.IsActive)
            .SelectMany(placement => placement.Versions.Select(version => version.Id))
            .ToHashSet() ?? [];
        Candidates = (await documentQueries.ListAttachableAsync(Actor.Context, CaseId, HttpContext.RequestAborted))
            .Where(candidate => !placedHere.Contains(candidate.Version.Id))
            .ToArray();
        return null;
    }

    public string SourceLabel(AttachableVersion candidate) =>
        Contexts.FirstOrDefault(option => option.Target == candidate.SourceTarget)?.Label ?? string.Empty;
}
