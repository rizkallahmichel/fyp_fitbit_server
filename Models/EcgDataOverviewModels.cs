using System.Text.Json.Serialization;

namespace FitServer.Models;

public sealed class EcgDataOverviewResponse
{
    [JsonPropertyName("collections")]
    public List<EcgCollectionOverview> Collections { get; set; } = new();

    [JsonPropertyName("participants")]
    public List<EcgParticipantOverview> Participants { get; set; } = new();

    [JsonPropertyName("recentSessions")]
    public List<EcgSessionPreview> RecentSessions { get; set; } = new();

    [JsonPropertyName("recentVerificationLogs")]
    public List<EcgVerificationLogPreview> RecentVerificationLogs { get; set; } = new();

    [JsonPropertyName("modelState")]
    public EcgModelStateOverview? ModelState { get; set; }

    [JsonPropertyName("notes")]
    public List<string> Notes { get; set; } = new();
}

public sealed class EcgCollectionOverview
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("documentCount")]
    public int DocumentCount { get; set; }

    [JsonPropertyName("lastUpdatedUtc")]
    public DateTimeOffset? LastUpdatedUtc { get; set; }

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;
}

public sealed class EcgParticipantOverview
{
    [JsonPropertyName("fitbitUserId")]
    public string FitbitUserId { get; set; } = string.Empty;

    [JsonPropertyName("sessionCount")]
    public int SessionCount { get; set; }

    [JsonPropertyName("lastSessionAtUtc")]
    public DateTimeOffset? LastSessionAtUtc { get; set; }
}

public sealed class EcgSessionPreview
{
    [JsonPropertyName("documentId")]
    public string DocumentId { get; set; } = string.Empty;

    [JsonPropertyName("fitbitUserId")]
    public string FitbitUserId { get; set; } = string.Empty;

    [JsonPropertyName("dataSource")]
    public string DataSource { get; set; } = string.Empty;

    [JsonPropertyName("ecgStartTimeUtc")]
    public DateTimeOffset? EcgStartTimeUtc { get; set; }

    [JsonPropertyName("signalQualityScore")]
    public double SignalQualityScore { get; set; }

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = new();
}

public sealed class EcgVerificationLogPreview
{
    [JsonPropertyName("fitbitUserId")]
    public string FitbitUserId { get; set; } = string.Empty;

    [JsonPropertyName("attemptedAtUtc")]
    public DateTimeOffset? AttemptedAtUtc { get; set; }

    [JsonPropertyName("authenticated")]
    public bool Authenticated { get; set; }

    [JsonPropertyName("score")]
    public double Score { get; set; }

    [JsonPropertyName("threshold")]
    public double Threshold { get; set; }

    [JsonPropertyName("confidenceLevel")]
    public double ConfidenceLevel { get; set; }
}

public sealed class EcgModelStateOverview
{
    [JsonPropertyName("lastTrainedUtc")]
    public DateTimeOffset? LastTrainedUtc { get; set; }

    [JsonPropertyName("sessionCount")]
    public int SessionCount { get; set; }

    [JsonPropertyName("sessionCountAtLastTrain")]
    public int SessionCountAtLastTrain { get; set; }

    [JsonPropertyName("retrainPending")]
    public bool RetrainPending { get; set; }

    [JsonPropertyName("retrainReason")]
    public string? RetrainReason { get; set; }

    [JsonPropertyName("lastAccuracy")]
    public double? LastAccuracy { get; set; }

    [JsonPropertyName("lastAreaUnderRocCurve")]
    public double? LastAreaUnderRocCurve { get; set; }

    [JsonPropertyName("lastF1Score")]
    public double? LastF1Score { get; set; }
}
