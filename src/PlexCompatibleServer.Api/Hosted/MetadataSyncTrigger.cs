using System.Threading.Channels;

namespace PlexCompatibleServer.Api.Hosted;

public enum MetadataSyncReason
{
    ScanCompleted
}

/// <summary>
/// Scan -> metadata hand-off: MediaScanHostedService requests a sync after every successful
/// scan, MetadataSyncHostedService consumes it.
/// </summary>
public sealed class MetadataSyncTrigger
{
    private readonly Channel<MetadataSyncReason> _requests =
        Channel.CreateUnbounded<MetadataSyncReason>(new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<MetadataSyncReason> Requests => _requests.Reader;

    public bool Request(MetadataSyncReason reason) => _requests.Writer.TryWrite(reason);
}
