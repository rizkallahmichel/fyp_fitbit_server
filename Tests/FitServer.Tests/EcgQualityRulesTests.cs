using FitServer.Services;

namespace FitServer.Tests;

public class EcgQualityRulesTests
{
    [Fact]
    public void IsAcceptable_ReturnsTrue_WhenAllThresholdsSatisfied()
    {
        var features = CreateFeatures(signalQuality: 0.82, motionArtifact: 0.2, baselineDrift: 0.05);

        var result = EcgQualityRules.IsAcceptable(features);

        Assert.True(result);
    }

    [Fact]
    public void IsAcceptable_ReturnsFalse_WhenSignalQualityTooLow()
    {
        var features = CreateFeatures(signalQuality: 0.4);

        var result = EcgQualityRules.IsAcceptable(features);

        Assert.False(result);
    }

    [Fact]
    public void IsAcceptable_ReturnsFalse_WhenMotionArtifactTooHigh()
    {
        var features = CreateFeatures(motionArtifact: 0.3);

        var result = EcgQualityRules.IsAcceptable(features);

        Assert.False(result);
    }

    [Fact]
    public void IsAcceptable_ReturnsFalse_WhenBaselineDriftTooHigh()
    {
        var features = CreateFeatures(baselineDrift: 0.2);

        var result = EcgQualityRules.IsAcceptable(features);

        Assert.False(result);
    }

    [Fact]
    public void EnsureAcceptable_Throws_WhenFeaturesFailRules()
    {
        var features = CreateFeatures(signalQuality: 0.2, motionArtifact: 0.4, baselineDrift: 0.2);

        var exception = Assert.Throws<InvalidOperationException>(() => EcgQualityRules.EnsureAcceptable(features));

        Assert.Equal("ECG signal quality is insufficient for reliable authentication.", exception.Message);
    }

    private static EcgFeatures CreateFeatures(
        double signalQuality = 0.75,
        double motionArtifact = 0.2,
        double baselineDrift = 0.05)
    {
        return new EcgFeatures(
            Mean: 0.1,
            Std: 0.05,
            Rms: 0.15,
            Min: -0.25,
            Max: 0.4,
            Skewness: 0.1,
            Kurtosis: 3.0,
            EstimatedBpm: 62,
            PeakCount: 15,
            RrMeanMs: 950,
            RrStdMs: 40,
            QrsWidthMs: 95,
            LowFreqPowerRatio: 0.15,
            MidFreqPowerRatio: 0.2,
            HighFreqPowerRatio: 0.35,
            SpectralCentroidHz: 6.5,
            SpectralEntropy: 0.6,
            VeryLowFreqPowerRatio: 0.1,
            SignalQualityScore: signalQuality,
            MotionArtifactIndex: motionArtifact,
            BaselineDriftRatio: baselineDrift);
    }
}
