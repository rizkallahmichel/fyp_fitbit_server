using MathNet.Numerics.Statistics;

namespace FitServer.Services;

public sealed record EcgFeatures(
    double Mean,
    double Std,
    double Rms,
    double Min,
    double Max,
    double Skewness,
    double Kurtosis,
    double EstimatedBpm,
    double PeakCount);

public interface IEcgFeatureExtractor
{
    EcgFeatures Extract(IReadOnlyList<int> waveformSamples, int scalingFactor, int samplingHz);
}

public sealed class EcgFeatureExtractor : IEcgFeatureExtractor
{
    public EcgFeatures Extract(IReadOnlyList<int> waveformSamples, int scalingFactor, int samplingHz)
    {
        if (waveformSamples == null || waveformSamples.Count == 0)
            throw new ArgumentException("Waveform samples are required.", nameof(waveformSamples));

        if (scalingFactor <= 0)
            throw new ArgumentOutOfRangeException(nameof(scalingFactor), "Scaling factor must be positive.");

        if (samplingHz <= 0)
            throw new ArgumentOutOfRangeException(nameof(samplingHz), "Sampling frequency must be positive.");

        var mv = new double[waveformSamples.Count];
        for (int i = 0; i < waveformSamples.Count; i++)
            mv[i] = waveformSamples[i] / (double)scalingFactor;

        var mean = mv.Mean();
        var std = mv.StandardDeviation();
        var rms = Math.Sqrt(mv.Select(v => v * v).Mean());
        var min = mv.Min();
        var max = mv.Max();
        var skew = mv.Skewness();
        var kurt = mv.Kurtosis();

        var peaks = DetectPeaks(mv, samplingHz);
        var peakCount = peaks.Count;

        var durationSeconds = mv.Length / (double)samplingHz;
        var bpm = durationSeconds > 0 ? (peakCount / durationSeconds) * 60d : 0d;

        return new EcgFeatures(mean, std, rms, min, max, skew, kurt, bpm, peakCount);
    }

    private static List<int> DetectPeaks(double[] values, int samplingHz)
    {
        var detrended = values.Select(v => v - values.Average()).ToArray();
        var abs = detrended.Select(Math.Abs).ToArray();
        var threshold = abs.Average() + 1.5 * abs.StandardDeviation();

        var peaks = new List<int>();
        var refractory = (int)(0.25 * samplingHz);
        var lastPeak = -refractory;

        for (int i = 1; i < abs.Length - 1; i++)
        {
            if (i - lastPeak < refractory)
                continue;

            if (abs[i] > threshold && abs[i] > abs[i - 1] && abs[i] > abs[i + 1])
            {
                peaks.Add(i);
                lastPeak = i;
            }
        }

        return peaks;
    }
}
