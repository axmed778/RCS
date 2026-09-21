using System.Text.Json;
using Rcs.UnitTests.TestSupport;

namespace Rcs.UnitTests.Windows;

/// <summary>
/// The shipped Windows single-laptop pilot package: the configuration template the installer writes, and the
/// PowerShell scripts that will run on a laptop holding real case documents. These are text assertions on purpose —
/// the scripts cannot be executed here, so what they contain is what gets reviewed.
/// </summary>
public sealed class WindowsPilotPackageTests
{
    private static readonly string Package = RepositoryRoot.Combine("deploy", "windows-pilot");

    private static JsonDocument Template() =>
        JsonDocument.Parse(File.ReadAllText(RepositoryRoot.Combine("deploy", "config-templates", "appsettings.WindowsPilot.example.json")));

    private static string Script(string name) => File.ReadAllText(Path.Combine(Package, name));

    /// <summary>
    /// The script with its comments removed. A comment may legitimately name a dangerous thing in order to explain
    /// why the script never does it ("robocopy /MIR would delete"); an assertion about behaviour must read the code.
    /// </summary>
    private static string Code(string name) => string.Join(
        '\n',
        System.Text.RegularExpressions.Regex.Replace(Script(name), @"<#.*?#>", string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline)
            .Split('\n')
            .Where(line => !line.TrimStart().StartsWith('#'))
            .Select(line => line.Split(" #")[0]));

    [Fact]
    public void EveryPilotPathPointsAtProgramDataRatherThanTheReleaseOrTheRepository()
    {
        using var template = Template();
        var rcs = template.RootElement.GetProperty("Rcs");

        string[] paths =
        [
            rcs.GetProperty("Storage").GetProperty("RootPath").GetString()!,
            rcs.GetProperty("Storage").GetProperty("TempPath").GetString()!,
            rcs.GetProperty("Preview").GetProperty("StorageRoot").GetString()!,
            rcs.GetProperty("Preview").GetProperty("TempRoot").GetString()!,
            rcs.GetProperty("Hosting").GetProperty("DataProtectionKeysPath").GetString()!,
        ];

        Assert.All(paths, path =>
        {
            Assert.StartsWith(@"C:\ProgramData\RCS\Pilot", path, StringComparison.Ordinal);
            Assert.DoesNotContain(@"Program Files", path, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Desktop", path, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void DataProtectionKeysSurviveAReinstallBecauseTheyLiveOutsideTheRelease()
    {
        using var template = Template();

        var keys = template.RootElement.GetProperty("Rcs").GetProperty("Hosting").GetProperty("DataProtectionKeysPath").GetString();

        // Without this, every redeploy would invalidate the session cookie and the employee would be signed out.
        Assert.Equal(@"C:\ProgramData\RCS\Pilot\data-protection-keys", keys);
    }

    [Fact]
    public void TheTemplateNeverEnablesTheDemonstrationActorAndNeverCarriesASecret()
    {
        var text = File.ReadAllText(RepositoryRoot.Combine("deploy", "config-templates", "appsettings.WindowsPilot.example.json"));
        using var template = Template();

        Assert.False(template.RootElement.GetProperty("Rcs").GetProperty("Review").GetProperty("Enabled").GetBoolean());
        Assert.Equal(string.Empty, template.RootElement.GetProperty("ConnectionStrings").GetProperty("Migration").GetString());
        Assert.DoesNotContain("Password=", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password=", text, StringComparison.Ordinal);

        // The single-laptop exception to the secure-cookie rule is explicit and explained, not silent.
        Assert.False(template.RootElement.GetProperty("Rcs").GetProperty("Authentication").GetProperty("RequireSecureCookie").GetBoolean());
        Assert.Contains("127.0.0.1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheApplicationOnlyListensOnTheLaptopItself()
    {
        using var template = Template();

        Assert.Equal("http://127.0.0.1:5080", template.RootElement.GetProperty("Urls").GetString());
        Assert.Contains("127.0.0.1", template.RootElement.GetProperty("ConnectionStrings").GetProperty("Runtime").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewsAreOffOnWindowsRatherThanQuietlyBroken()
    {
        using var template = Template();

        // The converters and the bubblewrap sandbox are Linux-only; the honest state is "no preview", which the UI
        // already shows as "Ön baxış mümkün deyil" while documents stay uploadable and downloadable.
        Assert.False(template.RootElement.GetProperty("Rcs").GetProperty("Preview").GetProperty("Enabled").GetBoolean());
    }

    /// <summary>
    /// Windows PowerShell 5.1 — the one every Windows laptop has — reads a BOM-less file as ANSI, which corrupts the
    /// Azerbaijani text in these scripts and then breaks their parsing in ways that look nothing like the cause.
    /// Every shipped script therefore carries a UTF-8 byte-order mark.
    /// </summary>
    [Fact]
    public void EveryScriptIsSavedAsUtf8WithABomSoPowerShell51ReadsItCorrectly()
    {
        foreach (var script in Directory.GetFiles(Package, "*.ps1", SearchOption.AllDirectories))
        {
            var head = new byte[3];
            using var stream = File.OpenRead(script);
            var read = stream.ReadAtLeast(head, 3, throwOnEndOfStream: false);

            Assert.True(read == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF,
                $"{Path.GetFileName(script)} must be saved as UTF-8 with BOM.");
        }
    }

    [Fact]
    public void TheRestoreTestCanOnlyEverCreateAThrowAwayDatabase()
    {
        var restore = Script("restore-test.ps1");

        Assert.Contains("Assert-SafeDatabaseName -Purpose Throwaway", restore, StringComparison.Ordinal);
        Assert.Contains("rcs_restore_test_", restore, StringComparison.Ordinal);
        // It must never name the live database as a restore or drop target.
        Assert.DoesNotContain("--dbname=rcs_pilot", restore, StringComparison.Ordinal);
        Assert.DoesNotContain("dropdb --host=127.0.0.1 --username=$PostgresSuperUser rcs_pilot", restore, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDatabaseNameGuardRefusesEverythingButAThrowAwayName()
    {
        var guard = File.ReadAllText(Path.Combine(Package, "lib", "RcsPilot.Common.ps1"));

        // The rule itself, as the script enforces it: a restore target matches rcs_restore_test_*, and the live
        // databases are named and rejected outright.
        Assert.Contains("^rcs_restore_test_[a-z0-9_]{1,40}$", guard, StringComparison.Ordinal);
        Assert.Contains("'rcs_dev'", guard, StringComparison.Ordinal);
        Assert.Contains("'postgres'", guard, StringComparison.Ordinal);
        Assert.Contains("'template1'", guard, StringComparison.Ordinal);
    }

    [Fact]
    public void NoScriptCanWriteRealDocumentsIntoTheRepositoryOrASystemDirectory()
    {
        var guard = File.ReadAllText(Path.Combine(Package, "lib", "RcsPilot.Common.ps1"));

        Assert.Contains("Refusing to use a drive root", guard, StringComparison.Ordinal);
        Assert.Contains("it is inside a source repository", guard, StringComparison.Ordinal);
        Assert.Contains("Refusing to use a system directory", guard, StringComparison.Ordinal);

        foreach (var script in new[] { "install.ps1", "backup.ps1", "restore-test.ps1" })
        {
            Assert.Contains("RcsPilot.Common.ps1", Script(script), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheBackupCopiesOriginalsAfterTheDumpAndNeverMirrors()
    {
        var backup = Code("backup.ps1");
        var dump = backup.IndexOf("$pg.PgDump", StringComparison.Ordinal);
        var objects = backup.IndexOf("robocopy.exe", StringComparison.Ordinal);

        // ADR-021: dump first, objects second, so every object the dump names exists in the copy.
        Assert.True(dump > 0 && objects > dump, "the database must be dumped before the objects are copied");

        // /MIR deletes files at the destination; nothing in RCS backup may ever delete.
        Assert.DoesNotContain("/MIR", backup, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-Item", backup, StringComparison.Ordinal);

        // A dump alone is not a backup, and the script says so where an operator will read it.
        Assert.Contains("NOT a backup", Script("backup.ps1"), StringComparison.Ordinal);
        Assert.Contains("RECOVERY_POINT", backup, StringComparison.Ordinal);
        Assert.Contains("objects.sha256", backup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBackupLeavesCredentialsOutOfTheRecoveryPoint()
    {
        var backup = Script("backup.ps1");

        Assert.Contains("pgpass.conf holds database passwords and is deliberately NOT copied", backup, StringComparison.Ordinal);
        Assert.DoesNotContain("Copy-Item -LiteralPath $pgpass", backup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheInstallerNeverWritesADatabasePasswordIntoConfiguration()
    {
        var install = Script("install.ps1");

        // Passwords go to the employee's own pgpass file; appsettings stays shareable and diff-able.
        Assert.Contains("pgpass.conf", install, StringComparison.Ordinal);
        Assert.DoesNotContain("Password=$appPassword", install, StringComparison.Ordinal);
        Assert.DoesNotContain("Password=$migratePassword", install, StringComparison.Ordinal);
        Assert.Contains("Read-Host", install, StringComparison.Ordinal);
        Assert.Contains("-AsSecureString", install, StringComparison.Ordinal);
    }

    [Fact]
    public void TheInstallerSetsUpExactlyThePilotRoleGrants()
    {
        var install = Script("install.ps1");

        Assert.Contains("user bootstrap-admin", install, StringComparison.Ordinal);
        Assert.Contains("--role=HEAD", install, StringComparison.Ordinal);
        Assert.Contains("--role=CHIEF", install, StringComparison.Ordinal);
        Assert.Contains("--role=WORKER", install, StringComparison.Ordinal);
        Assert.Contains("user set-department", install, StringComparison.Ordinal);

        // The employee gets a desktop icon rather than a command line.
        Assert.Contains("Rcs.Launcher.exe", install, StringComparison.Ordinal);
        Assert.Contains(".lnk", install, StringComparison.Ordinal);
    }

    [Fact]
    public void TheInstallerNeverTouchesTheDevelopmentDatabase()
    {
        foreach (var script in new[] { "install.ps1", "backup.ps1", "restore-test.ps1" })
        {
            // The guard list in the shared library names rcs_dev in order to reject it; no script may act on it.
            var text = Code(script).Replace("'rcs_dev'", string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain("rcs_dev", text, StringComparison.Ordinal);
            Assert.DoesNotContain("seed-demo", text, StringComparison.Ordinal);
        }
    }
}
