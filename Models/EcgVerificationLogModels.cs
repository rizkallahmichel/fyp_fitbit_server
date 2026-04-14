using System.Text.Json.Serialization;

namespace FitServer.Models;

public sealed class VerifyRequest
{
    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    [JsonPropertyName("alias")]
    public string? Alias { get; set; }
}

public sealed class UpdateVerificationLabelRequest
{
    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}

public sealed class VerificationLogRecord
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("fitbitUserId")]
    public string FitbitUserId { get; set; } = string.Empty;

    [JsonPropertyName("alias")]
    public string? Alias { get; set; }

    [JsonPropertyName("attemptedAtUtc")]
    public DateTimeOffset? AttemptedAtUtc { get; set; }

    [JsonPropertyName("ecgStartTimeUtc")]
    public DateTimeOffset? EcgStartTimeUtc { get; set; }

    [JsonPropertyName("score")]
    public double Score { get; set; }

    [JsonPropertyName("threshold")]
    public double Threshold { get; set; }

    [JsonPropertyName("authenticated")]
    public bool Authenticated { get; set; }

    [JsonPropertyName("consensusScore")]
    public double ConsensusScore { get; set; }

    [JsonPropertyName("votesPassing")]
    public int VotesPassing { get; set; }

    [JsonPropertyName("comparisonCount")]
    public int ComparisonCount { get; set; }

    [JsonPropertyName("confidenceLevel")]
    public double ConfidenceLevel { get; set; }

    [JsonPropertyName("confidenceDrift")]
    public double ConfidenceDrift { get; set; }

    [JsonPropertyName("confidenceSamples")]
    public int ConfidenceSamples { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}
