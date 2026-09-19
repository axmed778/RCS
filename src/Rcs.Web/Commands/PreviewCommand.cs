using Rcs.Application.Previews;
using Rcs.Domain.Documents;
using Rcs.Infrastructure;
using Rcs.Infrastructure.Migrations;
using Rcs.Web.Hosting;

namespace Rcs.Web.Commands;

internal static class PreviewCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("list" or "verify" or "retry" or "regenerate" or "run"))
        {
            Console.Error.WriteLine("Usage: Rcs.Web preview list|verify|run|retry <preview-id>|regenerate <version-id>");
            return ExitCodes.UsageOrConfigurationError;
        }

        var configuration = CommandConfiguration.Build([]);
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddSimpleConsole());
        services.AddRcsInfrastructure(configuration);
        await using var provider = services.BuildServiceProvider();
        if (!(await provider.GetRequiredService<SchemaCompatibilityChecker>().CheckAsync()).IsCompatible) return ExitCodes.SchemaIncompatible;
        var admin = provider.GetRequiredService<IPreviewAdministration>();
        if (args[0] == "list")
        {
            foreach (var row in await admin.ListAsync(Enum.GetValues<PreviewStatus>(), 200))
                Console.WriteLine($"{row.PreviewId} {row.DocumentVersionId} {row.Status} {row.Processor} {row.AttemptCount}/{row.MaxAttempts} {row.FailureCode}");
            return 0;
        }
        if (args[0] == "verify")
        {
            var report = await admin.VerifyAsync();
            Console.WriteLine($"{report.GenerationsChecked} generations, {report.ArtifactsChecked} artifacts, {report.Findings.Count} findings");
            foreach (var finding in report.Findings) Console.WriteLine($"{finding.PreviewId} {finding.ArtifactId}: {finding.Problem}");
            return report.Findings.Count == 0 ? 0 : 4;
        }
        if (args[0] == "run")
        {
            var runner = provider.GetRequiredService<IPreviewJobRunner>();
            await runner.ReconcileAsync();
            while (await runner.RunNextAsync()) { }
            return 0;
        }
        if (args.Length != 2 || !Guid.TryParse(args[1], out var id)) return ExitCodes.UsageOrConfigurationError;
        var result = args[0] == "retry" ? await admin.RetryAsync(id) : await admin.RegenerateAsync(id);
        Console.WriteLine(result.Succeeded ? result.Value.ToString() : result.Error!.Code);
        return result.Succeeded ? 0 : 1;
    }
}
