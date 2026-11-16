using System.Globalization;
using System.Text.Json;
using Amazon;
using Amazon.ECS;
using Amazon.ECS.Model;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using Nadlan.Core.Files;
using Nadlan.Core.Video;
using Task = System.Threading.Tasks.Task;

namespace Nadlan.Storage.S3;

/// <summary>
/// The conversion worker: a plain Alpine Linux container on AWS Fargate (ARM, in the bucket's region) that installs the free tools
/// ffmpeg and aws-cli, runs <see cref="Script"/> on one video, writes the results next to it in S3 and stops. None of our software
/// runs there (owner, 2026-11-15). One task per video; the app starts it (RunTask) and asks how it goes (DescribeTasks).
/// </summary>
public sealed class FargateTranscoder : IVideoTranscoder, IDisposable
{
    public const string StartedBy = "greekplot-video";
    public const string Container = "worker";

    /// <summary>
    /// One ffmpeg pass decodes the original once and writes four things: a 720p and a 1080p H.264 MP4 (the short side scaled to
    /// 720 / 1080, never up; quality-based with a bitrate cap; the index first - plays at once; sound if there is any) and a frame
    /// every 2 s, full size and 640 px wide. result.json is written last: it says the job is complete.
    /// </summary>
    internal const string Script = """
        set -eu
        t0=$(date +%s)
        say() { echo "[$(date -u +%H:%M:%S)] $*"; }
        say "worker: $IN_KEY"
        apk add --no-cache ffmpeg aws-cli >/dev/null
        mkdir -p /work/frames && cd /work
        aws s3 cp "s3://$BUCKET/$IN_KEY" in.bin --only-show-errors
        say "downloaded $(du -h in.bin | cut -f1) in $(( $(date +%s) - t0 )) s"
        DUR=$(ffprobe -v error -show_entries format=duration -of default=nw=1:nk=1 in.bin || true)
        CODEC=$(ffprobe -v error -select_streams v:0 -show_entries stream=codec_name -of default=nw=1:nk=1 in.bin || true)
        S720="scale=w='if(gt(iw,ih),-2,min(720,iw))':h='if(gt(iw,ih),min(720,ih),-2)'"
        S1080="scale=w='if(gt(iw,ih),-2,min(1080,iw))':h='if(gt(iw,ih),min(1080,ih),-2)'"
        X264="-c:v libx264 -preset veryfast -crf 23 -profile:v high -pix_fmt yuv420p -movflags +faststart -c:a aac -b:a 128k -ac 2"
        timeout "$MAX_SECONDS" ffmpeg -hide_banner -loglevel error -y -i in.bin \
          -filter_complex "[0:v]split=4[a][b][c][d];[a]$S720[s];[b]$S1080[l];[c]fps=1/2[f];[d]fps=1/2,scale=640:-2[t]" \
          -map "[s]" -map "0:a:0?" $X264 -maxrate 3500k -bufsize 7000k v720.mp4 \
          -map "[l]" -map "0:a:0?" $X264 -maxrate 7000k -bufsize 14000k v1080.mp4 \
          -map "[f]" -q:v 2 -start_number 0 frames/f_f.%07d.jpg \
          -map "[t]" -q:v 5 -start_number 0 frames/f_t.%07d.jpg
        say "converted in $(( $(date +%s) - t0 )) s: 720p $(du -h v720.mp4 | cut -f1), 1080p $(du -h v1080.mp4 | cut -f1), $(ls frames | wc -l) frame files"
        aws s3 cp v720.mp4 "s3://$BUCKET/${OUT}v720.mp4" --content-type video/mp4 --only-show-errors
        aws s3 cp v1080.mp4 "s3://$BUCKET/${OUT}v1080.mp4" --content-type video/mp4 --only-show-errors
        aws s3 cp frames "s3://$BUCKET/${OUT}frames/" --recursive --content-type image/jpeg --only-show-errors
        MS=$(awk -v d="${DUR:-0}" 'BEGIN { printf "%d", d * 1000 }')
        printf '{"durationMs":%s,"seconds":%s,"codec":"%s"}' "$MS" "$(( $(date +%s) - t0 ))" "$CODEC" > result.json
        aws s3 cp result.json "s3://$BUCKET/${OUT}result.json" --content-type application/json --only-show-errors
        say "done in $(( $(date +%s) - t0 )) s"
        """;

    /// <summary>
    /// "Catch image": the exact frame at each time in AT_MS (ms), full size and 640 px wide, as {ms}.jpg and {ms}_t.jpg. ffmpeg reads
    /// the original over a short-lived signed link and fetches only the part it needs (seeking reads a few MB of a 1 GB file); if
    /// that fails it downloads the original once. A time with no frame (past the end) simply has no file. result.json last.
    /// </summary>
    internal const string CaptureScript = """
        set -eu
        t0=$(date +%s)
        say() { echo "[$(date -u +%H:%M:%S)] $*"; }
        say "catch: $IN_KEY at $AT_MS"
        apk add --no-cache ffmpeg aws-cli >/dev/null
        mkdir -p /work/out && cd /work
        SRC=$(aws s3 presign "s3://$BUCKET/$IN_KEY" --expires-in 3600)
        grab() {
          T=$(awk -v m="$2" 'BEGIN { printf "%.3f", m / 1000 }')
          rm -f "out/$2.jpg" "out/$2_t.jpg"
          timeout 600 ffmpeg -hide_banner -loglevel error -y -ss "$T" -i "$1" \
            -map 0:v:0 -frames:v 1 -q:v 2 "out/$2.jpg" \
            -map 0:v:0 -vf scale=640:-2 -frames:v 1 -q:v 5 "out/$2_t.jpg"
        }
        for MS in $AT_MS; do
          if ! grab "$SRC" "$MS"; then
            if [ ! -f in.bin ]; then
              say "reading over the link failed: downloading the original"
              aws s3 cp "s3://$BUCKET/$IN_KEY" in.bin --only-show-errors
            fi
            SRC=in.bin
            grab in.bin "$MS" || say "no frame at $MS ms"
          fi
          if [ ! -s "out/$MS.jpg" ] || [ ! -s "out/${MS}_t.jpg" ]; then rm -f "out/$MS.jpg" "out/${MS}_t.jpg"; say "no frame at $MS ms"; fi
        done
        say "caught $(ls out | wc -l) files in $(( $(date +%s) - t0 )) s"
        aws s3 cp out "s3://$BUCKET/${OUT}" --recursive --content-type image/jpeg --only-show-errors
        printf '{"seconds":%s}' "$(( $(date +%s) - t0 ))" > result.json
        aws s3 cp result.json "s3://$BUCKET/${OUT}result.json" --content-type application/json --only-show-errors
        say "done in $(( $(date +%s) - t0 )) s"
        """;

    /// <summary>The script as Linux reads it: a Windows checkout may give the source file CRLF line ends, which sh would choke on.</summary>
    internal static string ShellScript => Linux(Script);

    internal static string CaptureShellScript => Linux(CaptureScript);

    private static string Linux(string script) => script.Replace("\r\n", "\n").Replace('\r', '\n');

    private readonly StorageOptions _storage;
    private readonly VideoSettings _settings;
    private readonly IObjectStorage _objects;
    private readonly Lazy<IAmazonECS> _client;

    public FargateTranscoder(StorageOptions storage, VideoSettings settings, IObjectStorage objects)
    {
        _storage = storage;
        _settings = settings;
        _objects = objects;
        _client = new Lazy<IAmazonECS>(CreateClient, LazyThreadSafetyMode.PublicationOnly);
    }

    public Task<string> SubmitAsync(VideoJobSpec spec, CancellationToken ct = default) =>
        RunAsync(ShellScript, _settings.Cpu, _settings.MemoryMb, _settings.StorageGb, spec.InputKey, spec.OutputPrefix,
            ("MAX_SECONDS", (Math.Max(5, _settings.MaxRunMinutes - 5) * 60).ToString(CultureInfo.InvariantCulture)), ct);

    public Task<string> SubmitCaptureAsync(string inputKey, string outputPrefix, IReadOnlyList<int> atMs, CancellationToken ct = default) =>
        RunAsync(CaptureShellScript, VideoCaptureWorker.Cpu, VideoCaptureWorker.MemoryMb, VideoCaptureWorker.StorageGb, inputKey, outputPrefix,
            ("AT_MS", string.Join(" ", atMs.Select(m => m.ToString(CultureInfo.InvariantCulture)))), ct);

    private async Task<string> RunAsync(string script, int cpu, int memoryMb, int storageGb, string inputKey, string outputPrefix,
        (string Name, string Value) extra, CancellationToken ct)
    {
        var request = new RunTaskRequest
        {
            Cluster = _settings.Cluster,
            TaskDefinition = _settings.TaskDefinition,
            LaunchType = LaunchType.FARGATE,
            Count = 1,
            StartedBy = StartedBy,
            NetworkConfiguration = new NetworkConfiguration
            {
                AwsvpcConfiguration = new AwsVpcConfiguration
                {
                    Subnets = _settings.Subnets.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                    SecurityGroups = string.IsNullOrWhiteSpace(_settings.SecurityGroup) ? null : new List<string> { _settings.SecurityGroup.Trim() },
                    AssignPublicIp = AssignPublicIp.ENABLED, // to reach S3 and the public image / package mirrors; nothing comes in
                },
            },
            Overrides = new TaskOverride
            {
                Cpu = cpu.ToString(CultureInfo.InvariantCulture),
                Memory = memoryMb.ToString(CultureInfo.InvariantCulture),
                EphemeralStorage = new EphemeralStorage { SizeInGiB = Math.Clamp(storageGb, 21, 200) },
                ContainerOverrides = new List<ContainerOverride>
                {
                    new()
                    {
                        Name = Container,
                        Command = new List<string> { "sh", "-c", script },
                        Environment = new List<Amazon.ECS.Model.KeyValuePair>
                        {
                            new() { Name = "BUCKET", Value = _storage.Bucket },
                            new() { Name = "IN_KEY", Value = _storage.FullKey(inputKey) },
                            new() { Name = "OUT", Value = _storage.FullKey(outputPrefix) },
                            new() { Name = "OUT_REL", Value = outputPrefix },
                            new() { Name = extra.Name, Value = extra.Value },
                        },
                    },
                },
            },
        };

        try
        {
            var response = await _client.Value.RunTaskAsync(request, ct);
            var task = response.Tasks?.FirstOrDefault();
            if (task?.TaskArn is null)
            {
                var why = response.Failures?.FirstOrDefault();
                throw new StorageUnavailableException($"The worker did not start: {why?.Reason} {why?.Detail}".Trim());
            }

            return task.TaskArn;
        }
        catch (AmazonServiceException ex)
        {
            throw new StorageUnavailableException("AWS refused to start the worker: " + ex.Message, ex);
        }
    }

    public async Task<VideoJobState> GetAsync(string jobId, CancellationToken ct = default)
    {
        var response = await _client.Value.DescribeTasksAsync(new DescribeTasksRequest { Cluster = _settings.Cluster, Tasks = new List<string> { jobId } }, ct);
        var task = response.Tasks?.FirstOrDefault();
        if (task is null)
        {
            return new VideoJobState("ERROR", "The worker is gone (AWS keeps a stopped task for about an hour).", null);
        }

        var started = task.PullStartedAt ?? task.StartedAt ?? task.CreatedAt;
        if (task.LastStatus != "STOPPED")
        {
            return new VideoJobState("PROGRESSING", null, null, started);
        }

        var container = task.Containers?.FirstOrDefault(c => c.Name == Container);
        var outRel = task.Overrides?.ContainerOverrides?.FirstOrDefault()?.Environment?.FirstOrDefault(e => e.Name == "OUT_REL")?.Value;
        if (container?.ExitCode == 0 && outRel is not null)
        {
            var result = await _objects.ReadTextAsync(outRel + "result.json", 4096, ct);
            if (result is not null)
            {
                int? ms = null;
                string? codec = null;
                try
                {
                    using var doc = JsonDocument.Parse(result);
                    ms = doc.RootElement.TryGetProperty("durationMs", out var d) && d.TryGetInt32(out var v) && v > 0 ? v : null;
                    codec = doc.RootElement.TryGetProperty("codec", out var c) && c.ValueKind == JsonValueKind.String && c.GetString() is { Length: > 0 } cs ? cs : null;
                }
                catch (JsonException)
                {
                    // the duration is a nicety
                }

                return new VideoJobState("COMPLETE", null, ms, started, task.StoppedAt, codec);
            }
        }

        var reason = string.Join(" ", new[]
        {
            container?.ExitCode is int code ? $"Exit code {code}." : null,
            container?.Reason,
            task.StoppedReason,
            "Details in CloudWatch Logs /nadlan/video-worker.",
        }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return new VideoJobState("ERROR", reason, null, started, task.StoppedAt);
    }

    public async Task<IReadOnlyList<RunningWorker>> ListRunningAsync(CancellationToken ct = default)
    {
        var arns = new List<string>();
        string? next = null;
        do
        {
            var page = await _client.Value.ListTasksAsync(new ListTasksRequest
            {
                Cluster = _settings.Cluster, StartedBy = StartedBy, DesiredStatus = DesiredStatus.RUNNING, NextToken = next,
            }, ct);
            arns.AddRange(page.TaskArns ?? new List<string>());
            next = page.NextToken;
        }
        while (next is not null && arns.Count < 500);

        var workers = new List<RunningWorker>();
        foreach (var chunk in arns.Chunk(100)) // DescribeTasks takes at most 100
        {
            var tasks = await _client.Value.DescribeTasksAsync(new DescribeTasksRequest { Cluster = _settings.Cluster, Tasks = chunk.ToList() }, ct);
            workers.AddRange((tasks.Tasks ?? new List<Amazon.ECS.Model.Task>()).Select(t => new RunningWorker(t.TaskArn, t.PullStartedAt ?? t.StartedAt ?? t.CreatedAt)));
        }

        return workers;
    }

    public async Task StopAsync(string jobId, string reason, CancellationToken ct = default) =>
        await _client.Value.StopTaskAsync(new StopTaskRequest { Cluster = _settings.Cluster, Task = jobId, Reason = reason.Length <= 255 ? reason : reason[..255] }, ct);

    private IAmazonECS CreateClient()
    {
        var region = RegionEndpoint.GetBySystemName(_storage.Region); // the worker runs where the files are
        if (string.IsNullOrWhiteSpace(_storage.AwsProfile))
        {
            return new AmazonECSClient(region); // default chain: AWS_PROFILE, env keys, EC2 role
        }

        if (!new CredentialProfileStoreChain().TryGetAWSCredentials(_storage.AwsProfile, out AWSCredentials credentials))
        {
            throw new StorageUnavailableException($"AWS profile '{_storage.AwsProfile}' was not found.");
        }

        return new AmazonECSClient(credentials, region);
    }

    public void Dispose()
    {
        if (_client.IsValueCreated)
        {
            _client.Value.Dispose();
        }
    }
}
