using System.Security.Cryptography;
using System.Text;

namespace Rcs.Infrastructure.Migrations;

/// <summary>One parsed, hand-written migration file.</summary>
/// <param name="Id">The ordered number from the file name (<c>0001</c> → 1). Also the schema version it produces.</param>
/// <param name="Name">The snake_case name from the file name.</param>
/// <param name="FileName">The full file name, e.g. <c>0001_foundation.sql</c>.</param>
/// <param name="Description">The <c>-- description:</c> header.</param>
/// <param name="Transactional">False only when the file declares <c>-- transactional: false</c>.</param>
/// <param name="Sql">The file content, LF-normalized.</param>
/// <param name="Checksum">Lower-case hex SHA-256 of the normalized content (<see cref="MigrationChecksum"/>).</param>
public sealed record Migration(
    int Id,
    string Name,
    string FileName,
    string Description,
    bool Transactional,
    string Sql,
    string Checksum);

/// <summary>
/// Checksums detect any edit to an applied migration, including comments and whitespace. Two
/// normalizations are applied first so that a Windows checkout does not look like an edit: a leading UTF-8
/// byte-order mark is removed and CRLF line endings become LF. Nothing else is normalized.
/// </summary>
public static class MigrationChecksum
{
    public static string Normalize(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length > 0 && content[0] == '﻿')
        {
            content = content[1..];
        }

        return content.Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    public static string Compute(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(content))));
}
