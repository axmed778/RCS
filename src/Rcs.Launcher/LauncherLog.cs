using System.Globalization;

namespace Rcs.Launcher;

/// <summary>
/// One line per event in a daily file under ProgramData, so a problem on the employee's laptop can be explained
/// afterwards without asking them to open a terminal. It records what the launcher did — never configuration
/// values, never credentials, never anything from the documents.
/// </summary>
public sealed class LauncherLog
{
    private readonly string? file;

    public LauncherLog(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            file = Path.Combine(directory, string.Create(CultureInfo.InvariantCulture, $"launcher-{DateTime.UtcNow:yyyyMMdd}.log"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A laptop where ProgramData is not writable must still start RCS; it simply gets no launcher log.
            file = null;
        }
    }

    public void Write(string message)
    {
        if (file is null)
        {
            return;
        }

        try
        {
            File.AppendAllText(file, string.Create(CultureInfo.InvariantCulture, $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ} {message}{Environment.NewLine}"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Logging must never be the reason the employee cannot work.
        }
    }
}
