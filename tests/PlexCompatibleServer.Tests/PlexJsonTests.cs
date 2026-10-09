using System.Text.Json;
using NUnit.Framework;
using PlexCompatibleServer.Api.Serialization;

namespace PlexCompatibleServer.Tests;

[TestFixture]
public class PlexJsonTests
{
    [Test]
    public void Serialize_WrapsInMediaContainerRoot()
    {
        var json = PlexJson.Serialize(new XmlIdentity
        {
            Size = 0,
            ApiVersion = "1.2.3",
            Claimed = "0",
            MachineIdentifier = "abc123",
            Version = "1.43.4.10903-e5521bd8c"
        });

        using var doc = JsonDocument.Parse(json);
        Assert.That(doc.RootElement.TryGetProperty("MediaContainer", out _), Is.True);
    }

    [Test]
    public void Serialize_EmitsNumericAttributesAsNumbers()
    {
        var json = PlexJson.Serialize(new XmlIdentity { Size = 3 });

        using var doc = JsonDocument.Parse(json);
        var container = doc.RootElement.GetProperty("MediaContainer");
        Assert.That(container.GetProperty("size").ValueKind, Is.EqualTo(JsonValueKind.Number));
        Assert.That(container.GetProperty("size").GetInt32(), Is.EqualTo(3));
    }

    [Test]
    public void Serialize_OmitsNullOptionalAttributes()
    {
        var json = PlexJson.Serialize(new XmlMediaContainer { Size = 0, LibrarySectionId = "" });

        using var doc = JsonDocument.Parse(json);
        var container = doc.RootElement.GetProperty("MediaContainer");
        Assert.That(container.TryGetProperty("librarySectionID", out _), Is.False);
        Assert.That(container.GetProperty("size").GetInt32(), Is.Zero);
    }

    [Test]
    public void Serialize_EmitsChildCollectionsAsArrays()
    {
        var model = new XmlMediaContainer
        {
            Size = 2,
            Directories = new List<XmlDirectory>
            {
                new() { Key = "1", Title = "Movies", Type = "movie" },
                new() { Key = "2", Title = "TV Shows", Type = "show" }
            }
        };

        var json = PlexJson.Serialize(model);

        using var doc = JsonDocument.Parse(json);
        var dirs = doc.RootElement.GetProperty("MediaContainer").GetProperty("Directory");
        Assert.That(dirs.GetArrayLength(), Is.EqualTo(2));
        Assert.That(dirs[0].GetProperty("title").GetString(), Is.EqualTo("Movies"));
        Assert.That(dirs[1].GetProperty("type").GetString(), Is.EqualTo("show"));
    }

    [Test]
    public void Serialize_OmitsEmptyChildCollections()
    {
        var json = PlexJson.Serialize(new XmlMediaContainer { Size = 0 });

        using var doc = JsonDocument.Parse(json);
        var container = doc.RootElement.GetProperty("MediaContainer");
        Assert.That(container.TryGetProperty("Directory", out _), Is.False);
        Assert.That(container.TryGetProperty("Video", out _), Is.False);
    }

    [Test]
    public void Serialize_ProducesValidJsonForFullServerInfo()
    {
        var json = PlexJson.Serialize(new XmlServerInfo
        {
            Size = 1,
            FriendlyName = "Test \"Server\"",
            MachineIdentifier = "mid",
            AllowSync = "0",
            OwnerFeatures = "a,b,c",
            Directories = new List<XmlRootDirectory>
            {
                new() { Count = 1, Key = "library", Title = "library" }
            }
        });

        using var doc = JsonDocument.Parse(json);
        var container = doc.RootElement.GetProperty("MediaContainer");
        Assert.That(container.GetProperty("friendlyName").GetString(), Is.EqualTo("Test \"Server\""));
        // Plex types allowSync as a boolean in JSON even though the XML dialect uses 0/1.
        Assert.That(container.GetProperty("allowSync").ValueKind, Is.EqualTo(JsonValueKind.False));
        Assert.That(container.GetProperty("ownerFeatures").GetString(), Is.EqualTo("a,b,c"));
        Assert.That(container.GetProperty("Directory")[0].GetProperty("count").GetInt32(), Is.EqualTo(1));
    }

    [Test]
    public void Serialize_RenamesVideoElementsToMetadata()
    {
        var json = PlexJson.Serialize(new XmlHubContainer
        {
            Size = 1,
            Hubs =
            {
                new XmlHub
                {
                    Key = "/hubs/home/recentlyAdded?type=1",
                    Title = "Recently Added Movies",
                    Type = "movie",
                    HubIdentifier = "home.movies.recent",
                    Context = "hub.home.movies.recent",
                    Size = 1,
                    Videos = { new XmlVideo { RatingKey = 847, Title = "Matilda", Key = "/library/metadata/847" } }
                }
            }
        });

        using var doc = JsonDocument.Parse(json);
        var hub = doc.RootElement.GetProperty("MediaContainer").GetProperty("Hub")[0];

        // The webOS client only reads "Metadata"; "Video" yields an empty home screen.
        Assert.That(hub.TryGetProperty("Metadata", out _), Is.True);
        Assert.That(hub.TryGetProperty("Video", out _), Is.False);

        // Real Plex sends ratingKey as a string, so the client treats it as one.
        var ratingKey = hub.GetProperty("Metadata")[0].GetProperty("ratingKey");
        Assert.That(ratingKey.ValueKind, Is.EqualTo(JsonValueKind.String));
        Assert.That(ratingKey.GetString(), Is.EqualTo("847"));
    }

    [Test]
    public void Serialize_KeepsVideoElementNameInXml()
    {
        var xml = PlexXml.Serialize(new XmlHubContainer
        {
            Size = 1,
            Hubs =
            {
                new XmlHub
                {
                    Key = "/hubs/home/recentlyAdded?type=1",
                    Videos = { new XmlVideo { RatingKey = 847, Title = "Matilda" } }
                }
            }
        });

        Assert.That(xml, Does.Contain("<Video "));
        Assert.That(xml, Does.Not.Contain("<Metadata "));
    }

    [Test]
    public void XmlAndJson_ExposeTheSameAttributeNames()
    {
        var model = new XmlIdentity
        {
            Size = 0,
            ApiVersion = "1.2.3",
            Claimed = "0",
            MachineIdentifier = "mid",
            Version = "1.43.4.10903-e5521bd8c"
        };

        var xml = PlexXml.Serialize(model);
        var json = PlexJson.Serialize(model);

        using var doc = JsonDocument.Parse(json);
        var jsonNames = doc.RootElement.GetProperty("MediaContainer")
            .EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToList();

        foreach (var name in jsonNames)
        {
            Assert.That(xml, Does.Contain($"{name}=\""), $"XML is missing attribute '{name}'");
        }
    }
}
