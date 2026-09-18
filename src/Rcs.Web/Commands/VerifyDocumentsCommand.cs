using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Rcs.Application.Documents;
using Rcs.Infrastructure;
using Rcs.Infrastructure.Migrations;
using Rcs.Web.Hosting;

namespace Rcs.Web.Commands;

/// <summary>
/// <c>Rcs.Web verify-documents [--rehash] [--orphans]</c>: the integrity check of DOCUMENT_MODEL.md §11 — every version's
/// object must exist with its recorded size (and, with <c>--rehash</c>, its SHA-256); <c>--orphans</c> also lists
/// objects no version references. Read-only: nothing is repaired, moved or deleted. Exit 4 on a critical finding.
/// </summary>
internal static class VerifyDocumentsCommand
{
    public const int IntegrityFailure = 4;

    public static async Task<int> RunAsync(string[] args)
    {
        var configuration = CommandConfiguration.Build(args.Where(arg => arg is not ("--rehash" or "--orphans")).ToArray());
        using var loggerFactory = CommandConfiguration.CreateLoggerFactory(configuration);
        var logger = loggerFactory.CreateLogger("Rcs.VerifyDocuments");

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddConfiguration(configuration.GetSection("Logging")).AddSimpleConsole(console => console.SingleLine = true));
        services.AddRcsInfrastructure(configuration);
        await using var provider = services.BuildServiceProvider();
        try
        {
            var schema = await provider.GetRequiredService<SchemaCompatibilityChecker>().CheckAsync();
            if (!schema.IsCompatible)
            {
                logger.LogError("{Status}: {Message}", schema.Status, schema.Message);
                return ExitCodes.SchemaIncompatible;
            }

            var report = await provider.GetRequiredService<IDocumentIntegrityService>()
                .CheckAsync(rehash: args.Contains("--rehash"), scanForUnreferenced: args.Contains("--orphans"));
            foreach (var finding in report.Findings)
            {
                logger.LogWarning("{Kind} {VersionId} {Path}: {Detail}", finding.Kind, finding.VersionId, finding.StoragePath, finding.Detail);
            }

            logger.LogInformation("{Versions} version(s) checked, {Objects} object(s) scanned, {Findings} finding(s).",
                report.VersionsChecked, report.ObjectsScanned, report.Findings.Count);
            return report.IsClean ? ExitCodes.Success : IntegrityFailure;
        }
        catch (DbException exception)
        {
            logger.LogError(exception, "The database could not be reached with the runtime credentials.");
            return ExitCodes.Failure;
        }
    }
}
