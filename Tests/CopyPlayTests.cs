using System;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class CopyPlayTests
{
    private static readonly MediaStream[] H264Aac =
    {
        new() { Type = MediaStreamType.Video, Codec = "h264", Index = 0, Height = 1080 },
        new() { Type = MediaStreamType.Audio, Codec = "aac", Index = 1, Channels = 2 }
    };

    private static readonly MediaStream[] HevcDts =
    {
        new() { Type = MediaStreamType.Video, Codec = "hevc", Index = 0, Height = 2160, Profile = "Main 10" },
        new() { Type = MediaStreamType.Audio, Codec = "dts", Profile = "DTS-HD MA", Index = 1, Channels = 6, IsDefault = true }
    };

    [Theory]
    [InlineData("TranscodeReasons=ContainerNotSupported", true)]
    [InlineData("TranscodeReasons=DirectPlayError", true)]
    [InlineData("TranscodeReasons=ContainerNotSupported,DirectPlayError", true)]
    [InlineData("TranscodeReasons=ContainerNotSupported,VideoCodecTagNotSupported", true)]
    [InlineData("TranscodeReasons=VideoCodecTagNotSupported", true)]
    [InlineData("TranscodeReasons=ContainerNotSupported,VideoCodecTagNotSupported,VideoCodecNotSupported", false)]
    [InlineData("TranscodeReasons=VideoCodecNotSupported", false)]
    [InlineData("TranscodeReasons=ContainerNotSupported,VideoLevelNotSupported", false)]
    [InlineData("TranscodeReasons=ContainerBitrateExceedsLimit", false)]
    [InlineData("TranscodeReasons=SubtitleCodecNotSupported", false)]
    [InlineData("VideoCodec=h264&AudioCodec=aac", false)]
    [InlineData("TranscodeReasons=ContainerNotSupported&AllowVideoStreamCopy=false", false)]
    [InlineData("TranscodeReasons=ContainerNotSupported&allowAudioStreamCopy=false", false)]
    [InlineData("TranscodeReasons=ContainerNotSupported&SubtitleStreamIndex=2&SubtitleMethod=Encode", false)]
    [InlineData("TranscodeReasons=ContainerNotSupported&SubtitleStreamIndex=2&SubtitleMethod=Hls", true)]
    [InlineData("TranscodeReasons=ContainerNotSupported&SubtitleStreamIndex=-1&SubtitleMethod=Encode", true)]
    public void Only_wrapper_reasons_make_a_copy_play(string query, bool fits)
        => Assert.Equal(fits, CopyPlay.Fits(RemoteMediaPath.ParseQuery(query), 8_000_000, 0, H264Aac));

    [Theory]
    [InlineData("TranscodeReasons=ContainerNotSupported,AudioCodecNotSupported", false, true)]
    [InlineData("TranscodeReasons=AudioCodecNotSupported,AudioChannelsNotSupported&AudioCodec=aac", false, true)]
    [InlineData("TranscodeReasons=ContainerNotSupported,AudioCodecNotSupported&AllowAudioStreamCopy=false", false, true)]
    [InlineData("TranscodeReasons=ContainerNotSupported,AudioCodecNotSupported,VideoCodecNotSupported", false, false)]
    [InlineData("TranscodeReasons=AudioCodecNotSupported,VideoLevelNotSupported", false, false)]
    [InlineData("TranscodeReasons=AudioCodecNotSupported&AllowVideoStreamCopy=false", false, false)]
    [InlineData("TranscodeReasons=AudioCodecNotSupported&SubtitleStreamIndex=2&SubtitleMethod=Encode", false, false)]
    public void Sound_only_conversion_accepts_sound_reasons_and_never_a_picture_reason(string query, bool withoutConsent, bool withConsent)
    {
        var parsed = RemoteMediaPath.ParseQuery(query);

        // TrueHD 7.1 only: the "false sound reason" rule cannot save it.
        Assert.Equal(withoutConsent, CopyPlay.Fits(parsed, null, 0, HevcDts));
        Assert.Equal(withConsent, CopyPlay.Fits(parsed, null, 0, HevcDts, soundOnly: true));
    }

    [Fact]
    public void The_owner_rewrite_for_sound_only_copies_the_picture_and_keeps_the_sound_wishes()
    {
        var rewritten = CopyPlay.ForceCopy(RemoteMediaPath.ParseQuery(
            "DeviceId=d&MediaSourceId=abc&VideoCodec=h264&AudioCodec=aac,mp3&VideoBitrate=2000000&MaxWidth=1280&MaxHeight=720&h264-level=41"
            + "&AudioBitrate=384000&TranscodingMaxAudioChannels=6&AllowVideoStreamCopy=false&AllowAudioStreamCopy=false"
            + "&TranscodeReasons=ContainerNotSupported,AudioCodecNotSupported&PlaySessionId=p&api_key=secret"), soundOnly: true);
        var query = RemoteMediaPath.ParseQuery(rewritten);

        // The picture can never be encoded.
        Assert.Equal("copy", RemoteMediaPath.QueryValue(query, "VideoCodec"));
        Assert.Equal("true", RemoteMediaPath.QueryValue(query, "AllowVideoStreamCopy"));
        Assert.Null(RemoteMediaPath.QueryValue(query, "VideoBitrate"));
        Assert.Null(RemoteMediaPath.QueryValue(query, "MaxWidth"));
        Assert.DoesNotContain("h264-level", rewritten);

        // The sound is encoded to what the player plays.
        Assert.Equal("aac,mp3", RemoteMediaPath.QueryValue(query, "AudioCodec"));
        Assert.Equal("384000", RemoteMediaPath.QueryValue(query, "AudioBitrate"));
        Assert.Equal("6", RemoteMediaPath.QueryValue(query, "TranscodingMaxAudioChannels"));
    }

    [Fact]
    public void Sound_only_rewrite_is_a_plain_copy_when_the_sound_needs_no_conversion()
    {
        var rewritten = CopyPlay.ForceCopy(RemoteMediaPath.ParseQuery("AudioCodec=aac&TranscodeReasons=ContainerNotSupported&AudioBitrate=128000"), soundOnly: true);
        var query = RemoteMediaPath.ParseQuery(rewritten);

        Assert.Equal("copy", RemoteMediaPath.QueryValue(query, "VideoCodec"));
        Assert.Equal("copy", RemoteMediaPath.QueryValue(query, "AudioCodec"));
        Assert.Null(RemoteMediaPath.QueryValue(query, "AudioBitrate"));
    }

    [Fact]
    public void The_owner_takes_a_picture_copy_with_sound_encoding_only_with_consent()
    {
        var link = RemoteMediaPath.ParseQuery("VideoCodec=copy&AudioCodec=aac&TranscodeReasons=AudioCodecNotSupported");

        Assert.False(CopyPlay.OwnerAccepts(link, HevcDts));
        Assert.True(CopyPlay.OwnerAccepts(link, HevcDts, soundOnly: true));
        // Never a request that encodes the picture, consent or not.
        Assert.False(CopyPlay.OwnerAccepts(RemoteMediaPath.ParseQuery("VideoCodec=h264&AudioCodec=aac&TranscodeReasons=VideoCodecNotSupported"), HevcDts, soundOnly: true));
    }

    // MediaStream.VideoRangeType is worked out by Jellyfin from the colour and
    // Dolby Vision fields, so a test sets those and checks the result it gets.
    private static MediaStream[] Film(VideoRangeType range)
    {
        var video = new MediaStream { Type = MediaStreamType.Video, Codec = "hevc", Index = 0, Height = 2160, Profile = "Main 10", ColorSpace = "bt2020nc", ColorTransfer = "smpte2084", ColorPrimaries = "bt2020" };
        switch (range)
        {
            case VideoRangeType.DOVIWithEL:
                video.DvProfile = 7; video.DvLevel = 6; video.RpuPresentFlag = 1; video.ElPresentFlag = 1; video.BlPresentFlag = 1; video.DvBlSignalCompatibilityId = 6;
                break;
            case VideoRangeType.DOVIWithHDR10:
                video.DvProfile = 8; video.DvLevel = 6; video.RpuPresentFlag = 1; video.ElPresentFlag = 0; video.BlPresentFlag = 1; video.DvBlSignalCompatibilityId = 1;
                break;
            case VideoRangeType.DOVIWithSDR:
                video.DvProfile = 8; video.DvLevel = 6; video.RpuPresentFlag = 1; video.ElPresentFlag = 0; video.BlPresentFlag = 1; video.DvBlSignalCompatibilityId = 2;
                video.ColorTransfer = "bt709"; video.ColorSpace = "bt709"; video.ColorPrimaries = "bt709";
                break;
            case VideoRangeType.DOVI:
                video.DvProfile = 5; video.DvLevel = 6; video.RpuPresentFlag = 1; video.ElPresentFlag = 0; video.BlPresentFlag = 1; video.DvBlSignalCompatibilityId = 0;
                break;
        }

        Assert.Equal(range, video.VideoRangeType);
        return new[] { video, new MediaStream { Type = MediaStreamType.Audio, Codec = "truehd", Index = 1, Channels = 8, IsDefault = true } };
    }

    [Theory]
    [InlineData(VideoRangeType.DOVIWithEL, true)]
    [InlineData(VideoRangeType.DOVIWithHDR10, true)]
    [InlineData(VideoRangeType.DOVIWithSDR, true)]
    [InlineData(VideoRangeType.DOVI, false)] // profile 5: the base layer looks wrong without Dolby Vision
    public void A_range_reason_passes_only_for_a_Dolby_Vision_picture_with_a_playable_base_layer(VideoRangeType range, bool fits)
    {
        var query = RemoteMediaPath.ParseQuery("TranscodeReasons=ContainerNotSupported,VideoRangeTypeNotSupported");

        Assert.Equal(fits, CopyPlay.Fits(query, null, 0, Film(range)));
    }

    [Fact]
    public void A_range_reason_without_known_tracks_does_not_pass()
        => Assert.False(CopyPlay.Fits(RemoteMediaPath.ParseQuery("TranscodeReasons=VideoRangeTypeNotSupported"), null, 0, null));

    [Fact]
    public void A_range_reason_never_hides_a_picture_codec_reason()
        => Assert.False(CopyPlay.Fits(RemoteMediaPath.ParseQuery("TranscodeReasons=VideoRangeTypeNotSupported,VideoCodecNotSupported"), null, 0, Film(VideoRangeType.DOVIWithEL)));

    [Fact]
    public void A_burned_in_subtitle_is_left_out_only_when_the_admin_allowed_it()
    {
        var query = RemoteMediaPath.ParseQuery("TranscodeReasons=ContainerNotSupported,SubtitleCodecNotSupported&SubtitleStreamIndex=3&SubtitleMethod=Encode");

        Assert.False(CopyPlay.Fits(query, null, 0, HevcDts, soundOnly: false, dropBurnedSubtitle: false, out var keptWithout));
        Assert.False(keptWithout);
        Assert.True(CopyPlay.Fits(query, null, 0, HevcDts, soundOnly: false, dropBurnedSubtitle: true, out var dropped));
        Assert.True(dropped);
    }

    [Fact]
    public void Leaving_the_subtitle_out_does_not_excuse_a_picture_reason()
    {
        var query = RemoteMediaPath.ParseQuery("TranscodeReasons=VideoCodecNotSupported&SubtitleStreamIndex=3&SubtitleMethod=Encode");

        Assert.False(CopyPlay.Fits(query, null, 0, HevcDts, soundOnly: true, dropBurnedSubtitle: true, out _));
    }

    [Fact]
    public void A_play_without_a_burned_subtitle_reports_nothing_dropped()
    {
        var query = RemoteMediaPath.ParseQuery("TranscodeReasons=ContainerNotSupported");

        Assert.True(CopyPlay.Fits(query, null, 0, HevcDts, soundOnly: false, dropBurnedSubtitle: true, out var dropped));
        Assert.False(dropped);
    }

    [Fact]
    public void A_file_over_the_connection_limit_is_not_a_copy_play()
    {
        var query = RemoteMediaPath.ParseQuery("TranscodeReasons=ContainerNotSupported");

        Assert.True(CopyPlay.Fits(query, 68_000_000, 100));
        Assert.False(CopyPlay.Fits(query, 68_000_000, 20));
        Assert.True(CopyPlay.Fits(query, null, 20));
    }

    [Fact]
    public void A_false_sound_reason_is_believed_only_when_the_tracks_copy()
    {
        // Jellyfin reports this for a lone track that is not flagged default.
        var query = RemoteMediaPath.ParseQuery("TranscodeReasons=ContainerNotSupported,AudioCodecNotSupported&AudioCodec=aac,mp3&TranscodingMaxAudioChannels=2");

        Assert.True(CopyPlay.Fits(query, null, 0, H264Aac));
        Assert.False(CopyPlay.Fits(query, null, 0, HevcDts));
        Assert.False(CopyPlay.Fits(query, null, 0, null));

        var surround = new[] { H264Aac[0], new MediaStream { Type = MediaStreamType.Audio, Codec = "aac", Channels = 6 } };
        Assert.False(CopyPlay.Fits(query, null, 0, surround));

        // FLAC rides in MP4 segments, not in TS ones.
        var flac = new[] { H264Aac[0], new MediaStream { Type = MediaStreamType.Audio, Codec = "flac", Channels = 2 } };
        Assert.False(CopyPlay.Fits(RemoteMediaPath.ParseQuery("TranscodeReasons=AudioCodecNotSupported&AudioCodec=flac"), null, 0, flac));
        Assert.True(CopyPlay.Fits(RemoteMediaPath.ParseQuery("TranscodeReasons=AudioCodecNotSupported&AudioCodec=flac&SegmentContainer=mp4"), null, 0, flac));
    }

    [Fact]
    public void The_owner_rewrite_cannot_start_an_encoder()
    {
        var rewritten = CopyPlay.ForceCopy(RemoteMediaPath.ParseQuery(
            "DeviceId=d&MediaSourceId=abc&VideoCodec=h264&AudioCodec=aac,mp3&VideoBitrate=2000000&AudioBitrate=192000&MaxWidth=1280&MaxHeight=720"
            + "&h264-level=41&h264-profile=high&TranscodingMaxAudioChannels=2&SubtitleStreamIndex=3&SubtitleMethod=Encode"
            + "&allowVideoStreamCopy=false&AllowAudioStreamCopy=false&PlaySessionId=p&api_key=secret&SegmentContainer=ts&StartTimeTicks=600000000"
            + "&TranscodeReasons=ContainerNotSupported&AudioStreamIndex=1"));
        var query = RemoteMediaPath.ParseQuery(rewritten);

        Assert.Equal("copy", RemoteMediaPath.QueryValue(query, "VideoCodec"));
        Assert.Equal("copy", RemoteMediaPath.QueryValue(query, "AudioCodec"));
        Assert.Equal("true", RemoteMediaPath.QueryValue(query, "AllowVideoStreamCopy"));
        Assert.Equal("true", RemoteMediaPath.QueryValue(query, "AllowAudioStreamCopy"));
        foreach (var gone in new[] { "VideoBitrate", "AudioBitrate", "MaxWidth", "MaxHeight", "h264-level", "h264-profile", "TranscodingMaxAudioChannels", "SubtitleStreamIndex", "SubtitleMethod", "api_key" })
        {
            Assert.Null(RemoteMediaPath.QueryValue(query, gone));
        }

        // What the play needs is kept.
        Assert.Equal("p", RemoteMediaPath.QueryValue(query, "PlaySessionId"));
        Assert.Equal("abc", RemoteMediaPath.QueryValue(query, "MediaSourceId"));
        Assert.Equal("600000000", RemoteMediaPath.QueryValue(query, "StartTimeTicks"));
        Assert.Equal("1", RemoteMediaPath.QueryValue(query, "AudioStreamIndex"));
        Assert.Equal("ts", RemoteMediaPath.QueryValue(query, "SegmentContainer"));
        Assert.DoesNotContain("secret", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void A_subtitle_that_is_not_burned_in_survives_the_rewrite()
    {
        var query = RemoteMediaPath.ParseQuery(CopyPlay.ForceCopy(RemoteMediaPath.ParseQuery("SubtitleStreamIndex=3&SubtitleMethod=Hls")));

        Assert.Equal("3", RemoteMediaPath.QueryValue(query, "SubtitleStreamIndex"));
        Assert.Equal("Hls", RemoteMediaPath.QueryValue(query, "SubtitleMethod"));
    }

    [Fact]
    public void The_owner_accepts_its_own_rewritten_links_and_refuses_real_conversions()
    {
        Assert.True(CopyPlay.OwnerAccepts(RemoteMediaPath.ParseQuery("VideoCodec=copy&AudioCodec=copy&PlaySessionId=p"), null));
        Assert.True(CopyPlay.OwnerAccepts(RemoteMediaPath.ParseQuery("VideoCodec=h264&AudioCodec=aac&TranscodeReasons=ContainerNotSupported"), H264Aac));
        Assert.False(CopyPlay.OwnerAccepts(RemoteMediaPath.ParseQuery("VideoCodec=h264&AudioCodec=aac&TranscodeReasons=VideoCodecNotSupported"), HevcDts));
        Assert.False(CopyPlay.OwnerAccepts(RemoteMediaPath.ParseQuery("VideoCodec=copy&AudioCodec=aac"), H264Aac));
        Assert.False(CopyPlay.OwnerAccepts(RemoteMediaPath.ParseQuery("VideoCodec=h264&AudioCodec=aac"), H264Aac));
    }

    [Fact]
    public void The_linked_rule_covers_video_work_for_linked_servers_only()
    {
        Assert.True(RemoteMediaPath.TryParse("/Videos/11111111111111111111111111111111/master.m3u8", out var master));
        Assert.True(RemoteMediaPath.TryParse("/Videos/11111111111111111111111111111111/22222222222222222222222222222222/Subtitles/3/0/Stream.vtt", out var subtitle));

        Assert.True(LinkedPlayRule.Applies(new RemoteServer { Linked = true }, master!));
        Assert.False(LinkedPlayRule.Applies(new RemoteServer { Linked = false }, master!));
        Assert.False(LinkedPlayRule.Applies(null, master!));
        Assert.False(LinkedPlayRule.Applies(new RemoteServer { Linked = true }, subtitle!));
    }

    [Fact]
    public void The_refusal_names_what_the_device_could_not_take()
    {
        var source = new FederatedSource { Container = "mkv", Bitrate = 68_172_463, MediaStreams = HevcDts };

        var sound = LinkedPlayRule.Explain("Attic", source, RemoteMediaPath.ParseQuery("TranscodeReasons=AudioCodecNotSupported&AudioStreamIndex=1"));
        Assert.Contains("its sound format (DTS-HD MA)", sound, StringComparison.Ordinal);
        Assert.Contains("stored as mkv / HEVC / DTS", sound, StringComparison.Ordinal);
        Assert.Contains("It is on Attic, a linked server", sound, StringComparison.Ordinal);
        Assert.DoesNotContain("picture", sound, StringComparison.Ordinal);

        var both = LinkedPlayRule.Explain("Attic", source, RemoteMediaPath.ParseQuery("TranscodeReasons=VideoCodecNotSupported,AudioCodecNotSupported"));
        Assert.Contains("its picture format (HEVC) and its sound format (DTS-HD MA)", both, StringComparison.Ordinal);

        var range = LinkedPlayRule.Explain("Attic", source, RemoteMediaPath.ParseQuery("TranscodeReasons=VideoRangeTypeNotSupported"));
        Assert.Contains("its picture (HEVC, Main 10, 2160p)", range, StringComparison.Ordinal);

        var quality = LinkedPlayRule.Explain("Attic", source, RemoteMediaPath.ParseQuery("TranscodeReasons=ContainerBitrateExceedsLimit"));
        Assert.Contains("its full quality (68 Mbps)", quality, StringComparison.Ordinal);
        Assert.Contains("Set the player's quality to Auto or Maximum.", quality, StringComparison.Ordinal);

        var subtitle = LinkedPlayRule.Explain("Attic", source, RemoteMediaPath.ParseQuery("TranscodeReasons=SubtitleCodecNotSupported"));
        Assert.Contains("Turn the subtitle off or pick another one.", subtitle, StringComparison.Ordinal);

        Assert.Contains("cannot play it as stored", LinkedPlayRule.Explain("Attic", null, RemoteMediaPath.ParseQuery(string.Empty)), StringComparison.Ordinal);
    }
}
