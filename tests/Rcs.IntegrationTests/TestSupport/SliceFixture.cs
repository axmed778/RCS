using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Rcs.Application.Cases;
using Rcs.Application.Common;
using Rcs.Application.Lookups;
using Rcs.Application.Organizations;
using Rcs.Application.Workflow;
using Rcs.Infrastructure;
using Rcs.Infrastructure.Development;

namespace Rcs.IntegrationTests.TestSupport;

/// <summary>
/// A migrated database with the Development demo data, and the real application services wired to it exactly as the
/// host wires them. The tests drive the services, not the SQL, so they exercise guards, consequences and audit too.
/// </summary>
internal sealed class SliceFixture : IAsyncDisposable
{
    private ServiceProvider provider = null!;

    public TestDatabase Database { get; private set; } = null!;

    public ICaseService Cases => provider.GetRequiredService<ICaseService>();

    public ICaseQueries Queries => provider.GetRequiredService<ICaseQueries>();

    public IWorkflowService Workflow => provider.GetRequiredService<IWorkflowService>();

    public IOrganizationService Organizations => provider.GetRequiredService<IOrganizationService>();

    public ILookupQueries Lookups => provider.GetRequiredService<ILookupQueries>();

    public Rcs.Application.Documents.IDocumentService Documents => provider.GetRequiredService<Rcs.Application.Documents.IDocumentService>();

    public Rcs.Application.Documents.IDocumentQueries DocumentQueries => provider.GetRequiredService<Rcs.Application.Documents.IDocumentQueries>();

    public Rcs.Application.Documents.IDocumentIntegrityService Integrity => provider.GetRequiredService<Rcs.Application.Documents.IDocumentIntegrityService>();

    public string StorageRoot { get; } = Path.Combine(Path.GetTempPath(), "rcs-document-tests", Guid.NewGuid().ToString("N"));

    public Rcs.Application.Lifecycle.ICaseLifecycleService Lifecycle => provider.GetRequiredService<Rcs.Application.Lifecycle.ICaseLifecycleService>();

    public Rcs.Application.Lifecycle.ILifecycleQueries LifecycleQueries => provider.GetRequiredService<Rcs.Application.Lifecycle.ILifecycleQueries>();

    public DemoDataSeeder Seeder => provider.GetRequiredService<DemoDataSeeder>();

    public Rcs.Application.Identifiers.IIdGenerator Ids => provider.GetRequiredService<Rcs.Application.Identifiers.IIdGenerator>();

    /// <summary>The synthetic Chief the demo data creates; the Review build acts as this user by default.</summary>
    public ActorContext Chief => new(DemoData.ReviewActorUserId, "integration-test");

    /// <summary>The synthetic Head — the only actor who may approve a final result or override a closure guard.</summary>
    public ActorContext Head => new(DemoData.ReviewHeadUserId, "integration-test");

    public static async Task<SliceFixture> CreateAsync(bool seedDemoData = true)
    {
        var fixture = new SliceFixture { Database = await TestDatabase.CreateAsync() };
        await fixture.Database.MigrateAsync();

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        services.AddRcsInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Runtime"] = fixture.Database.RuntimeConnectionString,
            ["Rcs:Storage:RootPath"] = Path.Combine(fixture.StorageRoot, "objects"),
            ["Rcs:Storage:TempPath"] = Path.Combine(fixture.StorageRoot, "temporary"),
            ["Rcs:Database:ExpectedSchemaVersion"] = Rcs.Infrastructure.Migrations.MigrationSet.LoadEmbedded().LatestVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }).Build());

        fixture.provider = services.BuildServiceProvider();

        if (seedDemoData)
        {
            await fixture.Seeder.SeedAsync(isDevelopmentEnvironment: true);
        }

        return fixture;
    }

    public Rcs.Application.Idempotency.OperationId NewOperation() => new(Ids.NewId());

    public async Task AttachSignedDecisionAsync(Guid caseId, Guid resultId)
    {
        using var bytes = new MemoryStream("%PDF-1.7\nSynthetic signed decision\n%%EOF"u8.ToArray());
        var result = await Documents.UploadDocumentAsync(Chief, new(
            NewOperation(), caseId, new(Rcs.Domain.Documents.DocumentTargetKind.FinalResult, resultId),
            Rcs.Domain.Vocabulary.DocumentLinkRoleCodes.FinalResultDocument, "Synthetic decision",
            Rcs.Domain.Vocabulary.DocumentKindCodes.Other, null, null, null), new(bytes, "decision.pdf", "application/pdf"));
        Assert.True(result.Succeeded, result.Error?.Code);
    }

    /// <summary>Fails the test with the command's message code instead of a bare null reference.</summary>
    public static Guid Succeeded(CommandResult<Guid> result)
    {
        Assert.True(result.Succeeded, $"{result.Error?.Kind} {result.Error?.Code} {result.Error?.Field}");
        return result.Value;
    }

    public async Task<CaseWorkspace> WorkspaceAsync(Guid caseId)
    {
        var result = await Queries.GetWorkspaceAsync(Chief, caseId);
        Assert.True(result.Succeeded, result.Error?.Code);
        return result.Value!;
    }

    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(Database.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default! : (T)value;
    }

    public async ValueTask DisposeAsync()
    {
        await provider.DisposeAsync();
        await Database.DisposeAsync();
    }
}
