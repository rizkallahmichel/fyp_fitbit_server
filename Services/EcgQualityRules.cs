using System;

namespace FitServer.Services;

public static class EcgQualityRules
{
    private const double MinSignalQuality = 0.45;
    private const double MaxMotionArtifact = 0.45;
    private const double MaxBaselineDrift = 0.25;

    public static bool IsAcceptable(EcgFeatures features)
    {
        if (features is null)
            return false;

        return features.SignalQualityScore >= MinSignalQuality &&
               features.MotionArtifactIndex <= MaxMotionArtifact &&
               features.BaselineDriftRatio <= MaxBaselineDrift;
    }

    public static void EnsureAcceptable(EcgFeatures features)
    {
        if (!IsAcceptable(features))
        {
            throw new InvalidOperationException(
                $"ECG signal quality is insufficient for reliable authentication. " +
                $"signalQuality={features.SignalQualityScore:F3} (min {MinSignalQuality:F2}), " +
                $"motionArtifact={features.MotionArtifactIndex:F3} (max {MaxMotionArtifact:F2}), " +
                $"baselineDrift={features.BaselineDriftRatio:F3} (max {MaxBaselineDrift:F2}).");
        }
    }
}
