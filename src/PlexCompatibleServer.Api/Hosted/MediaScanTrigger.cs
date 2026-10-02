using System.Threading.Channels;

namespace PlexCompatibleServer.Api.Hosted;

public enum MediaScanReason
{
    Startup,
    Manual
}

public sealed class MediaScanTrigger
{
    private readonly Channel<MediaScanReason> _requests =
        Channel.CreateUnbounded<MediaScanReason>(new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<MediaScanReason> Requests => _requests.Reader;

    public bool Request(MediaScanReason reason) => _requests.Writer.TryWrite(reason);
}