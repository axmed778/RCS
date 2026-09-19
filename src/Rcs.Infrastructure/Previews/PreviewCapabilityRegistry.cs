using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rcs.Application.Previews;
using Rcs.Domain.Documents;

namespace Rcs.Infrastructure.Previews;

/// <summary>
/// What each processor can do on this server, learned by running the worker's <c>probe</c> inside the same sandbox the
/// jobs run in — so "available" means "available to the sandboxed worker", not merely "installed somewhere". A processor
/// that is not available produces an honest UNSUPPORTED generation, never a fake preview.
/// </summary>
public sealed class PreviewCapabilityRegistry(
    PreviewSandbox sandbox,
    PreviewPaths paths,
    IOptions<PreviewOptions> options,
    ILogger<PreviewCapabilityRegistry> logger)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private IReadOnlyDictionary<string, PreviewProcessorCapability>? current;

    public IReadOnlyDictionary<string, PreviewProcessorCapability>? Current => current;

    /// <summary>The probed capability of one processor, or null when the probe has not succeeded.</summary>
    public PreviewProcessorCapability? Find(string processor) =>
        current is not null && current.TryGetValue(processor, out var capability) ? capability : null;

    /// <summary>Test seam: a fixed capability table instead of a probe.</summary>
    public void Set(IEnumerable<PreviewProcessorCapability> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        current = capabilities.ToDictionary(capability => capability.Processor, StringComparer.Ordinal);
    }

    public async Task<IReadOnlyDictionary<string, PreviewProcessorCapability>?> EnsureAsync(CancellationToken cancellationToken) =>
        current ?? await RefreshAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<string, PreviewProcessorCapability>?> RefreshAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        string? directory = null;
        try
        {
            directory = paths.CreateProbeDirectory();
            var toolsFile = Path.Combine(directory, "tools.json");
            await File.WriteAllTextAsync(toolsFile, JsonSerializer.Serialize(options.Value.Tools, PreviewProtocol.Json), cancellationToken);
            var run = await sandbox.RunAsync(["probe", toolsFile], new SandboxMounts([directory], []), directory, cancellationToken);
            if (run.ExitCode != 0 || run.TimedOut)
            {
                logger.LogError("The preview capability probe failed (exit {Exit}, timed out {TimedOut}): {Error}",
                    run.ExitCode, run.TimedOut, Truncate(run.StandardError));
                return current;
            }

            var capabilities = JsonSerializer.Deserialize<PreviewCapabilities>(run.StandardOutput, PreviewProtocol.Json);
            var valid = capabilities?.Processors
                .Where(capability => PreviewRouting.AllProcessors.Contains(capability.Processor) && PreviewVersion.IsValid(capability.Version))
                .ToDictionary(capability => capability.Processor, StringComparer.Ordinal);
            if (valid is null)
            {
                logger.LogError("The preview capability probe returned no usable answer.");
                return current;
            }

            foreach (var capability in valid.Values)
            {
                logger.LogInformation("Preview processor {Processor}: {State} {Version} {Reason}",
                    capability.Processor, capability.Available ? "available" : "UNAVAILABLE", capability.Version, capability.Reason);
            }

            current = valid;
            return current;
        }
        catch (JsonException)
        {
            logger.LogError("The preview capability probe returned malformed output.");
            return current;
        }
        finally
        {
            gate.Release();
            if (directory is not null) paths.DeleteJobDirectory(directory);
        }
    }

    private static string Truncate(string text) => text.Length <= 2000 ? text : text[..2000];
}

/// <summary>The shape a processor version or settings key must have to be recorded (mirrors the CHECK in migration 0015).</summary>
public static partial class PreviewVersion
{
    public static bool IsValid(string? value) => value is not null && Pattern().IsMatch(value);

    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._+;=-]{0,199}\z")]
    private static partial System.Text.RegularExpressions.Regex Pattern();
}
