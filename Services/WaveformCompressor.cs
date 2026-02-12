using System.IO.Compression;
using System.Text;

namespace FitServer.Services;

public static class WaveformCompressor
{
    public static string Compress(IReadOnlyList<int> samples)
    {
        if (samples == null || samples.Count == 0)
            return string.Empty;

        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        using (var writer = new BinaryWriter(gzip, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(samples.Count);
            foreach (var sample in samples)
                writer.Write(sample);
        }

        return Convert.ToBase64String(output.ToArray());
    }

    public static int[]? Decompress(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;

        try
        {
            var buffer = Convert.FromBase64String(payload);
            using var input = new MemoryStream(buffer);
            using var gzip = new GZipStream(input, CompressionMode.Decompress, leaveOpen: true);
            using var reader = new BinaryReader(gzip, Encoding.UTF8, leaveOpen: true);

            var length = reader.ReadInt32();
            var samples = new int[length];
            for (int i = 0; i < length; i++)
                samples[i] = reader.ReadInt32();

            return samples;
        }
        catch
        {
            return null;
        }
    }
}
