namespace Rcs.Application.Lookups;

/// <summary>The open vocabularies forms offer. An allowlist: nothing else can be queried by name.</summary>
public enum LookupKind
{
    OrganizationType = 1,
    ResponseType,
    ResponseOutcome,
    VoidReason,
    WaiverReason,
    DeadlineBasis,
    DecisionType,
    ClosureType,
    DocumentKind,
    DocumentLinkRole,
    WithdrawalReason,
}

/// <param name="Code">The stable code; user interfaces translate it.</param>
/// <param name="Label">The reference label stored with the vocabulary (a fallback, not the translation).</param>
/// <param name="Flag">The vocabulary's boolean attribute, where it has one: <c>is_conclusive_default</c>, <c>is_verdict</c> or <c>counts_as_business_outcome</c>.</param>
public sealed record LookupItem(string Code, string Label, bool? Flag);

public interface ILookupQueries
{
    Task<IReadOnlyList<LookupItem>> ListActiveAsync(LookupKind kind, CancellationToken cancellationToken = default);
}
