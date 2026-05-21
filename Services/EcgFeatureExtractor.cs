using MathNet.Numerics.IntegralTransforms;
using MathNet.Numerics.Statistics;
using System.Numerics;

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
    double PeakCount,
    double RrMeanMs,
    double RrStdMs,
    double QrsWidthMs,
    double LowFreqPowerRatio,
    double MidFreqPowerRatio,
    double HighFreqPowerRatio,
    double SpectralCentroidHz,
    double SpectralEntropy,
    double VeryLowFreqPowerRatio,
    double SignalQualityScore,
    double MotionArtifactIndex,
    double BaselineDriftRatio)
{
    public float[]? EmbeddingVector { get; init; }
}

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
        var detrended = mv.Select(v => v - mean).ToArray();

        var peaks = DetectPeaks(mv, samplingHz);
        var peakCount = peaks.Count;

        var durationSeconds = mv.Length / (double)samplingHz;
        var bpm = durationSeconds > 0 ? (peakCount / durationSeconds) * 60d : 0d;

        var (rrMeanMs, rrStdMs) = ComputeRrStats(peaks, samplingHz);
        var qrsDurations = EstimateQrsDurations(mv, peaks, samplingHz);
        var avgQrsMs = qrsDurations.Count > 0 ? qrsDurations.Average() : 0d;
        var spectral = ComputeSpectralFeatures(detrended, samplingHz);
        var baselineDrift = spectral.VeryLowRatio;
        var combinedHigh = spectral.HighRatio + spectral.MidRatio;
        var signalQuality = ComputeSignalQuality(rrMeanMs, rrStdMs, baselineDrift, combinedHigh, avgQrsMs, durationSeconds, peakCount);
        var motionArtifact = ComputeMotionArtifactIndex(baselineDrift, rrStdMs, rrMeanMs);

        return new EcgFeatures(
            mean,
            std,
            rms,
            min,
            max,
            skew,
            kurt,
            bpm,
            peakCount,
            rrMeanMs,
            rrStdMs,
            avgQrsMs,
            spectral.LowRatio,
            spectral.MidRatio,
            spectral.HighRatio,
            spectral.CentroidHz,
            spectral.Entropy,
            spectral.VeryLowRatio,
            signalQuality,
            motionArtifact,
            baselineDrift);
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

    private static (double MeanMs, double StdMs) ComputeRrStats(IReadOnlyList<int> peaks, int samplingHz)
    {
        if (peaks.Count < 2 || samplingHz <= 0)
            return (0d, 0d);

        var intervals = new List<double>(peaks.Count - 1);
        for (int i = 1; i < peaks.Count; i++)
        {
            var diff = peaks[i] - peaks[i - 1];
            if (diff <= 0)
                continue;

            intervals.Add(diff / (double)samplingHz * 1000d);
        }

        if (intervals.Count == 0)
            return (0d, 0d);

        var mean = intervals.Average();
        var std = intervals.Count > 1 ? intervals.StandardDeviation() : 0d;
        return (mean, std);
    }

    private static List<double> EstimateQrsDurations(double[] values, IReadOnlyList<int> peaks, int samplingHz)
    {
        var durations = new List<double>();
        if (peaks.Count == 0 || samplingHz <= 0)
            return durations;

        var maxWidthSamples = Math.Max(1, (int)(0.2 * samplingHz));
        foreach (var peak in peaks)
        {
            if (peak <= 0 || peak >= values.Length)
                continue;

            var peakMagnitude = Math.Abs(values[peak]);
            var threshold = peakMagnitude * 0.5;
            if (threshold <= 0)
                threshold = peakMagnitude * 0.25 + 1e-6;

            var left = peak;
            while (left > 0 && Math.Abs(values[left]) >= threshold && (peak - left) < maxWidthSamples)
                left--;

            var right = peak;
            while (right < values.Length && Math.Abs(values[right]) >= threshold && (right - peak) < maxWidthSamples)
                right++;

            var widthSamples = right - left;
            if (widthSamples > 0)
                durations.Add(widthSamples / (double)samplingHz * 1000d);
        }

        return durations;
    }

    private static SpectralSummary ComputeSpectralFeatures(double[] signal, int samplingHz)
    {
        if (signal.Length == 0 || samplingHz <= 0)
            return new SpectralSummary(0d, 0d, 0d, 0d, 0d, 0d);

        var length = 1;
        while (length < signal.Length)
            length <<= 1;

        var buffer = new Complex[length];
        for (int i = 0; i < signal.Length; i++)
            buffer[i] = new Complex(signal[i], 0);

        Fourier.Forward(buffer, FourierOptions.Matlab);

        var half = length / 2;
        var freqResolution = samplingHz / (double)length;
        var power = new double[half];
        for (int i = 1; i < half; i++)
            power[i] = buffer[i].Magnitude * buffer[i].Magnitude;

        var totalPower = power.Sum();
        if (totalPower <= 0)
            totalPower = 1e-12;

        var veryLow = BandPower(power, freqResolution, 0.01, 0.67);
        var low = BandPower(power, freqResolution, 0.67, 5);
        var mid = BandPower(power, freqResolution, 5, 15);
        var high = BandPower(power, freqResolution, 15, 40);

        var centroidNumerator = 0d;
        for (int i = 1; i < half; i++)
        {
            var freq = i * freqResolution;
            centroidNumerator += freq * power[i];
        }

        var centroid = centroidNumerator / totalPower;
        var entropy = 0d;
        var normalizer = Math.Log(power.Length);
        if (normalizer == 0)
            normalizer = 1;

        for (int i = 1; i < half; i++)
        {
            var p = power[i] / totalPower;
            if (p <= 0)
                continue;
            entropy -= p * Math.Log(p);
        }

        entropy /= normalizer;

        return new SpectralSummary(
            veryLow / totalPower,
            low / totalPower,
            mid / totalPower,
            high / totalPower,
            centroid,
            entropy);
    }

    private static double BandPower(IReadOnlyList<double> power, double freqResolution, double lowHz, double highHz)
    {
        if (power.Count == 0 || freqResolution <= 0 || highHz <= lowHz)
            return 0d;

        var start = Math.Max(1, (int)Math.Floor(lowHz / freqResolution));
        var end = Math.Min(power.Count - 1, (int)Math.Ceiling(highHz / freqResolution));
        var sum = 0d;

        for (int i = start; i <= end; i++)
            sum += power[i];

        return sum;
    }

    private static double ComputeSignalQuality(double rrMeanMs, double rrStdMs, double baselineRatio, double highEnergyRatio, double qrsWidthMs, double durationSeconds, double peakCount)
    {
        var rrStability = rrMeanMs <= 0 ? 0.5 : Clamp01(1 - (rrStdMs / Math.Max(1, rrMeanMs)));
        var baselinePenalty = Clamp01(1 - baselineRatio);
        var highEnergy = Clamp01(highEnergyRatio);
        var qrsScore = qrsWidthMs <= 0 ? 0.5 : Clamp01(1 - Math.Abs(qrsWidthMs - 100) / 120);
        var coverage = durationSeconds <= 0 ? 0.5 : Clamp01((peakCount / durationSeconds) / 3d);

        var score = (0.25 * rrStability) +
                    (0.25 * baselinePenalty) +
                    (0.2 * highEnergy) +
                    (0.15 * qrsScore) +
                    (0.15 * coverage);

        return Clamp01(score);
    }

    private static double ComputeMotionArtifactIndex(double baselineRatio, double rrStdMs, double rrMeanMs)
    {
        var rrInstability = rrMeanMs <= 0 ? 0.5 : Clamp01(rrStdMs / Math.Max(1, rrMeanMs));
        var artifact = Clamp01(0.6 * baselineRatio + 0.4 * rrInstability);
        return artifact;
    }

    private static double Clamp01(double value) => Math.Max(0d, Math.Min(1d, value));

    private readonly record struct SpectralSummary(
        double VeryLowRatio,
        double LowRatio,
        double MidRatio,
        double HighRatio,
        double CentroidHz,
        double Entropy);
}
