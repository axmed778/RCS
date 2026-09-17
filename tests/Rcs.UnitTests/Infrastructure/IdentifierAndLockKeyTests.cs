using System.Buffers.Binary;
using System.Security.Cryptography;
using Rcs.Infrastructure.Concurrency;
using Rcs.Infrastructure.Identifiers;
using Rcs.UnitTests.TestSupport;

namespace Rcs.UnitTests.Infrastructure;

public sealed class UuidV7IdGeneratorTests
{
    private static readonly DateTimeOffset Instant = new(2026, 9, 17, 10, 30, 15, 123, TimeSpan.Zero);

    [Fact]
    public void GeneratesRfc9562Version7WithTheClockTimestamp()
    {
        var id = new UuidV7IdGenerator(new FixedTimeProvider(Instant)).NewId();
        var bytes = id.ToByteArray(bigEndian: true);

        Assert.Equal(7, bytes[6] >> 4);          // version nibble
        Assert.Equal(0b10, bytes[8] >> 6);       // RFC 9562 variant
        var unixMilliseconds = ((long)BinaryPrimitives.ReadUInt32BigEndian(bytes) << 16) | BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(4));
        Assert.Equal(Instant.ToUnixTimeMilliseconds(), unixMilliseconds);
    }

    [Fact]
    public void IdentifiersAreUniqueWithinOneMillisecond()
    {
        var generator = new UuidV7IdGenerator(new FixedTimeProvider(Instant));
        var ids = Enumerable.Range(0, 10_000).Select(_ => generator.NewId()).ToHashSet();
        Assert.Equal(10_000, ids.Count);
    }

    [Fact]
    public void LaterMillisecondsSortLaterInUuidByteOrder()
    {
        var earlier = new UuidV7IdGenerator(new FixedTimeProvider(Instant)).NewId().ToByteArray(bigEndian: true);
        var later = new UuidV7IdGenerator(new FixedTimeProvider(Instant.AddMilliseconds(1))).NewId().ToByteArray(bigEndian: true);
        Assert.True(earlier.AsSpan().SequenceCompareTo(later) < 0);
    }
}

public sealed class CaseLockKeyTests
{
    private static readonly Guid CaseA = Guid.Parse("01923b6e-5c7a-7d1e-9a4b-3c2d1e0f9a8b");
    private static readonly Guid CaseB = Guid.Parse("01923b6e-5c7a-7d1e-9a4b-3c2d1e0f9a8c");

    [Fact]
    public void KeyIsTheDocumentedDerivation()
    {
        var input = "rcs:case-serialization:v1:"u8.ToArray().Concat(CaseA.ToByteArray(bigEndian: true)).ToArray();
        var expected = BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(input));
        Assert.Equal(expected, CaseLockKey.Derive(CaseA));
    }

    [Fact]
    public void KeyIsDeterministicAndDistinguishesCases()
    {
        Assert.Equal(CaseLockKey.Derive(CaseA), CaseLockKey.Derive(CaseA));
        Assert.NotEqual(CaseLockKey.Derive(CaseA), CaseLockKey.Derive(CaseB));
    }

    [Fact]
    public void KeyIsNeverTheMigrationLockKey()
    {
        Assert.NotEqual(Rcs.Infrastructure.Migrations.MigrationRunner.MigrationLockKey, CaseLockKey.Derive(CaseA));
    }

    [Fact]
    public void MultipleCasesAreLockedOnceEachInAscendingKeyOrder()
    {
        var keys = CaseLockKey.DeriveOrdered([CaseB, CaseA, CaseB]);

        Assert.Equal(2, keys.Count);
        Assert.Equal(keys.Order(), keys);
        Assert.Equal(keys, CaseLockKey.DeriveOrdered([CaseA, CaseB]));
    }

    [Fact]
    public void EmptyCaseIdIsRejected()
    {
        Assert.Throws<ArgumentException>(() => CaseLockKey.Derive(Guid.Empty));
    }
}
