using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace FitServer.Services;

public interface IEcgAugmentationService
{
    IReadOnlyList<int[]> Augment(int[] waveformSamples, int samplingHz, int variantCount);
}

public sealed class EcgAugmentationService : IEcgAugmentationService
{
    private readonly ConcurrentDictionary<int, Random> _rngCache = new();

    public IReadOnlyList<int[]> Augment(int[] waveformSamples, int samplingHz, int variantCount)
    {
        if (waveformSamples == null || waveformSamples.Length == 0 || variantCount <= 0)
            return Array.Empty<int[]>();

        var rng = _rngCache.GetOrAdd(Environment.CurrentManagedThreadId, _ => new Random());
        var variants = new List<int[]>(variantCount);
        for (int i = 0; i < variantCount; i++)
            variants.Add(ApplyAugmentations(waveformSamples, samplingHz, rng));

        return variants;
    }

    private static int[] ApplyAugmentations(int[] source, int samplingHz, Random rng)
    {
        var buffer = (int[])source.Clone();
        if (rng.NextDouble() < 0.7)
            AddJitter(buffer, rng);
        if (rng.NextDouble() < 0.5)
            ApplyAmplitudeScale(buffer, rng);
        if (rng.NextDouble() < 0.5)
            ApplyBaselineWander(buffer, samplingHz, rng);
        if (rng.NextDouble() < 0.4)
            buffer = TimeWarp(buffer, rng);
        return buffer;
    }

    private static void AddJitter(int[] buffer, Random rng)
    {
        var max = buffer.Max(b => Math.Abs(b));
        var noiseScale = max == 0 ? 1 : max * 0.02;
        for (int i = 0; i < buffer.Length; i++)
        {
            var noise = (rng.NextDouble() - 0.5) * 2 * noiseScale;
            buffer[i] = (int)Math.Clamp(buffer[i] + noise, int.MinValue, int.MaxValue);
        }
    }

    private static void ApplyAmplitudeScale(int[] buffer, Random rng)
    {
        var scale = 0.85 + rng.NextDouble() * 0.3;
        for (int i = 0; i < buffer.Length; i++)
            buffer[i] = (int)(buffer[i] * scale);
    }

    private static void ApplyBaselineWander(int[] buffer, int samplingHz, Random rng)
    {
        var frequency = 0.1 + rng.NextDouble() * 0.3;
        var amplitude = buffer.Max(b => Math.Abs(b)) * 0.05;
        if (amplitude <= 0)
            amplitude = 50;

        for (int i = 0; i < buffer.Length; i++)
        {
            var t = i / (double)Math.Max(1, samplingHz);
            var drift = Math.Sin(2 * Math.PI * frequency * t) * amplitude;
            buffer[i] = (int)Math.Clamp(buffer[i] + drift, int.MinValue, int.MaxValue);
        }
    }

    private static int[] TimeWarp(int[] buffer, Random rng)
    {
        if (buffer.Length == 0)
            return Array.Empty<int>();
        if (buffer.Length == 1)
            return (int[])buffer.Clone();

        var warp = 0.9 + rng.NextDouble() * 0.2;
        var targetLength = buffer.Length;
        var warped = new int[targetLength];
        var maxIndex = buffer.Length - 1;

        for (int i = 0; i < targetLength; i++)
        {
            var srcIndex = i / warp;
            var clamped = Math.Max(0, Math.Min(srcIndex, maxIndex));
            var lower = (int)Math.Floor(clamped);
            var upper = Math.Min(maxIndex, lower + 1);
            var fraction = clamped - lower;
            var lowerVal = buffer[lower];
            var upperVal = buffer[upper];
            var value = lowerVal * (1 - fraction) + upperVal * fraction;
            warped[i] = (int)value;
        }

        return warped;
    }
}
