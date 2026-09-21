using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Konscious.Security.Cryptography;
using Microsoft.Extensions.Options;
using Rcs.Application.Identity;
using Rcs.Infrastructure.Configuration;

namespace Rcs.Infrastructure.Identity;

/// <summary>
/// Argon2id password hashing (ADR-033; SECURITY.md §6.2): memory-hard, per-password salt from the system CSPRNG, and
/// the cost parameters stored inside the hash so they can be reviewed and raised later. Verification is constant-time,
/// and a hash produced with weaker parameters than this server is configured for is reported for transparent rehashing.
/// </summary>
/// <remarks>
/// The encoding is the usual PHC string: <c>$argon2id$v=19$m=65536,t=3,p=2$&lt;salt&gt;$&lt;hash&gt;</c>. A database
/// CHECK requires that exact shape, so no code path can store a plaintext or unsalted value even by mistake.
/// </remarks>
public sealed partial class Argon2PasswordHasher(IOptions<LocalAuthenticationOptions> options) : IPasswordHasher
{
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int Version = 19;

    private readonly LocalAuthenticationOptions settings = options.Value;

    public string Algorithm => "ARGON2ID";

    public string Parameters => string.Create(CultureInfo.InvariantCulture,
        $"m={settings.Argon2MemoryKibibytes};t={settings.Argon2Iterations};p={settings.Argon2Parallelism}");

    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(password, salt, settings.Argon2MemoryKibibytes, settings.Argon2Iterations, settings.Argon2Parallelism);
        return string.Create(CultureInfo.InvariantCulture,
            $"$argon2id$v={Version}$m={settings.Argon2MemoryKibibytes},t={settings.Argon2Iterations},p={settings.Argon2Parallelism}${Convert.ToBase64String(salt).TrimEnd('=')}${Convert.ToBase64String(hash).TrimEnd('=')}");
    }

    public (bool Verified, bool NeedsRehash) Verify(string encodedHash, string password)
    {
        if (string.IsNullOrEmpty(encodedHash) || encodedHash.Length > 512 || string.IsNullOrEmpty(password) || password.Length > 1024)
        {
            return (false, false);
        }

        var match = EncodedPattern().Match(encodedHash);
        if (!match.Success)
        {
            return (false, false);
        }

        var memory = int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture);
        var iterations = int.Parse(match.Groups["t"].Value, CultureInfo.InvariantCulture);
        var parallelism = int.Parse(match.Groups["p"].Value, CultureInfo.InvariantCulture);
        byte[] salt;
        byte[] expected;
        try
        {
            salt = Decode(match.Groups["salt"].Value);
            expected = Decode(match.Groups["hash"].Value);
        }
        catch (FormatException)
        {
            return (false, false);
        }

        // A hostile or corrupted row must not be able to make this process allocate gigabytes while verifying.
        if (salt.Length is < 8 or > 64 || expected.Length is < 16 or > 64
            || memory is < 1024 or > 1_048_576 || iterations is < 1 or > 16 || parallelism is < 1 or > 16)
        {
            return (false, false);
        }

        var actual = Derive(password, salt, memory, iterations, parallelism, expected.Length);
        var verified = CryptographicOperations.FixedTimeEquals(actual, expected);
        // Upgrade only when no configured work factor would decrease. A mixed policy needs operator review.
        var weaker = memory <= settings.Argon2MemoryKibibytes && iterations <= settings.Argon2Iterations
            && parallelism <= settings.Argon2Parallelism
            && (memory < settings.Argon2MemoryKibibytes || iterations < settings.Argon2Iterations
                || parallelism < settings.Argon2Parallelism || salt.Length < SaltBytes || expected.Length < HashBytes
                || encodedHash.Contains('=', StringComparison.Ordinal) && encodedHash.EndsWith('='));
        return (verified, verified && weaker);
    }

    private static byte[] Decode(string value) => Convert.FromBase64String(value.PadRight((value.Length + 3) / 4 * 4, '='));

    private static byte[] Derive(string password, byte[] salt, int memoryKibibytes, int iterations, int parallelism, int length = HashBytes)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memoryKibibytes,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };
        return argon.GetBytes(length);
    }

    [GeneratedRegex(@"^\$argon2id\$v=19\$m=(?<m>[0-9]{1,7}),t=(?<t>[0-9]{1,2}),p=(?<p>[0-9]{1,2})\$(?<salt>[A-Za-z0-9+/=]{16,})\$(?<hash>[A-Za-z0-9+/=]{16,})\z")]
    private static partial Regex EncodedPattern();
}
