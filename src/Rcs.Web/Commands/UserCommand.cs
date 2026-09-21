using System.Data.Common;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Rcs.Application.Identity;
using Rcs.Domain.Vocabulary;
using Rcs.Infrastructure;
using Rcs.Infrastructure.Identity;
using Rcs.Infrastructure.Migrations;
using Rcs.Web.Hosting;

namespace Rcs.Web.Commands;

/// <summary>
/// <c>Rcs.Web user …</c> — the installation-time identity commands (PERMISSIONS.md §25.2 bootstrap; SECURITY.md §6.8
/// break-glass). They need shell access to the server and the runtime database credentials, and they exist because a
/// new database has nobody who can sign in yet. Everything they do is written to the audit trail.
/// </summary>
/// <remarks>
/// A password is never taken as a command-line argument — it would land in shell history and in the process list.
/// Either it is read from standard input (<c>--password-stdin</c>), or the command generates a temporary one, prints
/// it once, and marks it as "must be changed at first sign-in".
/// </remarks>
internal static class UserCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            return Usage();
        }

        var configuration = CommandConfiguration.Build(args.Where(argument => argument.StartsWith("--", StringComparison.Ordinal) && argument.Contains('=', StringComparison.Ordinal)).ToArray());
        using var loggerFactory = CommandConfiguration.CreateLoggerFactory(configuration);
        var logger = loggerFactory.CreateLogger("Rcs.User");

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

            var bootstrap = provider.GetRequiredService<IdentityBootstrap>();
            var command = args[0];
            var options = Options(args);
            return command switch
            {
                "bootstrap-admin" => Report(logger, await bootstrap.CreateAdministratorAsync(
                    Required(options, "username"), Required(options, "full-name"), options.GetValueOrDefault("display-name"), ReadPassword(options))),
                "create" => Report(logger, await bootstrap.CreateUserAsync(
                    Required(options, "username"), Required(options, "full-name"), options.GetValueOrDefault("display-name"),
                    options.GetValueOrDefault("job-title"), ReadPassword(options), Required(options, "by"))),
                "set-password" => Report(logger, await bootstrap.SetPasswordAsync(
                    Required(options, "username"), ReadPassword(options), Required(options, "by"))),
                "grant" => Report(logger, await bootstrap.GrantRoleAsync(
                    Required(options, "username"), ParseRole(Required(options, "role")), Required(options, "by"))),
                "list" => await ListAsync(provider, logger),
                "set-department" => ReportDepartment(logger, await provider.GetRequiredService<Rcs.Infrastructure.Organizations.DepartmentBootstrap>()
                    .SetOwnOrganizationAsync(Required(options, "name"), options.GetValueOrDefault("short-name"),
                        options.GetValueOrDefault("type") ?? "MUNICIPAL_DEPARTMENT", Required(options, "by"))),
                _ => Usage(),
            };
        }
        catch (ArgumentException exception)
        {
            logger.LogError("{Message}", exception.Message);
            return ExitCodes.UsageOrConfigurationError;
        }
        catch (DbException exception)
        {
            logger.LogError(exception, "The database could not be reached with the runtime credentials.");
            return ExitCodes.Failure;
        }
    }

    private static async Task<int> ListAsync(ServiceProvider provider, ILogger logger)
    {
        // Read-only, and deliberately available without an actor: an administrator locked out of the UI must still be
        // able to see who holds which role before deciding what to fix.
        var users = await provider.GetRequiredService<IUserDirectory>().ListAssignableAsync();
        foreach (var user in users)
        {
            logger.LogInformation("{DisplayName} ({JobTitle})", user.DisplayName, user.JobTitle ?? "-");
        }

        logger.LogInformation("{Count} user(s) with a business role.", users.Count);
        return ExitCodes.Success;
    }

    private static int Report(ILogger logger, Rcs.Application.Common.CommandResult<BootstrapOutcome> result)
    {
        if (!result.Succeeded)
        {
            logger.LogError("Refused: {Code}", result.Error!.Code);
            return ExitCodes.Failure;
        }

        var outcome = result.Value!;
        logger.LogInformation("{Username}: {Summary}", outcome.Username, outcome.Summary);
        if (outcome.TemporaryPassword is { Length: > 0 } temporary)
        {
            // Printed once, to be handed over in person. It is not stored anywhere in readable form.
            Console.WriteLine();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  temporary password for {outcome.Username}: {temporary}"));
            Console.WriteLine("  hand it over in person; it expires shortly and must be changed at first sign-in.");
            Console.WriteLine();
        }

        return ExitCodes.Success;
    }

    private static int ReportDepartment(ILogger logger, Rcs.Application.Common.CommandResult<Rcs.Infrastructure.Organizations.DepartmentOutcome> result)
    {
        if (!result.Succeeded)
        {
            logger.LogError("Refused: {Code}", result.Error!.Code);
            return ExitCodes.Failure;
        }

        logger.LogInformation("{Name}: {Summary}", result.Value!.OfficialName, result.Value.Summary);
        return ExitCodes.Success;
    }

    private static Dictionary<string, string> Options(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var argument in args.Where(argument => argument.StartsWith("--", StringComparison.Ordinal)))
        {
            var separator = argument.IndexOf('=', StringComparison.Ordinal);
            if (separator > 2)
            {
                options[argument[2..separator]] = argument[(separator + 1)..];
            }
            else
            {
                options[argument[2..]] = "true";
            }
        }

        return options;
    }

    private static string Required(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && value.Length > 0 && value != "true"
            ? value
            : throw new ArgumentException($"--{name}=<value> is required.");

    /// <summary>Reads the password from standard input, so it never appears in shell history or the process list.</summary>
    private static string? ReadPassword(Dictionary<string, string> options)
    {
        if (!options.ContainsKey("password-stdin"))
        {
            return null;
        }

        var password = Console.In.ReadToEnd().Trim('\r', '\n', ' ');
        return password.Length == 0 ? throw new ArgumentException("No password was supplied on standard input.") : password;
    }

    private static BusinessRole ParseRole(string role) =>
        Enum.GetValues<BusinessRole>().FirstOrDefault(value => string.Equals(value.ToCode(), role, StringComparison.OrdinalIgnoreCase)) is var parsed && parsed != default
            ? parsed
            : throw new ArgumentException("--role must be WORKER, CHIEF, HEAD or TECH_ADMIN.");

    private static int Usage()
    {
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  Rcs.Web user bootstrap-admin --username=<name> --full-name=\"<name>\" [--display-name=\"<name>\"] [--password-stdin]");
        Console.Error.WriteLine("        creates the first technical administrator of a new database and gives it TECH_ADMIN");
        Console.Error.WriteLine("  Rcs.Web user create --username=<name> --full-name=\"<name>\" --by=<techadmin> [--job-title=\"…\"] [--password-stdin]");
        Console.Error.WriteLine("  Rcs.Web user set-password --username=<name> --by=<techadmin> [--password-stdin]");
        Console.Error.WriteLine("  Rcs.Web user grant --username=<name> --role=WORKER|CHIEF|HEAD|TECH_ADMIN --by=<head, or techadmin for the first Head>");
        Console.Error.WriteLine("  Rcs.Web user list");
        Console.Error.WriteLine("  Rcs.Web user set-department --name=\"<official name>\" [--short-name=\"…\"] [--type=MUNICIPAL_DEPARTMENT] --by=<techadmin>");
        Console.Error.WriteLine("        records which organization the department itself is; required once before the first case");
        return ExitCodes.UsageOrConfigurationError;
    }
}
