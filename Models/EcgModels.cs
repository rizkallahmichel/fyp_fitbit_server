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

    [JsonPropertyName("waveFormSamples")]
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
