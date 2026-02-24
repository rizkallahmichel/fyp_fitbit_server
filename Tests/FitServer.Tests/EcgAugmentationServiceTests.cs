using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using FitServer.Services;
using FitServer.Tests.TestData;

namespace FitServer.Tests;

public class EcgAugmentationServiceTests
{
    [Fact]
    public void Augment_ReturnsRequestedVariantCount()
    {
        var service = new EcgAugmentationService();
        var waveform = TestWaveforms.Baseline;

        var variants = service.Augment(waveform, 250, 4);

        Assert.Equal(4, variants.Count);
        Assert.All(variants, variant => Assert.Equal(waveform.Length, variant.Length));
    }

    [Fact]
    public void Augment_ProducesDeterministicChanges_WhenSeeded()
    {
        var service = new EcgAugmentationService();
        SeedRandom(service, new Random(42));
        var waveform = TestWaveforms.Baseline;

        var variant = service.Augment(waveform, 250, 1).Single();

        Assert.NotEqual(waveform, variant);
        Assert.NotEqual(0, variant[10]);
    }

    private static void SeedRandom(EcgAugmentationService service, Random rng)
    {
        var field = typeof(EcgAugmentationService).GetField("_rngCache", BindingFlags.NonPublic | BindingFlags.Instance);
        var cache = (ConcurrentDictionary<int, Random>?)field?.GetValue(service);
        cache![Environment.CurrentManagedThreadId] = rng;
    }
}
