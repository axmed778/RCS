using Rcs.Web.Commands;

namespace Rcs.Web.Hosting;

/// <summary>
/// <c>Rcs.Web</c> runs the web application. <c>Rcs.Web migrate</c> and <c>Rcs.Web check-schema</c> are explicit
/// deployment actions; normal startup never changes the schema (ARCHITECTURE.md §7.5).
/// </summary>
public static class RcsEntryPoint
{
    public static async Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length > 0 && !args[0].StartsWith('-'))
        {
            return args[0] switch
            {
                "migrate" => await MigrateCommand.RunAsync(args[1..]),
                "check-schema" => await CheckSchemaCommand.RunAsync(args[1..]),
                _ => PrintUsage(args[0]),
            };
        }

        var app = WebHostComposition.Build(args);
        await app.RunAsync();
        return ExitCodes.Success;
    }

    private static int PrintUsage(string unknownCommand)
    {
        Console.Error.WriteLine($"Unknown command '{unknownCommand}'.");
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  Rcs.Web                 run the web application (requires a compatible schema)");
        Console.Error.WriteLine("  Rcs.Web migrate         apply pending migrations with ConnectionStrings:Migration");
        Console.Error.WriteLine("  Rcs.Web check-schema    verify the schema with ConnectionStrings:Runtime; exit 3 if incompatible");
        return ExitCodes.UsageOrConfigurationError;
    }
}

public static class ExitCodes
{
    public const int Success = 0;
    public const int Failure = 1;
    public const int UsageOrConfigurationError = 2;
    public const int SchemaIncompatible = 3;
    public const int Cancelled = 130;
}
