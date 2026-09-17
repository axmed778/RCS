using Rcs.Application.Identifiers;

namespace Rcs.Infrastructure.Identifiers;

/// <summary>
/// RFC 9562 UUIDv7 generated in the application: 48-bit Unix milliseconds followed by random bits. No
/// PostgreSQL extension and no network are involved. Npgsql writes <see cref="Guid"/> to <c>uuid</c> in
/// RFC byte order, so PostgreSQL sees the timestamp prefix and B-tree inserts stay local.
/// </summary>
public sealed class UuidV7IdGenerator(TimeProvider timeProvider) : IIdGenerator
{
    public Guid NewId() => Guid.CreateVersion7(timeProvider.GetUtcNow());
}
