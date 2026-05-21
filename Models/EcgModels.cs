using System.Text.Json.Serialization;

namespace FitServer.Models;

public sealed class EcgLogListResponse
{
    [JsonPropertyName("ecgReadings")]
    public List<EcgReading>? EcgReadings { get; set; }

    [JsonPropertyName("pagination")]
    public EcgPagination? Pagination { get; set; }
}

public sealed class EcgPagination
{
    [JsonPropertyName("previous")]
    public string? Previous { get; set; }

    [JsonPropertyName("next")]
    public string? Next { get; set; }
}

public sealed class EcgReading
{
    [JsonPropertyName("startTime")]
    public DateTimeOffset? StartTime { get; set; }

    [JsonPropertyName("averageHeartRate")]
    public int? AverageHeartRate { get; set; }

    [JsonPropertyName("resultClassification")]
    public string? ResultClassification { get; set; }

    [JsonPropertyName("samplingFrequencyHz")]
    public int? SamplingFrequencyHz { get; set; }

    [JsonPropertyName("scalingFactor")]
    public int? ScalingFactor { get; set; }

    [JsonPropertyName("numberOfWaveformSamples")]
    public int? NumberOfWaveformSamples { get; set; }

    [JsonPropertyName("leadNumber")]
    public int? LeadNumber { get; set; }

    [JsonPropertyName("waveformSamples")]
    public List<int>? WaveFormSamples { get; set; }
}

public sealed class FitbitProfileResponse
{
    [JsonPropertyName("user")]
    public FitbitUser? User { get; set; }
}

public sealed class FitbitUser
{
    [JsonPropertyName("encodedId")]
    public string? EncodedId { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }
}

public sealed class CurrentFitbitUserResponse
{
    [JsonPropertyName("fitbitUserId")]
    public string FitbitUserId { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }
}

public sealed class SessionCaptureRequest
{
    [JsonPropertyName("metadata")]
    public SessionMetadata? Metadata { get; set; }

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}

public sealed class SessionMetadata
{
    [JsonPropertyName("activityLabel")]
    public string? ActivityLabel { get; set; }

    [JsonPropertyName("stressLevel")]
    public string? StressLevel { get; set; }

    [JsonPropertyName("sensorPlacement")]
    public string? SensorPlacement { get; set; }

    [JsonPropertyName("deviceModel")]
    public string? DeviceModel { get; set; }
}

public sealed class ContinuousVerifyRequest
{
    [JsonPropertyName("threshold")]
    public double? Threshold { get; set; }

    [JsonPropertyName("windowMinutes")]
    public int WindowMinutes { get; set; } = 15;

    [JsonPropertyName("strideMinutes")]
    public int StrideMinutes { get; set; } = 5;
}

public sealed class ContinuousVerifySample
{
    [JsonPropertyName("windowStartUtc")]
    public DateTimeOffset WindowStartUtc { get; set; }

    [JsonPropertyName("windowEndUtc")]
    public DateTimeOffset WindowEndUtc { get; set; }

    [JsonPropertyName("score")]
    public double Score { get; set; }

    [JsonPropertyName("passes")]
    public bool Passes { get; set; }
}

public sealed class ContinuousVerifyResponse
{
    [JsonPropertyName("authenticated")]
    public bool Authenticated { get; set; }

    [JsonPropertyName("rollingMeanScore")]
    public double RollingMeanScore { get; set; }

    [JsonPropertyName("rollingWorstScore")]
    public double RollingWorstScore { get; set; }

    [JsonPropertyName("samples")]
    public List<ContinuousVerifySample> Samples { get; set; } = new();
}

public sealed class FalseAttemptReportRequest
{
    [JsonPropertyName("fitbitUserId")]
    public string FitbitUserId { get; set; } = string.Empty;

    [JsonPropertyName("ecgStartTime")]
    public DateTimeOffset? EcgStartTime { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}

public sealed class FalseAttemptReportResponse
{
    [JsonPropertyName("fitbitUserId")]
    public string FitbitUserId { get; set; } = string.Empty;

    [JsonPropertyName("ecgStartTime")]
    public DateTimeOffset? EcgStartTime { get; set; }

    [JsonPropertyName("sessionDocumentId")]
    public string SessionDocumentId { get; set; } = string.Empty;

    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags { get; set; } = Array.Empty<string>();

    [JsonPropertyName("retrainRequested")]
    public bool RetrainRequested { get; set; }
}
