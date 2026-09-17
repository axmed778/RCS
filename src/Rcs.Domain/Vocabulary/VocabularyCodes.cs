using System.Reflection;

namespace Rcs.Domain.Vocabulary;

/// <summary>Maps closed-vocabulary enums to and from their exact database codes.</summary>
public static class VocabularyCodes
{
    public static string ToCode<TEnum>(this TEnum value)
        where TEnum : struct, Enum
    {
        return Map<TEnum>.ToCode.TryGetValue(value, out var code)
            ? code
            : throw new ArgumentOutOfRangeException(
                nameof(value), value, $"{typeof(TEnum).Name} has no database code for this value.");
    }

    public static TEnum FromCode<TEnum>(string code)
        where TEnum : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(code);
        return Map<TEnum>.FromCode.TryGetValue(code, out var value)
            ? value
            : throw new ArgumentException($"'{code}' is not a {typeof(TEnum).Name} code.", nameof(code));
    }

    /// <summary>All codes in declaration order — the exact list a database CHECK constraint must allow.</summary>
    public static IReadOnlyList<string> AllCodes<TEnum>()
        where TEnum : struct, Enum => Map<TEnum>.Codes;

    private static class Map<TEnum>
        where TEnum : struct, Enum
    {
        internal static readonly Dictionary<TEnum, string> ToCode = [];
        internal static readonly Dictionary<string, TEnum> FromCode = new(StringComparer.Ordinal);
        internal static readonly List<string> Codes = [];

        static Map()
        {
            foreach (var field in typeof(TEnum).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                var attribute = field.GetCustomAttribute<DbCodeAttribute>()
                    ?? throw new InvalidOperationException($"{typeof(TEnum).Name}.{field.Name} has no [DbCode].");
                var value = (TEnum)field.GetValue(null)!;
                ToCode.Add(value, attribute.Code);
                FromCode.Add(attribute.Code, value);
                Codes.Add(attribute.Code);
            }
        }
    }
}
