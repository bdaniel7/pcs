using NUnit.Framework;

namespace PlexCompatibleServer.Tests;

/// <summary>
/// Phase 2 guard. The metadata lookup path used to block request threads on plex.tv via
/// <c>Http.SendAsync(...).GetAwaiter().GetResult()</c>; it is awaited throughout now. This pins
/// that so a future edit cannot quietly reintroduce the blocking call.
/// </summary>
[TestFixture]
public class BlockingIoGuardTests
{
    [Test]
    public void Api_sources_contain_no_blocking_await_hacks()
    {
        var root = findRepoRoot();
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file);

            if (relative.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj")) continue;

            if (File.ReadAllText(file).Contains("GetAwaiter().GetResult()", StringComparison.Ordinal))
                offenders.Add(relative);
        }

        Assert.That(offenders, Is.Empty,
            "sync-over-async must not reappear; network I/O stays awaited");
    }

    private static string findRepoRoot()
    {
        var dir = TestContext.CurrentContext.TestDirectory;

        while (dir is not null && !File.Exists(Path.Combine(dir, "PlexCompatibleServer.sln")))
            dir = Path.GetDirectoryName(dir);

        Assert.That(dir, Is.Not.Null, "repo root not found");

        return dir!;
    }
}
