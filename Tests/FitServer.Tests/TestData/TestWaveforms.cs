using System;
using System.Linq;

namespace FitServer.Tests.TestData;

internal static class TestWaveforms
{
    public static int[] Baseline { get; } = BuildWaveform();

    private static int[] BuildWaveform()
    {
        var length = 256;
        var samples = new int[length];
        for (int i = 0; i < length; i++)
        {
            var radians = i / 12d;
            samples[i] = (int)(Math.Sin(radians) * 1000);
        }
        return samples;
    }
}
