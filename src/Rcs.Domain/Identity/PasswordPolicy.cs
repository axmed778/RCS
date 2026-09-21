using System.Globalization;
using System.Text;

namespace Rcs.Domain.Identity;

/// <summary>Why a proposed password was refused. The reason is shown to the person choosing it, never logged with the value.</summary>
public enum PasswordRejection
{
    None = 0,
    TooShort,
    TooLong,
    TooCommon,
    ContainsUsername,
    NotEnoughVariety,
}

/// <summary>
/// SECURITY.md §6.3 — length, not composition theatre. Twelve characters minimum, passphrases encouraged, no forced
/// rotation, no character-class rules, and a short local blocklist of the obvious (the application's own name, the
/// usual passwords, keyboard runs, the user's own username). No online breach service: that would be an Internet
/// dependency (§18.3).
/// </summary>
public static class PasswordPolicy
{
    public const int MinimumLength = 12;

    public const int MaximumLength = 128;

    /// <summary>Shipped with the release and deliberately short: the passwords people actually try first.</summary>
    private static readonly string[] Blocked =
    [
        "password", "parol", "parolparol", "qwerty", "123456", "12345678", "123456789", "1234567890",
        "qwertyuiop", "administrator", "admin", "welcome", "letmein", "iloveyou", "azerbaijan", "azerbaycan",
        "rcs", "rcsrcs", "reyestr", "sistem", "system", "default", "changeme", "temporary", "passw0rd",
    ];

    public static PasswordRejection Check(string? password, string? username = null)
    {
        if (password is null || password.Length < MinimumLength)
        {
            return PasswordRejection.TooShort;
        }

        if (password.Length > MaximumLength)
        {
            return PasswordRejection.TooLong;
        }

        var folded = password.ToLower(CultureInfo.InvariantCulture);
        if (Blocked.Any(blocked => folded.Contains(blocked, StringComparison.Ordinal)))
        {
            return PasswordRejection.TooCommon;
        }

        if (username is { Length: >= 3 } name && folded.Contains(name.ToLower(CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            return PasswordRejection.ContainsUsername;
        }

        // Not a composition rule: it only rejects a "password" that is one character repeated, or a straight run of
        // the same character class typed in order — "aaaaaaaaaaaa", "abcdefghijkl", "111111111111".
        return HasSomeVariety(password) ? PasswordRejection.None : PasswordRejection.NotEnoughVariety;
    }

    public static bool IsAcceptable(string? password, string? username = null) => Check(password, username) == PasswordRejection.None;

    private static bool HasSomeVariety(string password)
    {
        var distinct = new HashSet<Rune>(password.EnumerateRunes());
        if (distinct.Count < 5)
        {
            return false;
        }

        var ascending = true;
        var descending = true;
        for (var index = 1; index < password.Length; index++)
        {
            ascending &= password[index] == password[index - 1] + 1;
            descending &= password[index] == password[index - 1] - 1;
        }

        return !ascending && !descending;
    }
}
