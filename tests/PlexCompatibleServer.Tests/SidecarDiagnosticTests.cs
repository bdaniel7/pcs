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

    private static void ResetSidecarCache()
    {
        typeof(ExternalMetadata)
            .GetField("_cache", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, null);
    }

    [Test]
    public void Sidecar_json_deserializes_with_sidecar_item_type()
    {
        var path = Path.Combine(FindApiProjectDir(), "wwwroot", "plex-metadata.json");

        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, SidecarItem>>(
                File.ReadAllText(path), ExternalMetadata.Options);
            TestContext.WriteLine($"loaded {dict!.Count} entries");
            Assert.That(dict.Count, Is.GreaterThan(0));
        }
        catch (Exception ex)
        {
            Assert.Fail($"deserialize threw: {ex}");
        }
    }

    [Test]
    public void Disclosure_day_sidecar_match()
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
                ExternalMetadata.Apply(item, video);
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
