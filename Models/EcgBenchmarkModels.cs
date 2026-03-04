using System.Text.Json.Serialization;
using FitServer.Services;

namespace FitServer.Models;

public sealed class EcgBenchmarkRequest
{
    [JsonPropertyName("maxPairsPerUser")]
    public int MaxPairsPerUser { get; set; } = 500;

    [JsonPropertyName("testFraction")]
    public double TestFraction { get; set; } = 0.4;
}

public sealed class EcgBenchmarkResponse
{
    [JsonPropertyName("dataset")]
    public string Dataset { get; init; } = EcgDataSource.EcgId;

    [JsonPropertyName("subjectCount")]
    public int SubjectCount { get; init; }

    [JsonPropertyName("sessionCount")]
    public int SessionCount { get; init; }

    [JsonPropertyName("trainFraction")]
    public double TrainFraction { get; init; }

    [JsonPropertyName("testFraction")]
    public double TestFraction { get; init; }

    [JsonPropertyName("metrics")]
    public ModelTrainingResult Metrics { get; init; } = default!;
}
