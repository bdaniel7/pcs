using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NUnit.Framework;
using PlexCompatibleServer.Api;
using PlexCompatibleServer.Api.Controllers;
using PlexCompatibleServer.Core.Interfaces;
using PlexCompatibleServer.Core.Models;
using PlexCompatibleServer.Infrastructure.Media;

namespace PlexCompatibleServer.Tests;

/// <summary>
/// The two halves of the subtitle path: what the library publishes for a video with an external
/// .srt next to it, and what the subtitle endpoints answer when the client fetches that track.
/// The failure mode being guarded is a movie that plays with no subtitle at all, or errors out
/// on open, because the track was published with an id the delivery side cannot resolve.
/// </summary>
[TestFixture]
public class SubtitleTrackTests
{
    private const string VIDEO_NAME = "Emily.The.Criminal.2022.1080p.WEBRip.x264.AAC5.1-[YTS.MX].mp4";

    private string dir = "";

    [SetUp]
    public void SetUp()
    {
        dir = Path.Combine(Path.GetTempPath(), "plex-track-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    private MediaItem buildItem(params MediaStreamInfo[] streams)
    {
        var videoPath = Path.Combine(dir, VIDEO_NAME);
        File.WriteAllBytes(videoPath, [0x00]);

        var library = new MediaLibrary { Id = 1, Name = "Movies", Type = LibraryType.Movie };
        var item = new MediaItem
        {
            Id = 5,
            LibraryId = library.Id,
            Library = library,
            Title = Path.GetFileNameWithoutExtension(VIDEO_NAME),
            FilePath = videoPath,
            FileSize = 1,
            Width = 1920,
            Height = 1080
        };
        library.Items.Add(item);

        if (streams.Length > 0) item.StreamsJson = JsonSerializer.Serialize(streams.ToList());
        return item;
    }

    private static MediaStreamInfo videoStream() => new()
    {
        StreamType = 1,
        Codec = "h264",
        Width = 1920,
        Height = 1080,
        Location = "direct"
    };

    private static MediaStreamInfo audioStream() => new()
    {
        StreamType = 2,
        Codec = "aac",
        Channels = 6,
        Location = "direct"
    };

    private static MediaStreamInfo embeddedSubtitle() => new()
    {
        StreamType = 3,
        Codec = "subrip",
        Language = "English",
        LanguageCode = "eng",
        Location = "direct"
    };

    private string sidecar(string suffix)
    {
        var path = Path.Combine(dir, Path.GetFileNameWithoutExtension(VIDEO_NAME) + suffix + ".srt");
        File.WriteAllText(path, "1\n00:00:01,000 --> 00:00:02,000\nhello\n");
        return path;
    }

    [Test]
    public void TaggedSidecarIsPublishedAsItsOwnTrack()
    {
        var item = buildItem(videoStream(), audioStream());
        sidecar(".en");

        var part = VideoMapper.ToVideo(item).Media[0].Parts[0];

        Assert.That(part.Streams, Has.Count.EqualTo(3));

        var track = part.Streams[2];
        Assert.That(track.StreamType, Is.EqualTo(3));
        Assert.That(track.Location, Is.EqualTo("sidecar-subs"));
        Assert.That(track.Id, Is.EqualTo("5002"));
        Assert.That(track.Key, Is.EqualTo("/library/streams/5002"));
        Assert.That(track.Language, Is.EqualTo("English"));
        Assert.That(track.LanguageCode, Is.EqualTo("eng"));
        Assert.That(track.LanguageTag, Is.EqualTo("en"));
        // The codec has to be the spelling real Plex publishes and clients whitelist: an
        // ffprobe-style "subrip" is what leaves the track selectable but never fetched.
        Assert.That(track.Codec, Is.EqualTo("srt"));
        Assert.That(track.Format, Is.EqualTo("srt"));
        Assert.That(track.File, Is.Empty);
    }

    [Test]
    public void EmbeddedAndSidecarTracksAreBothPublished()
    {
        var item = buildItem(videoStream(), audioStream(), embeddedSubtitle());
        sidecar(".en");

        var part = VideoMapper.ToVideo(item).Media[0].Parts[0];

        Assert.That(part.Streams, Has.Count.EqualTo(4));
        // Text inside the container is fetched like an external file: it carries a key at the
        // position its id encodes, and the server extracts it to SRT on demand.
        Assert.That(part.Streams[2].Location, Is.EqualTo("sidecar-subs"));
        Assert.That(part.Streams[2].Key, Is.EqualTo("/library/streams/5002"));
        Assert.That(part.Streams[2].Codec, Is.EqualTo("srt"));
        // The sidecar track follows, at the position its id encodes.
        Assert.That(part.Streams[3].Location, Is.EqualTo("sidecar-subs"));
        Assert.That(part.Streams[3].Key, Is.EqualTo("/library/streams/5003"));
    }

    [Test]
    public void BitmapSubtitleStaysUnfetchableInsideTheContainer()
    {
        var item = buildItem(videoStream(), audioStream(), new MediaStreamInfo
        {
            StreamType = 3,
            Codec = "hdmv_pgs_subtitle",
            Language = "German",
            LanguageCode = "ger",
            Location = "direct"
        });

        var part = VideoMapper.ToVideo(item).Media[0].Parts[0];

        var track = part.Streams[2];
        Assert.That(track.Location, Is.EqualTo("direct"));
        Assert.That(track.Key, Is.Empty);
        Assert.That(track.Codec, Is.EqualTo("hdmv_pgs_subtitle"));
        // A raw code in the menu is still better than dropping the track, and "ger" would be
        // shown as "German" once the language table is consulted.
        Assert.That(track.Language, Is.EqualTo("German"));
    }

    [Test]
    public void SubtitleTracksAreNeverPreSelectedButVideoAndAudioAre()
    {
        var item = buildItem(videoStream(), audioStream(), embeddedSubtitle());
        sidecar(".en");

        var streams = VideoMapper.ToVideo(item).Media[0].Parts[0].Streams;

        Assert.Multiple(() =>
        {
            Assert.That(streams[0].Selected, Is.EqualTo("1"), "video");
            Assert.That(streams[1].Selected, Is.EqualTo("1"), "audio");
            Assert.That(streams[2].Selected, Is.Empty, "embedded subtitle");
            Assert.That(streams[3].Selected, Is.Empty, "sidecar subtitle");
        });
    }

    [Test]
    public void SidecarIsPublishedEvenWhenTheFileWasNeverProbed()
    {
        var item = buildItem();
        sidecar(".en");

        var part = VideoMapper.ToVideo(item).Media[0].Parts[0];

        Assert.That(part.Streams, Has.Count.EqualTo(1));
        Assert.That(part.Streams[0].StreamType, Is.EqualTo(3));
        Assert.That(part.Streams[0].Key, Is.EqualTo("/library/streams/5000"));
    }

    [Test]
    public async Task StreamFile_ServesTheTaggedSidecarThroughItsKey()
    {
        var item = buildItem(videoStream(), audioStream());
        var sub = sidecar(".en");
        var controller = new VideoController(new FakePlayback(item), new StreamSelectionStore());

        var result = await controller.StreamFile(5002, CancellationToken.None) as FileStreamResult;

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.ContentType, Is.EqualTo("text/plain"));
        Assert.That(((FileStream)result.FileStream).Name, Is.EqualTo(sub));
        result.FileStream.Dispose();
    }

    [Test]
    public async Task StreamFileAlt_ResolvesThroughThePartIdEvenForASmallStreamId()
    {
        var item = buildItem(videoStream(), audioStream());
        var sub = sidecar(".en");
        var controller = new VideoController(new FakePlayback(item), new StreamSelectionStore());

        // Clients that address the track by its stream identifier (1, 2, ...) rather than by the
        // id the server minted still have to land on the file.
        var result = await controller.StreamFileAlt(5, 1, CancellationToken.None) as FileStreamResult;

        Assert.That(result, Is.Not.Null);
        Assert.That(((FileStream)result!.FileStream).Name, Is.EqualTo(sub));
        result.FileStream.Dispose();
    }

    [Test]
    public async Task StreamFile_PicksTheRightFileWhenSeveralSidecarsExist()
    {
        var item = buildItem(videoStream(), audioStream());
        var plain = sidecar("");
        var spanish = sidecar(".es");
        var controller = new VideoController(new FakePlayback(item), new StreamSelectionStore());

        var first = await controller.StreamFile(5002, CancellationToken.None) as FileStreamResult;
        var second = await controller.StreamFile(5003, CancellationToken.None) as FileStreamResult;

        Assert.That(((FileStream)first!.FileStream).Name, Is.EqualTo(plain));
        Assert.That(((FileStream)second!.FileStream).Name, Is.EqualTo(spanish));
        first.FileStream.Dispose();
        second!.FileStream.Dispose();
    }

    [Test]
    public async Task EmbeddedSubtitleWithNoSidecar_AnswersWithAnEmptyBodyNotA404()
    {
        var item = buildItem(videoStream(), audioStream(), embeddedSubtitle());
        var controller = new VideoController(new FakePlayback(item), new StreamSelectionStore());

        var result = await controller.StreamFile(5002, CancellationToken.None) as ContentResult;

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Content, Is.EqualTo(""));
        Assert.That(result.ContentType, Is.EqualTo("text/plain"));
    }

    [Test]
    public async Task StreamFile_ForAnUnknownItemIsNotFound()
    {
        var item = buildItem(videoStream(), audioStream());
        sidecar(".en");
        var controller = new VideoController(new FakePlayback(item), new StreamSelectionStore());

        var result = await controller.StreamFile(999002, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<NotFoundResult>());
    }

    [Test]
    public async Task Decision_FlagsTheSubtitleTheClientAskedForAndNoOther()
    {
        var item = buildItem(videoStream(), audioStream(), embeddedSubtitle());
        sidecar(".en");
        var controller = new VideoController(new FakePlayback(item), new StreamSelectionStore())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = await controller.Decision("/library/metadata/5", subtitleStreamId: 5002)
            as ContentResult;

        Assert.That(result, Is.Not.Null);
        var selected = Regex.Matches(result!.Content ?? "", "<Stream\\s[^>]*/>")
            .Cast<Match>()
            .ToDictionary(
                m => Regex.Match(m.Value, "id=\"([^\"]+)\"").Groups[1].Value,
                m => Regex.Match(m.Value, "selected=\"([^\"]*)\"").Groups[1].Value);

        Assert.Multiple(() =>
        {
            Assert.That(selected["5002"], Is.EqualTo("1"), "the track the client chose");
            // The other subtitle must not come back selected: that is a fetch the client never
            // asked for, and a track it cannot load turns into a burn request.
            Assert.That(selected["5003"], Is.EqualTo(""), "the subtitle nobody picked");
            Assert.That(selected["5000"], Is.EqualTo("1"), "video");
            Assert.That(selected["5001"], Is.EqualTo("1"), "audio");
        });
    }

    [Test]
    public async Task SetStreamSelection_RecordsTheChoiceAndPublishesItAsSelected()
    {
        var item = buildItem(videoStream(), audioStream(), embeddedSubtitle());
        sidecar(".en");
        var store = new StreamSelectionStore();
        var controller = new VideoController(new FakePlayback(item), store)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var put = await controller.SetStreamSelection(5, subtitleStreamId: 5003);

        Assert.That(put, Is.InstanceOf<ContentResult>());
        var streams = VideoMapper.ToVideo(item, selections: store).Media[0].Parts[0].Streams;
        Assert.Multiple(() =>
        {
            Assert.That(streams[3].Selected, Is.EqualTo("1"), "the track the viewer picked");
            Assert.That(streams[2].Selected, Is.EqualTo(""), "the subtitle they did not");
            Assert.That(streams[1].Selected, Is.EqualTo("1"), "audio still the default");
        });
    }

    [Test]
    public async Task SetStreamSelection_ZeroClearsTheChosenSubtitle()
    {
        var item = buildItem(videoStream(), audioStream(), embeddedSubtitle());
        sidecar(".en");
        var store = new StreamSelectionStore();
        var controller = new VideoController(new FakePlayback(item), store)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        await controller.SetStreamSelection(5, subtitleStreamId: 5003);
        await controller.SetStreamSelection(5, subtitleStreamId: 0);

        var streams = VideoMapper.ToVideo(item, selections: store).Media[0].Parts[0].Streams;
        Assert.That(streams.Where(s => s.StreamType == 3).Select(s => s.Selected),
            Has.All.EqualTo(""));
    }

    [Test]
    public async Task SetStreamSelection_NamesAStreamThePartDoesNotHave()
    {
        var item = buildItem(videoStream(), audioStream());
        var store = new StreamSelectionStore();
        var controller = new VideoController(new FakePlayback(item), store)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        // 5002 would be a subtitle id, offered as the audio track.
        var put = await controller.SetStreamSelection(5, audioStreamId: 5002) as ContentResult;

        Assert.That(put, Is.Not.Null);
        Assert.That(put!.StatusCode, Is.EqualTo(400));
        Assert.That(store.Get(5).Audio, Is.EqualTo(0), "nothing should have been recorded");
    }

    [Test]
    public async Task SetStreamSelection_ForAnUnknownPartIsNotFound()
    {
        var item = buildItem(videoStream(), audioStream());
        var store = new StreamSelectionStore();
        var controller = new VideoController(new FakePlayback(item), store)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var put = await controller.SetStreamSelection(999001, subtitleStreamId: 1) as ContentResult;

        Assert.That(put, Is.Not.Null);
        Assert.That(put!.StatusCode, Is.EqualTo(404));
    }

    [Test]
    public async Task SubtitleTranscodeStart_ServesTheChosenTrackAsPlainSrt()
    {
        var item = buildItem(videoStream(), audioStream());
        var sub = sidecar(".en");
        var store = new StreamSelectionStore();
        var controller = new VideoController(new FakePlayback(item), store)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        await controller.SetStreamSelection(5, subtitleStreamId: 5002);
        var result = await controller.SubtitleTranscodeStart("/library/metadata/5", CancellationToken.None)
            as FileStreamResult;

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.ContentType, Is.EqualTo("text/plain"));
        Assert.That(((FileStream)result.FileStream).Name, Is.EqualTo(sub));
        result.FileStream.Dispose();
    }

    [Test]
    public async Task SubtitleTranscodeStart_WithNoChoiceMadeAnswersAnEmptyBody()
    {
        var item = buildItem(videoStream(), audioStream());
        sidecar(".en");
        var controller = new VideoController(new FakePlayback(item), new StreamSelectionStore())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = await controller.SubtitleTranscodeStart("/library/metadata/5", CancellationToken.None)
            as ContentResult;

        // The client only calls this once it has picked something, but an unanswered pick must
        // degrade to "no subtitle" rather than an error the playback surfaces.
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Content, Is.EqualTo(""));
        Assert.That(result.ContentType, Is.EqualTo("text/plain"));
    }

    [Test]
    public async Task SubtitleTranscodeStart_ForAnUnknownPathIsNotFound()
    {
        var item = buildItem(videoStream(), audioStream());
        var controller = new VideoController(new FakePlayback(item), new StreamSelectionStore())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = await controller.SubtitleTranscodeStart("/library/metadata/999", CancellationToken.None)
            as ContentResult;

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.StatusCode, Is.EqualTo(404));
    }

    private sealed class FakePlayback(MediaItem item) : IPlaybackService
    {
        public Task<MediaItem?> GetMediaAsync(int id, CancellationToken ct)
            => Task.FromResult<MediaItem?>(id == item.Id ? item : null);
    }
}
