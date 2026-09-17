using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Rcs.Infrastructure.Concurrency;

/// <summary>
/// Derives the PostgreSQL advisory-lock key for a case (DECISIONS.md ADR-019).
/// </summary>
/// <remarks>
/// key = first 8 bytes, big-endian, of SHA-256("rcs:case-serialization:v1:" ‖ case UUID in RFC 9562 byte
/// order), read as a signed 64-bit integer. Deterministic across processes, machines and versions of
/// .NET. The namespace keeps case locks apart from every other advisory lock in the database, including
/// the migration lock. A hash collision between two cases only makes them serialise with each other —
/// never incorrect, just slower — and is negligible at 64 bits.
/// <para>
/// Changing this derivation while two application versions could run against one database would break
/// the convention; the "v1" suffix exists so that any future change is explicit.
/// </para>
/// </remarks>
public static class CaseLockKey
{
    private static readonly byte[] Namespace = "rcs:case-serialization:v1:"u8.ToArray();

    public static long Derive(Guid caseId)
    {
        if (caseId == Guid.Empty)
        {
            throw new ArgumentException("A case id cannot be empty.", nameof(caseId));
        }

        Span<byte> input = stackalloc byte[Namespace.Length + 16];
        Namespace.CopyTo(input);
        if (!caseId.TryWriteBytes(input[Namespace.Length..], bigEndian: true, out _))
        {
            throw new InvalidOperationException("Could not encode the case id.");
        }

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(input, hash);
        return BinaryPrimitives.ReadInt64BigEndian(hash);
    }

    /// <summary>
    /// Distinct keys in ascending order: the fixed acquisition order. Ordering by the key actually locked
    /// (rather than by case id) keeps the order consistent even if two cases ever shared a key.
    /// </summary>
    public static IReadOnlyList<long> DeriveOrdered(IEnumerable<Guid> caseIds)
    {
        ArgumentNullException.ThrowIfNull(caseIds);
        return caseIds.Select(Derive).Distinct().Order().ToArray();
    }
}
