using Rcs.Web.Hosting;

return await RcsEntryPoint.RunAsync(args);

/// <summary>Entry point. Public so integration tests can host the application.</summary>
public partial class Program
{
}
