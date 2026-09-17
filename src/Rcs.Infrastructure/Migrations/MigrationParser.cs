using System.Globalization;
using System.Text.RegularExpressions;

namespace Rcs.Infrastructure.Migrations;

/// <summary>
/// Parses one migration file. Convention (database/README.md):
/// <list type="bullet">
/// <item>file name <c>NNNN_snake_case_name.sql</c> — four digits, contiguous from 0001;</item>
/// <item>a <c>-- description: ...</c> line in the leading comment block (required);</item>
/// <item><c>-- transactional: false</c> in the same block for the rare statement PostgreSQL cannot run in a
/// transaction (optional; default true).</item>
/// </list>
/// </summary>
public static partial class MigrationParser
{
    public static Migration Parse(string fileName, string content)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(content);

        var problems = new List<string>();
        var normalized = MigrationChecksum.Normalize(content);

        var id = 0;
        var name = string.Empty;
        var nameMatch = FileNamePattern().Match(fileName);
        if (!nameMatch.Success)
        {
            problems.Add($"{fileName}: the file name must match NNNN_snake_case_name.sql (four digits, then lowercase letters and digits separated by single underscores).");
        }
        else
        {
            id = int.Parse(nameMatch.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture);
            name = nameMatch.Groups["name"].Value;
            if (id == 0)
            {
                problems.Add($"{fileName}: migration numbers start at 0001.");
            }
        }

        var header = ReadHeader(fileName, normalized, problems);

        header.TryGetValue("description", out var description);
        if (string.IsNullOrWhiteSpace(description))
        {
            problems.Add($"{fileName}: a '-- description: ...' line is required in the leading comment block.");
        }

        var transactional = true;
        if (header.TryGetValue("transactional", out var transactionalValue))
        {
            switch (transactionalValue)
            {
                case "true":
                    break;
                case "false":
                    transactional = false;
                    break;
                default:
                    problems.Add($"{fileName}: '-- transactional:' must be 'true' or 'false'.");
                    break;
            }
        }

        var lines = normalized.Split('\n');
        if (lines.All(IsBlankOrComment))
        {
            problems.Add($"{fileName}: the file contains no SQL.");
        }

        if (transactional)
        {
            for (var index = 0; index < lines.Length; index++)
            {
                if (!IsBlankOrComment(lines[index]) && TransactionControlPattern().IsMatch(lines[index].TrimStart()))
                {
                    problems.Add($"{fileName}: line {index + 1} controls the transaction. Transactional migrations run inside the runner's transaction; remove the statement, or declare '-- transactional: false'.");
                }
            }
        }

        if (problems.Count > 0)
        {
            throw new MigrationValidationException(problems);
        }

        return new Migration(id, name, fileName, description!.Trim(), transactional, normalized, MigrationChecksum.Compute(content));
    }

    private static Dictionary<string, string> ReadHeader(string fileName, string normalized, List<string> problems)
    {
        var header = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in normalized.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (!line.StartsWith("--", StringComparison.Ordinal))
            {
                break; // The header is the leading comment block only.
            }

            var match = HeaderPattern().Match(line);
            if (match.Success && !header.TryAdd(match.Groups["key"].Value, match.Groups["value"].Value.Trim()))
            {
                problems.Add($"{fileName}: '-- {match.Groups["key"].Value}:' is declared more than once.");
            }
        }

        return header;
    }

    private static bool IsBlankOrComment(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.Length == 0 || trimmed.StartsWith("--", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"^(?<id>\d{4})_(?<name>[a-z0-9]+(?:_[a-z0-9]+)*)\.sql$", RegexOptions.CultureInvariant)]
    private static partial Regex FileNamePattern();

    [GeneratedRegex(@"^--\s*(?<key>description|transactional)\s*:(?<value>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex HeaderPattern();

    // Statement-level transaction control only. PL/pgSQL block keywords (BEGIN on its own line, END;) are not matched.
    [GeneratedRegex(
        @"^(?:(?:COMMIT|ROLLBACK|ABORT)\b|START\s+TRANSACTION\b|END\s+(?:TRANSACTION|WORK)\b|BEGIN\s*(?:TRANSACTION|WORK)?\s*;)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TransactionControlPattern();
}
