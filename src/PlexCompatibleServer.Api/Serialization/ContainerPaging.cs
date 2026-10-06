namespace PlexCompatibleServer.Api.Serialization;

/// <summary>
/// Applies the X-Plex-Container-Start / X-Plex-Container-Size window.
/// </summary>
/// <remarks>
/// Clients page through a listing with these two values, and they rely on the response honouring
/// them. Sending Size=0 is how the client fetches a count - and the library's sort and filter
/// metadata - without transferring a single item, which is exactly what the movie detail screen
/// does for its fallback request. Ignoring the window and returning the whole listing answers a
/// question the client did not ask, with far more data than it can use.
///
/// The values arrive as query parameters in requests captured from the LG client, and as headers
/// in others, so both are consulted.
/// </remarks>
public static class ContainerPaging
{
    /// <summary>Returns the requested window of <paramref name="items"/>.</summary>
    public static List<T> Page<T>(HttpContext http, IReadOnlyList<T> items, out int offset, out int total)
    {
        total = items.Count;
        offset = Read(http, "X-Plex-Container-Start") ?? 0;
        if (offset < 0) offset = 0;
        if (offset > total) offset = total;

        var take = Read(http, "X-Plex-Container-Size");
        if (take is not { } limit) return items.Skip(offset).ToList();

        if (limit < 0) limit = 0;
        if (offset + limit > total) limit = total - offset;
        return items.Skip(offset).Take(limit).ToList();
    }

    private static int? Read(HttpContext http, string name) =>
        int.TryParse(http.Request.Query[name], out var fromQuery) ? fromQuery :
        int.TryParse(http.Request.Headers[name], out var fromHeader) ? fromHeader :
        null;
}