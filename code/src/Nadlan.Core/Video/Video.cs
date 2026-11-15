using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nadlan.Core.Files;

namespace Nadlan.Core.Video;

// Large drone / phone videos (owner, 2026-11-10/11/15; AI/Remarks/Design-Video-2026-11-10.md): a worker - ffmpeg in a container on
// AWS Fargate, started only while a video is converted - makes a 720p and a 1080p MP4 that start at once (index first), plus a frame
// every 2 s from the original: the poster and the photos offered under the video. The 1080p becomes the kept file and the original
// is deleted after a few days. Heights above take-off (from the DJI .srt flight track) show as subtitles. Not MediaConvert: the
// account's AWS plan (projects, "new experience") does not offer it without "advanced features" (owner chose Fargate, 2026-11-15).

public static class VideoStatuses
{
    public const string Queued = "Queued";
    public const string Submitted = "Submitted";
    public const string Ready = "Ready";
    public const string Failed = "Failed";
}

/// <summary>Settings (Nadlan:Video), read at startup.</summary>
public sealed record VideoSettings
{
    /// <summary>Off = nothing is queued or sent; videos play as uploaded.</summary>
    public bool Enabled { get; init; }

    /// <summary>The ECS cluster and task definition of the worker (made once with the CloudShell setup script).</summary>
    public string Cluster { get; init; } = "nadlan-video";

    public string TaskDefinition { get; init; } = "nadlan-video-worker";

    /// <summary>Subnets (comma separated) and security group the worker runs in: the default VPC, outbound only.</summary>
    public string Subnets { get; init; } = "";

    public string SecurityGroup { get; init; } = "";

    /// <summary>Worker size: vCPU units (1024 = 1 vCPU), memory MB, disk GB. ffmpeg uses every core; 4 vCPU / 8 GB / 50 GB by default.</summary>
    public int Cpu { get; init; } = 4096;

    public int MemoryMb { get; init; } = 8192;

    public int StorageGb { get; init; } = 50;

    /// <summary>A worker running longer is stopped by the checker (and the script stops itself a bit before).</summary>
    public int MaxRunMinutes { get; init; } = 90;

    /// <summary>Fargate ARM (Graviton) list prices, EU (Stockholm), USD - for the cost of each run shown to the Admin.</summary>
    public double CostPerVcpuHour { get; init; } = 0.0356;

    public double CostPerGbHour { get; init; } = 0.00392;

    /// <summary>Disk above the 20 GB included, per GB-hour.</summary>
    public double CostPerStorageGbHour { get; init; } = 0.000111;

    /// <summary>Photos offered per video under it (0-30).</summary>
    public int FrameCount { get; init; } = 10;

    public bool HeightsOnByDefault { get; init; } = true;

    public double FloorHeightM { get; init; } = 3.0;

    public double EyeHeightM { get; init; } = 1.6;

    /// <summary>Days the original is kept after a verified conversion.</summary>
    public int OriginalRetentionDays { get; init; } = 7;

    /// <summary>Videos uploaded from this moment on are queued by themselves; older ones by "Optimize" (one or all).</summary>
    public DateTime QueueUploadedAfterUtc { get; init; } = DateTime.MaxValue;

    public const int FrameIntervalMs = 2000;
    public const int MaxCaptures = 900; // 30 minutes of video
}

/// <summary>One video's conversion (migration 028, video_rendition).</summary>
public sealed record VideoRendition
{
    public long FileAttachmentId { get; init; }
    public string Status { get; init; } = VideoStatuses.Queued;
    public string? JobId { get; init; }
    public int Attempts { get; init; }
    public bool WithAudio { get; init; } = true;
    public DateTime QueuedUtc { get; init; }
    public DateTime? SubmittedUtc { get; init; }
    public DateTime? CompletedUtc { get; init; }
    public string? ErrorMessage { get; init; }
    public string? SmallKey { get; init; }
    public long? SmallSize { get; init; }
    public string? LargeKey { get; init; }
    public long? LargeSize { get; init; }
    public int? DurationMs { get; init; }
    public int? FrameCount { get; init; }
    public string? FramesJson { get; init; }
    public int? PosterIndex { get; init; }
    public string? HeightsJson { get; init; }
    public decimal TakeoffOffsetM { get; init; }
    public string? OriginalKey { get; init; }
    public long? OriginalSize { get; init; }
    public string? OriginalMime { get; init; }
    public DateTime? OriginalDeletedUtc { get; init; }
    public DateTime? FramesDeletedUtc { get; init; }

    public IReadOnlyList<int> OfferedFrames => string.IsNullOrEmpty(FramesJson) ? Array.Empty<int>() : JsonSerializer.Deserialize<int[]>(FramesJson) ?? Array.Empty<int>();
}

/// <summary>A video the worker may convert: Ready, a video, on a Parcel or an Asset.</summary>
public sealed record VideoCandidate
{
    public long FileAttachmentId { get; init; }
    public string AttachedToType { get; init; } = "";
    public long AttachedToId { get; init; }
    public string StorageKey { get; init; } = "";
    public string OriginalFileName { get; init; } = "";
    public string MimeType { get; init; } = "";
    public long FileSize { get; init; }
    public bool HasThumbnail { get; init; }
    public DateTime UploadedUtc { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public bool HasTrack { get; init; }
}

public interface IVideoStore
{
    Task<VideoRendition?> GetAsync(long fileAttachmentId, CancellationToken ct = default);

    /// <summary>Ready videos on Parcels / Assets without a conversion row, uploaded after the cutoff (null = any time).</summary>
    Task<IReadOnlyList<VideoCandidate>> ListUnqueuedAsync(DateTime? uploadedAfterUtc, int limit, CancellationToken ct = default);

    Task<VideoCandidate?> GetCandidateAsync(long fileAttachmentId, CancellationToken ct = default);

    /// <summary>Adds a Queued row unless one exists. True if added.</summary>
    Task<bool> QueueAsync(VideoCandidate file, string? heightsJson, CancellationToken ct = default);

    Task<IReadOnlyList<VideoRendition>> ListByStatusAsync(string status, int limit, CancellationToken ct = default);

    Task MarkSubmittedAsync(long fileAttachmentId, string jobId, bool withAudio, CancellationToken ct = default);

    Task MarkFailedAsync(long fileAttachmentId, string message, CancellationToken ct = default);

    /// <summary>One more failed try to start the job (stays Queued); the message is kept for the File details.</summary>
    Task CountAttemptAsync(long fileAttachmentId, string message, CancellationToken ct = default);

    /// <summary>Back to Queued (retry) - without audio when <paramref name="withAudio"/> is false.</summary>
    Task RequeueAsync(long fileAttachmentId, bool withAudio, CancellationToken ct = default);

    Task MarkReadyAsync(long fileAttachmentId, VideoResult result, CancellationToken ct = default);

    Task SetOffsetAsync(long fileAttachmentId, decimal offsetM, CancellationToken ct = default);

    Task SetHeightsAsync(long fileAttachmentId, string? heightsJson, CancellationToken ct = default);

    /// <summary>Ready conversions whose original is still there and which completed before the cutoff.</summary>
    Task<IReadOnlyList<VideoRendition>> ListOriginalsDueAsync(DateTime completedBeforeUtc, int limit, CancellationToken ct = default);

    /// <summary>
    /// The file now points at its 1080p MP4 (size, type, name), only while it still points at the original. True if switched.
    /// </summary>
    Task<bool> SwitchToLargeAsync(long fileAttachmentId, string originalKey, string largeKey, long largeSize, string fileName, CancellationToken ct = default);

    Task MarkOriginalDeletedAsync(long fileAttachmentId, CancellationToken ct = default);

    /// <summary>Records a run once (a known job id is left as it is). No file: a worker the checker found without a record.</summary>
    Task StartRunAsync(string jobId, long? fileAttachmentId, int cpu, int memoryMb, int storageGb, CancellationToken ct = default);

    /// <summary>The run's end, once (a run already ended is left as it is).</summary>
    Task EndRunAsync(string jobId, DateTime? startedUtc, DateTime? stoppedUtc, string status, string stoppedBy, decimal? costUsd, CancellationToken ct = default);

    Task<IReadOnlyList<VideoWorkerRun>> ListRunsAsync(int limit, CancellationToken ct = default);

    /// <summary>Runs started but not ended (the leftover check, and runs whose job vanished).</summary>
    Task<IReadOnlyList<VideoWorkerRun>> ListOpenRunsAsync(CancellationToken ct = default);
}

public sealed record VideoJobSpec(string InputKey, string OutputPrefix, bool WithAudio, int? SourceWidth, int? SourceHeight);

/// <summary>A job's state; StartedUtc / StoppedUtc = what the worker is billed for (from the image pull to its stop).</summary>
public sealed record VideoJobState(string Status, string? ErrorMessage, int? DurationMs, DateTime? StartedUtc = null, DateTime? StoppedUtc = null);

/// <summary>A worker the cloud says is running (the leftover check compares them with the jobs we wait for).</summary>
public sealed record RunningWorker(string JobId, DateTime? StartedUtc);

/// <summary>The worker (Fargate, or a fake in tests). Keys are relative to the storage root folder, like every key in the app.</summary>
public interface IVideoTranscoder
{
    Task<string> SubmitAsync(VideoJobSpec spec, CancellationToken ct = default);

    /// <summary>Status: PROGRESSING / COMPLETE / ERROR.</summary>
    Task<VideoJobState> GetAsync(string jobId, CancellationToken ct = default);

    Task<IReadOnlyList<RunningWorker>> ListRunningAsync(CancellationToken ct = default);

    Task StopAsync(string jobId, string reason, CancellationToken ct = default);
}

/// <summary>One worker run, kept for the Admin: when, how big, how long, what it cost (migration 028, video_worker_run).</summary>
public sealed record VideoWorkerRun
{
    public string JobId { get; init; } = "";
    public long FileAttachmentId { get; init; }
    public string? FileName { get; init; }
    public DateTime SubmittedUtc { get; init; }
    public DateTime? StartedUtc { get; init; }
    public DateTime? StoppedUtc { get; init; }
    public int Cpu { get; init; }
    public int MemoryMb { get; init; }
    public int StorageGb { get; init; }
    public string Status { get; init; } = "";
    public string? StoppedBy { get; init; }
    public decimal? CostUsd { get; init; }
}

public sealed record VideoResult(string SmallKey, long SmallSize, string LargeKey, long LargeSize, int? DurationMs, int FrameCount,
    IReadOnlyList<int> OfferedFrames, int? PosterIndex);

/// <summary>Where a conversion's files live and how they are named (MediaConvert adds the modifiers and frame numbers).</summary>
public static class VideoKeys
{
    public static string Prefix(long fileAttachmentId) => $"_renditions/{fileAttachmentId}/";

    public static string VideoBase(long fileAttachmentId) => Prefix(fileAttachmentId) + "v";

    public static string Small(long fileAttachmentId) => VideoBase(fileAttachmentId) + "720.mp4";

    public static string Large(long fileAttachmentId) => VideoBase(fileAttachmentId) + "1080.mp4";

    public static string FramesBase(long fileAttachmentId) => Prefix(fileAttachmentId) + "frames/f";

    /// <summary>A frame: full size (photos) or the small one (strip, poster).</summary>
    public static string Frame(long fileAttachmentId, int index, bool small) =>
        FramesBase(fileAttachmentId) + (small ? "_t" : "_f") + "." + index.ToString("0000000", CultureInfo.InvariantCulture) + ".jpg";

    private static readonly Regex FramePattern = new(@"_f\.(\d+)\.jpg$", RegexOptions.CultureInvariant);

    /// <summary>The full-size frame numbers among stored keys.</summary>
    public static IReadOnlyList<int> FullFrames(IEnumerable<string> keys) =>
        keys.Select(k => FramePattern.Match(k)).Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).Order().ToList();
}

/// <summary>Pure rules (tested on known inputs).</summary>
public static class VideoRules
{
    /// <summary><paramref name="count"/> frames spread evenly between 5 % and 95 % of the video (all of them when there are fewer).</summary>
    public static IReadOnlyList<int> OfferedFrames(int frameCount, int count)
    {
        if (frameCount <= 0 || count <= 0)
        {
            return Array.Empty<int>();
        }

        if (frameCount <= count)
        {
            return Enumerable.Range(0, frameCount).ToList();
        }

        var first = frameCount * 0.05;
        var last = (frameCount - 1) * 0.95;
        return Enumerable.Range(0, count)
            .Select(i => (int)Math.Round(first + (last - first) * i / Math.Max(1, count - 1)))
            .Select(i => Math.Clamp(i, 0, frameCount - 1)).Distinct().ToList();
    }

    /// <summary>
    /// The poster (Alon: "one photo at the middle of the parcel flight"): the middle of the longest time frame the video has on
    /// its own Parcel (Map drop), else the middle of the video.
    /// </summary>
    public static int PosterFrame(int frameCount, IReadOnlyList<FileSegment>? segmentsOnHome)
    {
        if (frameCount <= 0)
        {
            return 0;
        }

        var longest = segmentsOnHome?.OrderByDescending(s => s.EndMs - s.StartMs).FirstOrDefault();
        var ms = longest is null ? frameCount * VideoSettings.FrameIntervalMs / 2 : (longest.StartMs + longest.EndMs) / 2;
        return Math.Clamp((int)Math.Round(ms / (double)VideoSettings.FrameIntervalMs), 0, frameCount - 1);
    }

    /// <summary>
    /// What a worker run cost: Fargate bills per second from the image pull to the stop, at least one minute, for vCPU, memory and
    /// the disk above the 20 GB included.
    /// </summary>
    public static decimal RunCost(DateTime startedUtc, DateTime stoppedUtc, int cpu, int memoryMb, int storageGb, VideoSettings s)
    {
        var hours = Math.Max(60, (stoppedUtc - startedUtc).TotalSeconds) / 3600.0;
        var perHour = cpu / 1024.0 * s.CostPerVcpuHour + memoryMb / 1024.0 * s.CostPerGbHour + Math.Max(0, storageGb - 20) * s.CostPerStorageGbHour;
        return Math.Round((decimal)(hours * perHour), 5);
    }

    /// <summary>A job that failed because the video has no sound: try again without audio.</summary>
    public static bool IsNoAudioError(string? message) =>
        message is not null && (message.Contains("audio", StringComparison.OrdinalIgnoreCase) || message.Contains("selector", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Heights to keep from a flight track: [ms, metres above take-off] each time the height moved half a metre or more from
    /// the last kept value (track points are 2 a second; GPS noise below that is dropped).
    /// </summary>
    public static string? HeightsFromTrack(string? trackJson)
    {
        if (string.IsNullOrWhiteSpace(trackJson))
        {
            return null;
        }

        var kept = new List<double[]>();
        double? last = null;
        try
        {
            using var doc = JsonDocument.Parse(trackJson);
            foreach (var p in doc.RootElement.EnumerateArray())
            {
                if (p.GetArrayLength() < 4 || p[3].ValueKind != JsonValueKind.Number || p[0].ValueKind != JsonValueKind.Number)
                {
                    continue;
                }

                var ms = p[0].GetDouble();
                var h = p[3].GetDouble();
                if (last is null || Math.Abs(h - last.Value) >= 0.5)
                {
                    kept.Add(new[] { Math.Round(ms), Math.Round(h, 1) });
                    last = h;
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return kept.Count == 0 ? null : JsonSerializer.Serialize(kept);
    }

    /// <summary>
    /// Subtitle cues for the player: [startMs, endMs, "18 m · ≈ 5th floor view"] - a new cue only when the text changes.
    /// Height = above take-off + the video's correction ("take-off was 5 m above the plot").
    /// </summary>
    public static IReadOnlyList<object[]> HeightCues(string? heightsJson, decimal offsetM, VideoSettings s, int? durationMs)
    {
        if (string.IsNullOrWhiteSpace(heightsJson))
        {
            return Array.Empty<object[]>();
        }

        var points = JsonSerializer.Deserialize<double[][]>(heightsJson) ?? Array.Empty<double[]>();
        var cues = new List<(int Start, string Text)>();
        foreach (var p in points.Where(p => p.Length >= 2).OrderBy(p => p[0]))
        {
            var text = HeightLabel(p[1] + (double)offsetM, s);
            if (cues.Count == 0 || cues[^1].Text != text)
            {
                cues.Add(((int)p[0], text));
            }
        }

        var end = Math.Max(durationMs ?? 0, cues.Count == 0 ? 0 : cues[^1].Start + 5000);
        return cues.Select((c, i) => new object[] { c.Start, i + 1 < cues.Count ? cues[i + 1].Start : end, c.Text }).ToList();
    }

    /// <summary>"18 m · ≈ 5th floor view"; ground floor at eye height; above ~40 m only metres; below the plot only metres.</summary>
    public static string HeightLabel(double metres, VideoSettings s)
    {
        var m = (int)Math.Round(metres);
        if (metres < -0.5)
        {
            return (-m).ToString(CultureInfo.InvariantCulture) + " m below take-off"; // flying down a slope, below where it took off
        }

        if (metres > 40)
        {
            return m.ToString(CultureInfo.InvariantCulture) + " m";
        }

        var floor = Math.Max(0, (int)Math.Round((metres - s.EyeHeightM) / Math.Max(0.5, s.FloorHeightM)));
        return m.ToString(CultureInfo.InvariantCulture) + " m · ≈ " + (floor == 0 ? "ground floor" : Ordinal(floor) + " floor") + " view";
    }

    private static string Ordinal(int n) => n + ((n % 100) is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });
}

/// <summary>A DJI .srt flight log read on the server (videos uploaded through the Files dialog, whose .srt was never linked).</summary>
public static class DjiSrt
{
    private static readonly Regex Time = new(@"^(\d{2}):(\d{2}):(\d{2})[,.](\d{3})\s*-->", RegexOptions.CultureInvariant | RegexOptions.Multiline);
    private static readonly Regex Lat = new(@"\[latitude\s*:\s*(-?[\d.]+)\]", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex Lng = new(@"\[long(?:i|t)tude\s*:\s*(-?[\d.]+)\]", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex Rel = new(@"rel_alt\s*:\s*(-?[\d.]+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>Points at most every 500 ms (2 a second), like the page's reader (media-inspect.js).</summary>
    public static IReadOnlyList<TrackPoint> Parse(string text)
    {
        var points = new List<TrackPoint>();
        var lastMs = -1000;
        foreach (var block in text.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var t = Time.Match(block);
            var la = Lat.Match(block);
            var lo = Lng.Match(block);
            if (!t.Success || !la.Success || !lo.Success)
            {
                continue;
            }

            var ms = ((int.Parse(t.Groups[1].Value, CultureInfo.InvariantCulture) * 60 + int.Parse(t.Groups[2].Value, CultureInfo.InvariantCulture)) * 60
                      + int.Parse(t.Groups[3].Value, CultureInfo.InvariantCulture)) * 1000 + int.Parse(t.Groups[4].Value, CultureInfo.InvariantCulture);
            if (ms - lastMs < 500)
            {
                continue;
            }

            var lat = double.Parse(la.Groups[1].Value, CultureInfo.InvariantCulture);
            var lng = double.Parse(lo.Groups[1].Value, CultureInfo.InvariantCulture);
            if (lat == 0 && lng == 0)
            {
                continue;
            }

            var rel = Rel.Match(block);
            points.Add(new TrackPoint(ms, lat, lng, rel.Success ? double.Parse(rel.Groups[1].Value, CultureInfo.InvariantCulture) : null));
            lastMs = ms;
        }

        return points;
    }

    public static string Serialize(IReadOnlyList<TrackPoint> points) =>
        JsonSerializer.Serialize(points.Select(p => new object?[] { p.Ms, Math.Round(p.Lat, 7), Math.Round(p.Lng, 7), p.RelAlt is { } a ? Math.Round(a, 1) : null }));
}
