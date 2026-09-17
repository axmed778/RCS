namespace Rcs.UnitTests.TestSupport;

/// <summary>Locates the repository root (the directory holding Rcs.sln) from the test output directory.</summary>
internal static class RepositoryRoot
{
    private static readonly Lazy<string> Root = new(() =>
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Rcs.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Rcs.sln was not found above the test output directory.");
    });

    public static string FullPath => Root.Value;

    public static string Combine(params string[] parts) => Path.Combine([Root.Value, .. parts]);
}

/// <summary>A clock that always returns the same instant.</summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
