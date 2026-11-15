using Amazon;
using Amazon.MediaConvert;
using Amazon.MediaConvert.Model;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using Nadlan.Core.Files;
using Nadlan.Core.Video;

namespace Nadlan.Storage.S3;

/// <summary>
/// AWS Elemental MediaConvert. One job per video: a 720p and a 1080p MP4 (H.264, quality-based bitrate with a cap, index at the
/// start so playing begins at once), and a frame every 2 s in two sizes (full for photos, 640 px for the strip and the poster).
/// The job runs in <see cref="VideoSettings.Region"/> and reads / writes the bucket where it is.
/// </summary>
public sealed class MediaConvertTranscoder : IVideoTranscoder, IDisposable
{
    private const string Audio = "Audio Selector 1";

    private readonly StorageOptions _storage;
    private readonly VideoSettings _settings;
    private readonly Lazy<IAmazonMediaConvert> _client;

    public MediaConvertTranscoder(StorageOptions storage, VideoSettings settings)
    {
        _storage = storage;
        _settings = settings;
        _client = new Lazy<IAmazonMediaConvert>(CreateClient, LazyThreadSafetyMode.PublicationOnly);
    }

    private string S3(string relativeKey) => $"s3://{_storage.Bucket}/{_storage.FullKey(relativeKey)}";

    public async Task<string> SubmitAsync(VideoJobSpec spec, CancellationToken ct = default)
    {
        var id = spec.OutputPrefix; // _renditions/{file}/
        var input = new Input
        {
            FileInput = S3(spec.InputKey),
            TimecodeSource = InputTimecodeSource.ZEROBASED,
            VideoSelector = new VideoSelector { Rotate = InputRotate.AUTO }, // phone videos filmed upright
        };
        if (spec.WithAudio)
        {
            input.AudioSelectors = new Dictionary<string, AudioSelector> { [Audio] = new() { DefaultSelection = AudioDefaultSelection.DEFAULT } };
        }

        var request = new CreateJobRequest
        {
            Role = _settings.RoleArn,
            UserMetadata = new Dictionary<string, string> { ["app"] = "greekplot", ["key"] = spec.InputKey },
            Settings = new JobSettings
            {
                Inputs = new List<Input> { input },
                OutputGroups = new List<OutputGroup>
                {
                    new()
                    {
                        Name = "MP4",
                        OutputGroupSettings = new OutputGroupSettings
                        {
                            Type = OutputGroupType.FILE_GROUP_SETTINGS,
                            FileGroupSettings = new FileGroupSettings { Destination = S3(id + "v") },
                        },
                        Outputs = new List<Output>
                        {
                            Mp4("720", Size(720, spec), 3_500_000, spec.WithAudio),
                            Mp4("1080", Size(1080, spec), 7_000_000, spec.WithAudio),
                        },
                    },
                    new()
                    {
                        Name = "Frames",
                        OutputGroupSettings = new OutputGroupSettings
                        {
                            Type = OutputGroupType.FILE_GROUP_SETTINGS,
                            FileGroupSettings = new FileGroupSettings { Destination = S3(id + "frames/f") },
                        },
                        Outputs = new List<Output> { Frames("_f", null, 90), Frames("_t", 640, 80) },
                    },
                },
            },
        };

        try
        {
            return (await _client.Value.CreateJobAsync(request, ct)).Job.Id;
        }
        catch (AmazonServiceException ex)
        {
            throw new StorageUnavailableException("MediaConvert refused the job: " + ex.Message, ex);
        }
    }

    public async Task<VideoJobState> GetAsync(string jobId, CancellationToken ct = default)
    {
        var job = (await _client.Value.GetJobAsync(new GetJobRequest { Id = jobId }, ct)).Job;
        var duration = job.OutputGroupDetails?.SelectMany(g => g.OutputDetails ?? new List<OutputDetail>())
            .Select(d => d.DurationInMs).FirstOrDefault(d => d is > 0);
        return new VideoJobState(job.Status?.Value ?? "UNKNOWN", job.ErrorMessage, duration);
    }

    /// <summary>The height to scale to; none (the source size) when the source is not bigger - never blown up.</summary>
    internal static int? Size(int target, VideoJobSpec spec) =>
        spec.SourceWidth is int w && spec.SourceHeight is int h && Math.Min(w, h) <= target ? null : target;

    private static Output Mp4(string name, int? height, int maxBitrate, bool withAudio) => new()
    {
        NameModifier = name,
        ContainerSettings = new ContainerSettings
        {
            Container = ContainerType.MP4,
            Mp4Settings = new Mp4Settings { MoovPlacement = Mp4MoovPlacement.PROGRESSIVE_DOWNLOAD }, // index first: plays at once
        },
        VideoDescription = new VideoDescription
        {
            Height = height,
            ScalingBehavior = ScalingBehavior.DEFAULT,
            CodecSettings = new VideoCodecSettings
            {
                Codec = VideoCodec.H_264, // plays everywhere (phones' HEVC doesn't in every browser)
                H264Settings = new H264Settings
                {
                    RateControlMode = H264RateControlMode.QVBR,
                    QvbrSettings = new H264QvbrSettings { QvbrQualityLevel = 7 },
                    MaxBitrate = maxBitrate,
                    SceneChangeDetect = H264SceneChangeDetect.TRANSITION_DETECTION,
                },
            },
        },
        AudioDescriptions = withAudio
            ? new List<AudioDescription>
            {
                new()
                {
                    AudioSourceName = Audio,
                    CodecSettings = new AudioCodecSettings
                    {
                        Codec = AudioCodec.AAC,
                        AacSettings = new AacSettings { Bitrate = 128000, CodingMode = AacCodingMode.CODING_MODE_2_0, SampleRate = 48000 },
                    },
                },
            }
            : null,
    };

    private static Output Frames(string name, int? width, int quality) => new()
    {
        NameModifier = name,
        ContainerSettings = new ContainerSettings { Container = ContainerType.RAW },
        VideoDescription = new VideoDescription
        {
            Width = width,
            ScalingBehavior = ScalingBehavior.DEFAULT,
            CodecSettings = new VideoCodecSettings
            {
                Codec = VideoCodec.FRAME_CAPTURE,
                FrameCaptureSettings = new FrameCaptureSettings
                {
                    FramerateNumerator = 1,
                    FramerateDenominator = VideoSettings.FrameIntervalMs / 1000, // one frame every 2 s
                    MaxCaptures = VideoSettings.MaxCaptures,
                    Quality = quality,
                },
            },
        },
    };

    private IAmazonMediaConvert CreateClient()
    {
        var region = RegionEndpoint.GetBySystemName(string.IsNullOrWhiteSpace(_settings.Region) ? "eu-central-1" : _settings.Region.Trim());
        if (string.IsNullOrWhiteSpace(_storage.AwsProfile))
        {
            return new AmazonMediaConvertClient(region); // default chain: AWS_PROFILE, env keys, EC2 role
        }

        if (!new CredentialProfileStoreChain().TryGetAWSCredentials(_storage.AwsProfile, out AWSCredentials credentials))
        {
            throw new StorageUnavailableException($"AWS profile '{_storage.AwsProfile}' was not found.");
        }

        return new AmazonMediaConvertClient(credentials, region);
    }

    public void Dispose()
    {
        if (_client.IsValueCreated)
        {
            _client.Value.Dispose();
        }
    }
}
