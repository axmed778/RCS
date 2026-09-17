namespace Rcs.Domain.Vocabulary;

/// <summary>
/// The exact code stored in PostgreSQL for a member of a closed vocabulary. Enum names and numeric
/// values are never persisted; only this code is.
/// </summary>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class DbCodeAttribute(string code) : Attribute
{
    public string Code { get; } = code;
}
