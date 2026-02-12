using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace FitServer.Services;

public interface IEcgEmbeddingService
{
    float[]? GenerateEmbedding(IReadOnlyList<int> waveformSamples, int scalingFactor, int samplingHz);
}

public sealed class EcgEmbeddingService : IEcgEmbeddingService, IDisposable
{
    private readonly ILogger<EcgEmbeddingService> _logger;
    private readonly string _onnxPath;
    private readonly object _sessionLock = new();
    private InferenceSession? _session;

    public EcgEmbeddingService(IWebHostEnvironment environment, ILogger<EcgEmbeddingService> logger)
    {
        _logger = logger;
        _onnxPath = Path.Combine(environment.ContentRootPath, "models", "ecg_encoder.onnx");
    }

    public float[]? GenerateEmbedding(IReadOnlyList<int> waveformSamples, int scalingFactor, int samplingHz)
    {
        if (waveformSamples == null || waveformSamples.Count == 0)
            return null;

        var normalized = NormalizeWaveform(waveformSamples, scalingFactor);

        try
        {
            var session = TryGetSession();
            if (session != null)
                return RunOnnx(session, normalized, samplingHz);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falling back to handcrafted embedding");
        }

        return BuildFallbackEmbedding(normalized, samplingHz);
    }

    private InferenceSession? TryGetSession()
    {
        lock (_sessionLock)
        {
            if (_session != null)
                return _session;

            if (!File.Exists(_onnxPath))
                return null;

            _session = new InferenceSession(_onnxPath);
            return _session;
        }
    }

    private static float[] NormalizeWaveform(IReadOnlyList<int> samples, int scalingFactor)
    {
        var factor = scalingFactor <= 0 ? 1f : scalingFactor;
        var normalized = new float[samples.Count];
        for (int i = 0; i < samples.Count; i++)
            normalized[i] = samples[i] / factor;
        return normalized;
    }

    private static float[]? RunOnnx(InferenceSession session, float[] normalized, int samplingHz)
    {
        var inputName = session.InputMetadata.Keys.FirstOrDefault();
        if (inputName is null)
            return null;

        var metadata = session.InputMetadata[inputName];
        var targetLength = metadata.Dimensions.LastOrDefault();
        if (targetLength <= 0)
            targetLength = normalized.Length;

        var resampled = Resample(normalized, targetLength);
        var tensor = new DenseTensor<float>(new[] { 1, targetLength });
        for (int i = 0; i < targetLength; i++)
            tensor[0, i] = resampled[i];

        var input = NamedOnnxValue.CreateFromTensor(inputName, tensor);
        using var results = session.Run(new[] { input });
        var first = results.FirstOrDefault();
        if (first?.Value is not DenseTensor<float> output)
            return null;

        return output.ToArray();
    }

    private static float[] BuildFallbackEmbedding(float[] normalized, int samplingHz)
    {
        const int targetLength = 128;
        var resampled = Resample(normalized, targetLength);
        var derivative = new float[targetLength];
        for (int i = 1; i < targetLength; i++)
            derivative[i] = resampled[i] - resampled[i - 1];

        var stats = new[]
        {
            ComputeMean(resampled),
            ComputeStd(resampled),
            ComputeMean(derivative),
            ComputeStd(derivative),
            samplingHz,
            normalized.Length
        };

        var vector = new float[targetLength * 2 + stats.Length];
        Array.Copy(resampled, 0, vector, 0, targetLength);
        Array.Copy(derivative, 0, vector, targetLength, targetLength);
        for (int i = 0; i < stats.Length; i++)
            vector[targetLength * 2 + i] = stats[i];

        return vector;
    }

    private static float[] Resample(float[] source, int targetLength)
    {
        if (targetLength <= 0)
            return source.ToArray();

        var result = new float[targetLength];
        var step = (double)source.Length / targetLength;
        for (int i = 0; i < targetLength; i++)
        {
            var start = (int)Math.Floor(i * step);
            var end = (int)Math.Floor((i + 1) * step);
            if (end <= start)
                end = Math.Min(source.Length, start + 1);

            float sum = 0;
            int count = 0;
            for (int j = start; j < end && j < source.Length; j++)
            {
                sum += source[j];
                count++;
            }

            result[i] = count == 0 ? source[Math.Min(source.Length - 1, start)] : sum / count;
        }

        return result;
    }

    private static float ComputeMean(float[] values)
    {
        if (values.Length == 0)
            return 0f;
        float sum = 0;
        for (int i = 0; i < values.Length; i++)
            sum += values[i];
        return sum / values.Length;
    }

    private static float ComputeStd(float[] values)
    {
        if (values.Length == 0)
            return 0f;
        var mean = ComputeMean(values);
        float sum = 0;
        for (int i = 0; i < values.Length; i++)
        {
            var diff = values[i] - mean;
            sum += diff * diff;
        }
        return MathF.Sqrt(sum / values.Length);
    }

    public void Dispose()
    {
        lock (_sessionLock)
        {
            _session?.Dispose();
            _session = null;
        }
    }
}
