using FitServer.Services;

namespace FitServer.Tests;

public class EcgFeatureExtractorTests
{
    private readonly EcgFeatureExtractor _extractor = new();

    [Fact]
    public void Extract_ThrowsArgumentException_WhenWaveformSamplesNull()
    {
        var exception = Assert.Throws<ArgumentException>(() => _extractor.Extract(null!, 1000, 250));

        Assert.Equal("waveformSamples", exception.ParamName);
    }

    [Fact]
    public void Extract_ThrowsArgumentException_WhenWaveformSamplesEmpty()
    {
        var exception = Assert.Throws<ArgumentException>(() => _extractor.Extract(Array.Empty<int>(), 1000, 250));

        Assert.Equal("waveformSamples", exception.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public void Extract_ThrowsArgumentOutOfRange_WhenScalingFactorNonPositive(int scalingFactor)
    {
        var samples = new[] { 1, 2, 3 };

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => _extractor.Extract(samples, scalingFactor, 250));

        Assert.Equal("scalingFactor", exception.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Extract_ThrowsArgumentOutOfRange_WhenSamplingFrequencyNonPositive(int samplingHz)
    {
        var samples = new[] { 1, 2, 3 };

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => _extractor.Extract(samples, 1000, samplingHz));

        Assert.Equal("samplingHz", exception.ParamName);
    }
}
