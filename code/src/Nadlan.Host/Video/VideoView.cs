using Nadlan.Core.Files;
using Nadlan.Core.Video;

namespace Nadlan.Host.Video;

/// <summary>What the player needs of a converted video: the light versions and the heights as subtitle cues.</summary>
public static class VideoView
{
    /// <summary>
    /// The URL to play: the 1080p MP4 once ready (much lighter than a 36 Mbit/s original), else the file itself. After the original
    /// is deleted the file IS the 1080p, so both are the same.
    /// </summary>
    public static string PlayKey(FileListItem f) => f.VideoStatus == VideoStatuses.Ready && f.VideoLargeKey is not null ? f.VideoLargeKey : f.StorageKey;

    /// <summary>The extra fields of a video in a list (null for anything else): status, the 720p for small screens, heights.</summary>
    public static object? For(FileListItem f, IFileUrlProvider urls, VideoSettings? settings, string? downloadName = null)
    {
        if (settings is null || f.VideoStatus is null)
        {
            return null;
        }

        var ready = f.VideoStatus == VideoStatuses.Ready;
        var cues = VideoRules.HeightCues(f.VideoHeightsJson, f.VideoOffsetM, settings, f.VideoDurationMs);
        return new
        {
            status = f.VideoStatus,
            urlSmall = ready && f.VideoSmallKey is not null ? (downloadName is null ? urls.GetUrl(f.VideoSmallKey) : urls.GetUrl(f.VideoSmallKey, downloadName)) : null,
            heights = cues.Count == 0 ? null : cues,
            heightsOn = settings.HeightsOnByDefault,
        };
    }
}
