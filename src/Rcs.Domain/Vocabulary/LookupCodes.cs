namespace Rcs.Domain.Vocabulary;

// Codes of OPEN vocabularies (lookup tables, DOMAIN_MODEL.md §1.4, §2.19) that application logic refers to.
// Lookup tables hold more codes than these; only codes the rules branch on belong here. Codes are stable
// and are never translated: user interfaces translate them for display.

/// <summary><c>correspondence_kind</c> — DOMAIN_MODEL.md §2.8.</summary>
public static class CorrespondenceKindCodes
{
    public const string Initiating = "INITIATING";
    public const string OutgoingRequest = "OUTGOING_REQUEST";
    public const string IncomingResponse = "INCOMING_RESPONSE";
}

/// <summary><c>response_outcome</c> — DOMAIN_MODEL.md §2.10 (decision C-2).</summary>
public static class ResponseOutcomeCodes
{
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";
    public const string Conditional = "CONDITIONAL";
    public const string NotApplicable = "NOT_APPLICABLE";

    /// <summary>A verdict was expected but is not established yet — listed as needing attention (WORKFLOW.md §4.2).</summary>
    public const string Undetermined = "UNDETERMINED";
}

/// <summary><c>requirement_origin_type</c> — DOMAIN_MODEL.md §2.11.</summary>
public static class RequirementOriginCodes
{
    public const string Response = "RESPONSE";
}

/// <summary><c>deadline_basis</c> — DOMAIN_MODEL.md §2.19; WORKFLOW.md §12.1.</summary>
public static class DeadlineBasisCodes
{
    /// <summary>The department's own 10-calendar-day rule (ADR-041), as opposed to a statutory or agreed date.</summary>
    public const string Internal = "INTERNAL";
}

/// <summary><c>assignment_role</c> — DOMAIN_MODEL.md §2.7.</summary>
public static class AssignmentRoleCodes
{
    public const string Responsible = "RESPONSIBLE";
    public const string TemporaryCover = "TEMPORARY_COVER";
}

/// <summary><c>void_reason</c> — DOMAIN_MODEL.md §2.19; PERMISSIONS.md §26 footnote ⁷.</summary>
public static class VoidReasonCodes
{
    public const string NoLongerRequired = "NO_LONGER_REQUIRED";
    public const string SupersededByResponse = "SUPERSEDED_BY_RESPONSE";
    public const string BranchRemoved = "BRANCH_REMOVED";
    public const string DataEntryError = "DATA_ENTRY_ERROR";
    public const string Duplicate = "DUPLICATE";

    /// <summary>Corrections a Worker may apply to their own entry; every other void reason is a Chief judgement.</summary>
    public static bool IsCorrection(string code) => code is DataEntryError or Duplicate;
}

/// <summary>Stable names used in <c>audit_event.entity_type</c>: domain entity names, not physical table names (ADR-037).</summary>
public static class AuditEntityTypes
{
    public const string Case = "case";
    public const string User = "user";
    public const string UserRole = "user_role";
    public const string Organization = "organization";
    public const string Correspondence = "correspondence";
    public const string Request = "request";
    public const string Response = "response";
    public const string Requirement = "requirement";
    public const string RequirementEvidence = "requirement_evidence";
    public const string Assignment = "assignment";
    public const string FinalResult = "final_result";
}

/// <summary><c>audit_event.action_code</c> — the frozen vocabulary (DOMAIN_MODEL.md §2.18).</summary>
public static class AuditActionCodes
{
    public const string Create = "CREATE";
    public const string Update = "UPDATE";
    public const string StateChange = "STATE_CHANGE";
    public const string Link = "LINK";
    public const string Unlink = "UNLINK";
    public const string Void = "VOID";
    public const string Assign = "ASSIGN";
    public const string PermissionDenied = "PERMISSION_DENIED";
}
