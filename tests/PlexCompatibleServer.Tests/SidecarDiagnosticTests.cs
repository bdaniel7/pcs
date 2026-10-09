using System.Reflection;
using System.Text.Json;
using NUnit.Framework;
using PlexCompatibleServer.Api.Controllers;
using PlexCompatibleServer.Api.Serialization;
using PlexCompatibleServer.Core.Models;

namespace PlexCompatibleServer.Tests;

public class SidecarDiagnosticTests
{
    private static string FindApiProjectDir()
    {
        var dir = TestContext.CurrentContext.TestDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "PlexCompatibleServer.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.That(dir, Is.Not.Null, "repo root not found");
        return Path.Combine(dir!, "src", "PlexCompatibleServer.Api");
    }

    private ExternalMetadata _metadata = null!;

    [SetUp]
    public void SetUp() => _metadata = new ExternalMetadata();

    private void ResetSidecarCache()
    {
        typeof(SidecarStore)
            .GetField("_cache", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(_metadata.Store, null);
    }

    [Test]
    public void Sidecar_json_deserializes_with_sidecar_item_type()
    {
        var path = Path.Combine(FindApiProjectDir(), "wwwroot", "plex-metadata.json");

        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, SidecarItem>>(
                File.ReadAllText(path), SidecarStore.Options);
            TestContext.WriteLine($"loaded {dict!.Count} entries");
            Assert.That(dict.Count, Is.GreaterThan(0));
        }
        catch (Exception ex)
        {
            Assert.Fail($"deserialize threw: {ex}");
        }
    }

    [Test]
    public async Task Disclosure_day_sidecar_match()
    {
        var prev = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(FindApiProjectDir());
        ResetSidecarCache();
        try
        {
            var item = new MediaItem
            {
                Id = 9,
                Title = "Disclosure Day 2026",
                FilePath = @"Z:\Ser\gogo.mkv"
            };
            var video = new XmlVideo { Title = item.Title };

            try
            {
                await _metadata.ApplyAsync(item, video);
            }
            catch (Exception ex)
            {
                Assert.Fail($"Apply threw: {ex}");
            }

            Assert.That(video.Summary, Is.Not.Empty,
                $"no sidecar match: guid={video.Guid} studio='{video.Studio}' titleSort='{video.TitleSort}'");
            TestContext.WriteLine($"summary len={video.Summary?.Length} guid={video.Guid} studio='{video.Studio}'");
        }
        finally
        {
            Directory.SetCurrentDirectory(prev);
            ResetSidecarCache();
        }
    }
}
