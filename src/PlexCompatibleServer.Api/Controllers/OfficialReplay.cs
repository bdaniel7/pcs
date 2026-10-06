using System.Text.Json;
using System.Text.Json.Nodes;

namespace PlexCompatibleServer.Api.Controllers;

/// <summary>
/// TEMPORARY DIAGNOSTIC - delete once the movie detail path is understood.
///
/// Reads a response body captured from an official Plex Media Server for this same client, so the
/// server can replay genuine Plex bytes instead of generated ones. Used to answer one question:
/// does the detail screen render from a real Plex body? If it does, the fault is in the data we
/// generate. If it does not, the fault is somewhere the body cannot explain.
/// </summary>
internal static class OfficialReplay
{
    private const string CaptureDir = @"C:\Users\Daniel\AppData\Local\Temp\opencode\ref_dump";

    /// <summary>
    /// True when the item response should be replayed. PCS_REPLAY_OFFICIAL covers both endpoints;
    /// the per-endpoint flags let a single one be replayed while the other stays generated, which
    /// is how the culprit is isolated.
    /// </summary>
    public static bool ItemEnabled =>
        Flag("PCS_REPLAY_OFFICIAL") || Flag("PCS_REPLAY_ITEM");

    public static bool HubEnabled =>
        Flag("PCS_REPLAY_OFFICIAL") || Flag("PCS_REPLAY_HUB");

    private static bool Flag(string name) => Environment.GetEnvironmentVariable(name) == "1";

    /// <summary>
    /// Regions of our generated item that replace the matching region of the official body.
    /// Empty means replay the official body untouched. Bisecting by region - rather than by
    /// guessing individual fields - localises the fault in one request per round.
    /// </summary>
    public static HashSet<string> MergeRegions => new(
        (Environment.GetEnvironmentVariable("PCS_MERGE") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>Keys copied from our item when a region is merged, grouped by region name.</summary>
    public static IReadOnlyDictionary<string, string[]> Regions { get; } = new Dictionary<string, string[]>
    {
        ["streams"] = new[] { "Media" },
        ["tags"] = new[]
        {
            "Genre", "Director", "Writer", "Role", "Rating", "Country", "Producer",
            "CommonSenseMedia", "UltraBlurColors"
        },
        ["review"] = new[] { "Review", "Extras" },
    };

    /// <summary>Regions handled by copying primitives rather than whole keys.</summary>
    private static readonly HashSet<string> PrimitiveRegions = new(StringComparer.OrdinalIgnoreCase)
    {
        "scalars", "container"
    };

    /// <summary>Named subsets of the item's scalar attributes, for bisecting within them.</summary>
    public static IReadOnlyDictionary<string, string[]> ScalarGroups { get; } =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        // Description and rating fields: the ones we leave empty or zero.
        ["text"] = new[]
        {
            "summary", "tagline", "contentRating", "contentRatingAge",
            "audienceRating", "audienceRatingImage", "originallyAvailableAt"
        },
        // Identity and placement: how the item names and locates itself.
        ["identity"] = new[]
        {
            "key", "guid", "type", "title", "titleSort", "slug", "studio", "year",
            "parentKey", "parentGuid", "parentRatingKey", "parentThumb",
            "grandparentKey", "grandparentGuid", "grandparentRatingKey",
            "librarySectionID", "librarySectionTitle", "librarySectionUUID"
        },
        // Artwork.
        ["art"] = new[] { "thumb", "art" },
        // Counters, timing and file facts.
        ["stats"] = new[]
        {
            "addedAt", "updatedAt", "duration", "bitrate", "size", "file",
            "mediaTagVersion", "viewOffset", "viewCount", "lastViewedAt"
        },
    };

    private static void CopySelectedPrimitives(JsonObject target, JsonObject source, IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            if (source.TryGetPropertyValue(key, out var value)) target[key] = value?.DeepClone();
        }
    }

    /// <summary>
    /// Copies scalar (non-object, non-array) attributes of ours onto the official body. Used for
    /// the item's own attributes and the container's, which is the widest untested surface left
    /// once the nested regions have been ruled out.
    /// </summary>
    private static void CopyPrimitives(JsonObject target, JsonObject source)
    {
        foreach (var property in source.ToList())
        {
            if (property.Value is JsonObject or JsonArray) continue;
            target[property.Key] = property.Value?.DeepClone();
        }
    }

    public static async Task<string?> ReadAsync(string stem)
    {
        var path = Path.Combine(CaptureDir, stem + ".body");
        if (!File.Exists(path)) return null;
        return await File.ReadAllTextAsync(path);
    }

    /// <summary>
    /// Builds an official body with the requested regions swapped for ours. Used to find which
    /// region of the generated response the client rejects: a region that can be merged into the
    /// official body without breaking the screen is not the cause.
    /// </summary>
    public static string? Merge(string official, string ours)
    {
        var regions = MergeRegions;
        if (regions.Count == 0) return null;

        JsonNode? officialRoot;
        JsonNode? oursRoot;
        try
        {
            officialRoot = JsonNode.Parse(official);
            oursRoot = JsonNode.Parse(ours);
        }
        catch (JsonException) { return null; }

        if (officialRoot?["MediaContainer"]?["Metadata"] is not JsonArray officialItems) return null;
        if (oursRoot?["MediaContainer"]?["Metadata"] is not JsonArray oursItems) return null;
        if (officialItems.Count == 0 || oursItems.Count == 0) return null;

        var officialItem = officialItems[0]!.AsObject();
        var oursItem = oursItems[0]!.AsObject();

        foreach (var region in regions)
        {
            if (region.Equals("scalars", StringComparison.OrdinalIgnoreCase))
            {
                CopyPrimitives(officialItem, oursItem);
                continue;
            }

            if (ScalarGroups.TryGetValue(region, out var scalarKeys))
            {
                CopySelectedPrimitives(officialItem, oursItem, scalarKeys);
                continue;
            }

            if (region.Equals("container", StringComparison.OrdinalIgnoreCase))
            {
                CopyPrimitives(
                    officialRoot!["MediaContainer"]!.AsObject(),
                    oursRoot!["MediaContainer"]!.AsObject());
                continue;
            }

            if (!Regions.TryGetValue(region, out var keys)) continue;
            foreach (var key in keys)
            {
                if (oursItem.TryGetPropertyValue(key, out var value)) officialItem[key] = value?.DeepClone();
                else officialItem.Remove(key);
            }
        }

        return officialRoot.ToJsonString();
    }
}