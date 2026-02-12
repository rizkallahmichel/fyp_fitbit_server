using System;

namespace FitServer.Services;

public static class EcgQualityRules
{
    private const double MinSignalQuality = 0.6;
    private const double MaxMotionArtifact = 0.25;
    private const double MaxBaselineDrift = 0.08;

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
            throw new InvalidOperationException("ECG signal quality is insufficient for reliable authentication.");
        }
    }
}
