using FitServer.Services;
using System.Collections.Generic;
using System.Linq;

namespace FitServer.Tests;

public class WaveformCompressorTests
{
    [Fact]
    public void CompressAndDecompress_RoundTripsSamples()
    {
        var samples = Enumerable.Range(-50, 100)
            .Select(i => i * 3)
            .ToArray();

        var payload = WaveformCompressor.Compress(samples);

        Assert.False(string.IsNullOrWhiteSpace(payload));

        var restored = WaveformCompressor.Decompress(payload);

        Assert.NotNull(restored);
        Assert.Equal(samples, restored);
    }

    [Fact]
    public void Compress_ReturnsEmpty_WhenSamplesNull()
    {
        IReadOnlyList<int>? samples = null;

        var payload = WaveformCompressor.Compress(samples!);

        Assert.Equal(string.Empty, payload);
    }

    [Fact]
    public void Compress_ReturnsEmpty_WhenSamplesEmpty()
    {
        var payload = WaveformCompressor.Compress(Array.Empty<int>());

        Assert.Equal(string.Empty, payload);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Decompress_ReturnsNull_WhenPayloadMissing(string? payload)
    {
        var restored = WaveformCompressor.Decompress(payload);

        Assert.Null(restored);
    }

    [Fact]
    public void Decompress_ReturnsNull_WhenPayloadIsNotBase64()
    {
        var restored = WaveformCompressor.Decompress("not-base64!");

        Assert.Null(restored);
    }
}
