using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Rcs.Application.Cases;
using Rcs.Application.Common;
using Rcs.Application.Documents;
using Rcs.Application.Identifiers;
using Rcs.Application.Lifecycle;
using Rcs.Application.Lookups;
using Rcs.Domain.Documents;
using Rcs.Infrastructure.Configuration;
using Rcs.Web.Documents;
using Rcs.Web.Review;
using Rcs.Web.Ui;

namespace Rcs.Web.Pages.Cases;

/// <summary>
/// The upload form, always opened from the business context the file belongs to (DOCUMENT_MODEL.md §19 principles 1–2).
/// It renders only: the multipart body is posted to the streaming endpoint, never through page model binding, so a
/// 500 MB file is never buffered. The operation id is generated here, once, so a resubmission creates nothing twice.
/// </summary>
public sealed class DocumentUploadModel(
    ICaseQueries cases,
    ILifecycleQueries lifecycleQueries,
    IDocumentQueries documents,
    ILookupQueries lookups,
    IIdGenerator ids,
    IOptions<StorageOptions> storage,
    CurrentActor actor,
    UiText text) : ReviewPageModel(actor, text)
{
    [BindProperty(SupportsGet = true)]
    public Guid CaseId { get; set; }

    /// <summary>The context a new document goes into, as <c>kind:id</c>.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Target { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Role { get; set; }

    /// <summary>Version mode: the home-case placement a new version of its document is uploaded through.</summary>
    [BindProperty(SupportsGet = true)]
    public Guid? Link { get; set; }

    /// <summary>A refusal code handed back by the endpoint after a plain (script-less) form post.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Error { get; set; }

    public CaseWorkspace? Workspace { get; private set; }

    public DocumentContextOption? Context { get; private set; }

    public IReadOnlyList<DocumentContextOption> Contexts { get; private set; } = [];

    public DocumentPlacementView? Placement { get; private set; }

    public IReadOnlyList<LookupItem> Kinds { get; private set; } = [];

    public Guid OperationId { get; private set; }

    public long MaxBytes => storage.Value.MaxUploadBytes;

    public string? ErrorMessage => string.IsNullOrEmpty(Error) ? null : Text.Error(new CommandError(CommandErrorKind.Validation, Error));

    public bool IsVersion => Placement is not null;

    public string FormAction => IsVersion ? $"/cases/{CaseId}/documents/{Placement!.LinkId}/versions" : $"/cases/{CaseId}/documents/upload";

    public string ReturnUrl => $"/cases/{CaseId}" + (IsVersion ? $"/documents/{Placement!.LinkId}" : Context is null ? string.Empty : "#" + Context.Anchor);

    public string FormUrl => HttpContext.Request.Path + QueryWithoutError();

    public async Task<IActionResult> OnGetAsync()
    {
        if (!HasActor)
        {
            return Page();
        }

        var workspace = await cases.GetWorkspaceAsync(Actor.Context, CaseId, HttpContext.RequestAborted);
        if (!workspace.Succeeded)
        {
            return NotFound();
        }

        Workspace = workspace.Value;
        var results = await lifecycleQueries.ListFinalResultsAsync(Actor.Context, CaseId, HttpContext.RequestAborted);
        Contexts = DocumentContexts.Of(Workspace!, results, Text);
        Kinds = await lookups.ListActiveAsync(LookupKind.DocumentKind, HttpContext.RequestAborted);
        OperationId = ids.NewId();

        if (Link is { } linkId)
        {
            var caseDocuments = await documents.GetCaseDocumentsAsync(Actor.Context, CaseId, HttpContext.RequestAborted);
            Placement = caseDocuments.Value?.Find(linkId);
            if (Placement is not { IsActive: true, IsHomeCase: true })
            {
                return NotFound();
            }

            // A new version may also be placed, pinned, in a context of this (home) case — e.g. the later letter that brought it.
            return Page();
        }

        Context = DocumentTargetCodec.Parse(Target) is { } target ? Contexts.FirstOrDefault(option => option.Target == target) : null;
        if (Context is null)
        {
            return NotFound();
        }

        if (Role is null || !Context.Roles.Contains(Role))
        {
            Role = Context.Roles[0];
        }

        return Page();
    }

    private string QueryWithoutError()
    {
        var query = HttpContext.Request.Query.Where(pair => !string.Equals(pair.Key, "error", StringComparison.OrdinalIgnoreCase))
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value.ToString())}")
            .ToArray();
        return query.Length == 0 ? string.Empty : "?" + string.Join('&', query);
    }
}
