using System;
using System.Linq;
using FitServer.Models;
using FitServer.Services;

namespace FitServer.Tests.TestData;

internal static class TestDataFactory
{
    private static readonly SessionMetadata DefaultMetadata = new()
    {
        ActivityLabel = "Resting",
        DeviceModel = "Charge 6"
    };

    public static EcgFeatures CreateFeatures(float offset = 0)
    {
        return new EcgFeatures(
            Mean: 0.1 + offset,
            Std: 0.05 + offset,
            Rms: 0.07 + offset,
            Min: -0.2 - offset,
            Max: 0.3 + offset,
            Skewness: 0.01f + offset,
            Kurtosis: 0.02f + offset,
            EstimatedBpm: 64 + offset,
            PeakCount: 35,
            RrMeanMs: 840,
            RrStdMs: 25,
            QrsWidthMs: 92,
            LowFreqPowerRatio: 0.3f,
            MidFreqPowerRatio: 0.4f,
            HighFreqPowerRatio: 0.3f,
            SpectralCentroidHz: 3.1f,
            SpectralEntropy: 0.8f,
            VeryLowFreqPowerRatio: 0.15f,
            SignalQualityScore: 0.92f,
            MotionArtifactIndex: 0.1f,
            BaselineDriftRatio: 0.05f)
        {
            EmbeddingVector = Enumerable.Repeat(0.1f + offset, 16).ToArray()
        };
    }

    public static EcgSessionRecord CreateSessionRecord(string id = "session-1", string userId = "user-123")
    {
        var features = CreateFeatures();
        return new EcgSessionRecord(
            id,
            userId,
            DateTimeOffset.UtcNow,
            72,
            features,
            DefaultMetadata,
            Enumerable.Range(0, 64).ToArray(),
            features.SignalQualityScore,
            features.MotionArtifactIndex,
            features.BaselineDriftRatio,
            250,
            1024,
            new[] { "baseline" },
            "All good");
    }

    public static VerifyResult CreateVerifyResult(bool authenticated = true, double score = 0.91)
    {
        return new VerifyResult(
            "user-123",
            authenticated,
            score,
            0.85,
            DateTimeOffset.UtcNow,
            70,
            new[] { 0.91, 0.88, 0.86 },
            0.88,
            3,
            new ConfidenceSnapshot(
                "user-123",
                5,
                0.88,
                0.03,
                0.87,
                0.05,
                0.92,
                4,
                0,
                DateTimeOffset.UtcNow));
    }

    public static ContinuousVerifyResponse CreateContinuousResponse()
    {
        return new ContinuousVerifyResponse
        {
            Authenticated = true,
            RollingMeanScore = 0.9,
            RollingWorstScore = 0.83,
            Samples =
            {
                new ContinuousVerifySample
                {
                    WindowStartUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
                    WindowEndUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
                    Score = 0.9,
                    Passes = true
                }
            }
        };
    }
}
