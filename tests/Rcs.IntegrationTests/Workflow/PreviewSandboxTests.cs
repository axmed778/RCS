using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Rcs.Infrastructure.Configuration;
using Rcs.Infrastructure.Previews;

namespace Rcs.IntegrationTests.Workflow;

public sealed class PreviewSandboxTests
{
    [Fact]
    public async Task BubblewrapHidesHostCredentialsHomeDatabaseAndMakesInputReadOnly()
    {
        var root = Path.Combine(Path.GetTempPath(), "rcs-sandbox-test-" + Guid.NewGuid().ToString("N"));
        var settings = new PreviewOptions { StorageRoot = Path.Combine(root, "store"), TempRoot = Path.Combine(root, "jobs"), Worker = new PreviewWorkerOptions { Path = "/usr/bin/sh" } };
        var paths = new PreviewPaths(Options.Create(settings), Options.Create(new StorageOptions()));
        var job = paths.CreateJobDirectory(Guid.NewGuid());
        var input = Path.Combine(job.InputDirectory, "original");
        await File.WriteAllTextAsync(input, "immutable");
        var sandbox = new PreviewSandbox(Options.Create(settings), NullLogger<PreviewSandbox>.Instance);
        var result = await sandbox.RunAsync(["-c", "test ! -e /run/postgresql && test ! -e /home && test -z \"$RCS_TEST_ADMIN_CONNECTION\" && test -z \"$RCS_SECRETS_FILE\" && ! (echo changed > \"$1\") && test $(wc -l < /proc/net/route) -eq 1 && echo isolated", "probe", input],
            new SandboxMounts([job.InputDirectory], [job.WorkDirectory, job.OutputDirectory]), job.WorkDirectory, default);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("isolated", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal("immutable", await File.ReadAllTextAsync(input));
        paths.DeleteJobDirectory(job.Root);
    }
}
