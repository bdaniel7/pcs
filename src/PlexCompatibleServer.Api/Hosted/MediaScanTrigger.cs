using System.Threading.Channels;

namespace PlexCompatibleServer.Api.Hosted;

public enum MediaScanReason
{
    Startup,
    Manual
}

public sealed class MediaScanTrigger
{
    private readonly Channel<MediaScanReason> requests =
        Channel.CreateUnbounded<MediaScanReason>(new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<MediaScanReason> Requests => requests.Reader;

    public bool Request(MediaScanReason reason) => requests.Writer.TryWrite(reason);
}
