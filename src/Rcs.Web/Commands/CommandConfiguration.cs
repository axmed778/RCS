using Rcs.Web.Configuration;

namespace Rcs.Web.Commands;

/// <summary>
/// Configuration and logging for the deployment commands. No web host is built: the commands read
/// appsettings from the application directory, environment variables, command-line switches and, last,
/// the <c>RCS_SECRETS_FILE</c>.
/// </summary>
internal static class CommandConfiguration
{
    public static IConfigurationRoot Build(string[] args)
    {
        var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environments.Production;

        return new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddJsonFile($"appsettings.{environment}.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .AddCommandLine(args)
            .AddRcsSecretsFile()
            .Build();
    }

    public static ILoggerFactory CreateLoggerFactory(IConfiguration configuration) =>
        LoggerFactory.Create(logging => logging
            .AddConfiguration(configuration.GetSection("Logging"))
            .AddSimpleConsole(console =>
            {
                console.SingleLine = true;
                console.UseUtcTimestamp = true;
                console.TimestampFormat = "yyyy-MM-ddTHH:mm:ssZ ";
            }));
}
