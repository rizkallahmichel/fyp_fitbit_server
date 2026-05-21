using System;
using FitServer.Services;
using FitServer.Tests.Infrastructure;
using FitServer.Tests.TestData;
using Microsoft.Extensions.Logging.Abstractions;

namespace FitServer.Tests;

public class EcgEmbeddingServiceTests
{
    [Fact]
    public void GenerateEmbedding_ReturnsNull_WhenSamplesMissing()
    {
        var environment = new FakeWebHostEnvironment();
        using var service = new EcgEmbeddingService(environment, NullLogger<EcgEmbeddingService>.Instance);

        var result = service.GenerateEmbedding(Array.Empty<int>(), 1024, 250);

        Assert.Null(result);
    }

    [Fact]
    public void GenerateEmbedding_FallbackHasExpectedLength()
    {
        var environment = new FakeWebHostEnvironment();
        using var service = new EcgEmbeddingService(environment, NullLogger<EcgEmbeddingService>.Instance);
        var waveform = TestWaveforms.Baseline;

        var embedding = service.GenerateEmbedding(waveform, 1024, 250);

        Assert.NotNull(embedding);
        Assert.Equal(262, embedding!.Length);
        Assert.NotEqual(0, embedding[0]);
    }
}
