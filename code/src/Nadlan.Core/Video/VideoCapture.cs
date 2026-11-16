namespace Nadlan.Core.Video;

// "Catch image" (owner, 2026-11-16): while a video's original still exists, an Admin / Data person pauses on the exact moment and
// the worker cuts that frame from the ORIGINAL at full resolution (4K for 4K drone videos); it becomes a photo of the video's own
// parcel / asset. Our server only records the time and starts the worker (it never opens a video).

public static class VideoCaptureStatuses
{
    public const string Pending = "Pending";
    public const string Running = "Running";
    public const string Done = "Done";
    public const string Failed = "Failed";
}

/// <summary>One requested frame (migration 029, video_capture).</summary>
public sealed record VideoCapture
{
    public long CaptureId { get; init; }
    public long FileAttachmentId { get; init; }
    public int AtMs { get; init; }
    public int FileTypeId { get; init; }
    public long? RequestedByUserId { get; init; }
    public string Status { get; init; } = VideoCaptureStatuses.Pending;
    public string? JobId { get; init; }
    public long? PhotoFileId { get; init; }
    public DateTime CreatedUtc { get; init; }
    public string? ErrorMessage { get; init; }
}

public static class VideoCaptureKeys
{
    /// <summary>One worker run's folder, named by its first capture (several runs for one video never share a result.json).</summary>
    public static string RunPrefix(long fileAttachmentId, long firstCaptureId) =>
        VideoKeys.Prefix(fileAttachmentId) + "captures/c" + firstCaptureId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/";

    /// <summary>The full frame and its 640 px preview, named by the time in ms (as the worker writes them).</summary>
    public static string Full(string runPrefix, int atMs) => runPrefix + atMs.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".jpg";

    public static string Small(string runPrefix, int atMs) => runPrefix + atMs.ToString(System.Globalization.CultureInfo.InvariantCulture) + "_t.jpg";
}

/// <summary>The worker size for catching frames: one frame needs far less than a conversion.</summary>
public static class VideoCaptureWorker
{
    public const int Cpu = 1024;
    public const int MemoryMb = 2048;
    public const int StorageGb = 21;

    /// <summary>Frames per worker run (more wait for the next one).</summary>
    public const int MaxPerRun = 40;
}
