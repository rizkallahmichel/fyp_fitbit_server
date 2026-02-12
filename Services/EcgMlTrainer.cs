using System;
using System.Collections.Generic;
using System.Linq;
using Google.Cloud.Firestore;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Trainers;
using Microsoft.ML.Trainers.LightGbm;

namespace FitServer.Services;

public sealed class EcgSession
{
    public string FitbitUserId { get; set; } = string.Empty;
    public DateTimeOffset? CollectedAtUtc { get; set; }
    public double HrvDailyRmssd { get; set; }
    public double Mean { get; set; }
    public double Std { get; set; }
    public double Rms { get; set; }
    public double Min { get; set; }
    public double Max { get; set; }
    public double Skewness { get; set; }
    public double Kurtosis { get; set; }
    public double EstimatedBpm { get; set; }
    public double PeakCount { get; set; }
    public double RrMeanMs { get; set; }
    public double RrStdMs { get; set; }
    public double QrsWidthMs { get; set; }
    public double LowFreqPowerRatio { get; set; }
    public double MidFreqPowerRatio { get; set; }
    public double HighFreqPowerRatio { get; set; }
    public double SpectralCentroidHz { get; set; }
    public double SpectralEntropy { get; set; }
    public double VeryLowFreqPowerRatio { get; set; }
    public double SignalQualityScore { get; set; }
    public double MotionArtifactIndex { get; set; }
    public double BaselineDriftRatio { get; set; }
    public float[]? Embedding { get; set; }
    public int[]? WaveformSamples { get; set; }
    public int SamplingHz { get; set; }
    public int ScalingFactor { get; set; }
}

public sealed class PairRow
{
    [LoadColumn(0)] public bool Label { get; set; }
    [LoadColumn(1)] public float dMean { get; set; }
    [LoadColumn(2)] public float dStd { get; set; }
    [LoadColumn(3)] public float dRms { get; set; }
    [LoadColumn(4)] public float dMin { get; set; }
    [LoadColumn(5)] public float dMax { get; set; }
    [LoadColumn(6)] public float dSkewness { get; set; }
    [LoadColumn(7)] public float dKurtosis { get; set; }
    [LoadColumn(8)] public float dEstimatedBpm { get; set; }
    [LoadColumn(9)] public float dPeakCount { get; set; }
    [LoadColumn(10)] public float dRrMeanMs { get; set; }
    [LoadColumn(11)] public float dRrStdMs { get; set; }
    [LoadColumn(12)] public float dQrsWidthMs { get; set; }
    [LoadColumn(13)] public float dLowFreqPowerRatio { get; set; }
    [LoadColumn(14)] public float dMidFreqPowerRatio { get; set; }
    [LoadColumn(15)] public float dHighFreqPowerRatio { get; set; }
    [LoadColumn(16)] public float dSpectralCentroidHz { get; set; }
    [LoadColumn(17)] public float dSpectralEntropy { get; set; }
    [LoadColumn(18)] public float dHrvDailyRmssd { get; set; }
    [LoadColumn(19)] public float dVeryLowFreqPowerRatio { get; set; }
    [LoadColumn(20)] public float dSignalQualityScore { get; set; }
    [LoadColumn(21)] public float dMotionArtifactIndex { get; set; }
    [LoadColumn(22)] public float dBaselineDriftRatio { get; set; }
    [LoadColumn(23)] public float dEmbeddingL2 { get; set; }
    [LoadColumn(24)] public float dEmbeddingCosine { get; set; }
    [LoadColumn(25)] public float dMeanRelative { get; set; }
    [LoadColumn(26)] public float dStdRelative { get; set; }
    [LoadColumn(27)] public float dRrMeanRatio { get; set; }
    [LoadColumn(28)] public float dRrStdRatio { get; set; }
    [LoadColumn(29)] public float dHighLowBalance { get; set; }
    [LoadColumn(30)] public float dQualityRatio { get; set; }
}

public sealed class PairPrediction
{
    [ColumnName("PredictedLabel")]
    public bool PredictedLabel { get; set; }

    public float Probability { get; set; }
    public float Score { get; set; }
}

public sealed class PairCorrectionInput
{
    public bool Label { get; set; }
    public float Score { get; set; }
    public float Probability { get; set; }
    public float dSignalQualityScore { get; set; }
    public float dMotionArtifactIndex { get; set; }
    public float dBaselineDriftRatio { get; set; }
    public float dEmbeddingL2 { get; set; }
    public float dEmbeddingCosine { get; set; }
}

internal sealed class PairCorrectionRow
{
    public bool Label { get; set; }
    public float Score { get; set; }
    public float Probability { get; set; }
    public float dSignalQualityScore { get; set; }
    public float dMotionArtifactIndex { get; set; }
    public float dBaselineDriftRatio { get; set; }
    public float dEmbeddingL2 { get; set; }
    public float dEmbeddingCosine { get; set; }
}

public sealed record ModelTrainingResult(
    string ModelPath,
    string CorrectionModelPath,
    double Accuracy,
    double AreaUnderRocCurve,
    double F1Score,
    int SessionCount,
    int PairCount);

public interface IEcgMlTrainer
{
    Task<ModelTrainingResult> TrainAndSaveAsync(string modelPath, int maxPairsPerUser, CancellationToken ct = default);
}

public sealed class EcgMlTrainer : IEcgMlTrainer
{
    private readonly FirestoreDb _db;
    private readonly IEcgFeatureExtractor _extractor;
    private readonly IEcgAugmentationService _augmentor;
    private readonly IEcgEmbeddingService _embedding;

    public EcgMlTrainer(
        FirestoreDb db,
        IEcgFeatureExtractor extractor,
        IEcgAugmentationService augmentor,
        IEcgEmbeddingService embedding)
    {
        _db = db;
        _extractor = extractor;
        _augmentor = augmentor;
        _embedding = embedding;
    }

    public async Task<ModelTrainingResult> TrainAndSaveAsync(string modelPath, int maxPairsPerUser, CancellationToken ct = default)
    {
        var sessions = await LoadSessionsAsync(ct);
        if (sessions.Count < 10)
            throw new InvalidOperationException("Collect at least 10 ECG sessions before training.");

        var pairs = BuildPairDataset(sessions, maxPairsPerUser);
        if (pairs.Count < 100)
            throw new InvalidOperationException("Not enough training pairs. Gather more sessions across multiple users.");

        var ml = new MLContext(seed: 42);
        var data = ml.Data.LoadFromEnumerable(pairs);
        var split = ml.Data.TrainTestSplit(data, testFraction: 0.2);

        var featureColumns = new[]
        {
            nameof(PairRow.dMean),
            nameof(PairRow.dStd),
            nameof(PairRow.dRms),
            nameof(PairRow.dMin),
            nameof(PairRow.dMax),
            nameof(PairRow.dSkewness),
            nameof(PairRow.dKurtosis),
            nameof(PairRow.dEstimatedBpm),
            nameof(PairRow.dPeakCount),
            nameof(PairRow.dRrMeanMs),
            nameof(PairRow.dRrStdMs),
            nameof(PairRow.dQrsWidthMs),
            nameof(PairRow.dLowFreqPowerRatio),
            nameof(PairRow.dMidFreqPowerRatio),
            nameof(PairRow.dHighFreqPowerRatio),
            nameof(PairRow.dSpectralCentroidHz),
            nameof(PairRow.dSpectralEntropy),
            nameof(PairRow.dHrvDailyRmssd),
            nameof(PairRow.dVeryLowFreqPowerRatio),
            nameof(PairRow.dSignalQualityScore),
            nameof(PairRow.dMotionArtifactIndex),
            nameof(PairRow.dBaselineDriftRatio),
            nameof(PairRow.dEmbeddingL2),
            nameof(PairRow.dEmbeddingCosine),
            nameof(PairRow.dMeanRelative),
            nameof(PairRow.dStdRelative),
            nameof(PairRow.dRrMeanRatio),
            nameof(PairRow.dRrStdRatio),
            nameof(PairRow.dHighLowBalance),
            nameof(PairRow.dQualityRatio)
        };

        var trainer = ml.BinaryClassification.Trainers.LightGbm(new LightGbmBinaryTrainer.Options
        {
            FeatureColumnName = "Features",
            LabelColumnName = nameof(PairRow.Label),
            NumberOfLeaves = 64,
            NumberOfIterations = 500,
            MinimumExampleCountPerLeaf = 20,
            LearningRate = 0.05,
            UseCategoricalSplit = false
        });

        var pipeline = ml.Transforms.Concatenate("Features", featureColumns)
            .Append(ml.Transforms.NormalizeMinMax("Features"))
            .Append(trainer);

        var model = pipeline.Fit(split.TrainSet);
        var predictions = model.Transform(split.TestSet);
        var metrics = ml.BinaryClassification.Evaluate(predictions);

        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
        ml.Model.Save(model, split.TrainSet.Schema, modelPath);

        var correctionModelPath = BuildCorrectionModelPath(modelPath);
        TrainCorrectionModel(ml, model, split.TrainSet, correctionModelPath);

        return new ModelTrainingResult(
            modelPath,
            correctionModelPath,
            metrics.Accuracy,
            metrics.AreaUnderRocCurve,
            metrics.F1Score,
            sessions.Count,
            pairs.Count);
    }

    private static string BuildCorrectionModelPath(string modelPath)
    {
        var directory = Path.GetDirectoryName(modelPath);
        var fileName = Path.GetFileNameWithoutExtension(modelPath);
        var correctedName = string.IsNullOrWhiteSpace(fileName) ? "ecg_correction_model.zip" : $"{fileName}_correction.zip";
        return string.IsNullOrWhiteSpace(directory)
            ? correctedName
            : Path.Combine(directory, correctedName);
    }

    private static void TrainCorrectionModel(MLContext ml, ITransformer baseModel, IDataView trainingData, string outputPath)
    {
        try
        {
            var scored = baseModel.Transform(trainingData);
            var rows = ml.Data.CreateEnumerable<PairCorrectionRow>(scored, reuseRowObject: false).ToList();
            var correctionInputs = rows
                .Where(r => !float.IsNaN(r.Score))
                .Select(r => new PairCorrectionInput
                {
                    Label = r.Label,
                    Score = r.Score,
                    Probability = r.Probability,
                    dSignalQualityScore = r.dSignalQualityScore,
                    dMotionArtifactIndex = r.dMotionArtifactIndex,
                    dBaselineDriftRatio = r.dBaselineDriftRatio,
                    dEmbeddingL2 = r.dEmbeddingL2,
                    dEmbeddingCosine = r.dEmbeddingCosine
                })
                .ToList();

            if (correctionInputs.Count < 50)
            {
                if (File.Exists(outputPath))
                    File.Delete(outputPath);
                return;
            }

            var correctionData = ml.Data.LoadFromEnumerable(correctionInputs);
            var correctionTrainer = ml.BinaryClassification.Trainers.LbfgsLogisticRegression(
                new LbfgsLogisticRegressionBinaryTrainer.Options
                {
                    LabelColumnName = nameof(PairCorrectionInput.Label),
                    FeatureColumnName = "Features",
                    L2Regularization = 0.01f,
                    MaximumNumberOfIterations = 100
                });

            var correctionPipeline = ml.Transforms.Concatenate("Features",
                    nameof(PairCorrectionInput.Score),
                    nameof(PairCorrectionInput.Probability),
                    nameof(PairCorrectionInput.dSignalQualityScore),
                    nameof(PairCorrectionInput.dMotionArtifactIndex),
                    nameof(PairCorrectionInput.dBaselineDriftRatio),
                    nameof(PairCorrectionInput.dEmbeddingL2),
                    nameof(PairCorrectionInput.dEmbeddingCosine))
                .Append(correctionTrainer);

            var correctionModel = correctionPipeline.Fit(correctionData);
            ml.Model.Save(correctionModel, correctionData.Schema, outputPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Correction model training skipped: {ex.Message}");
            if (File.Exists(outputPath))
                File.Delete(outputPath);
        }
    }

    private async Task<List<EcgSession>> LoadSessionsAsync(CancellationToken ct)
    {
        var snapshot = await _db.Collection("ecg_sessions").GetSnapshotAsync(ct);
        var sessions = new List<EcgSession>();

        foreach (var doc in snapshot.Documents)
        {
            var payload = doc.ToDictionary();
            if (!payload.TryGetValue("fitbitUserId", out var userObj) || userObj is null)
                continue;

            if (!payload.TryGetValue("ecgFeatures", out var featuresObj) || featuresObj is not Dictionary<string, object> featureDict)
                continue;

            var features = ParseFeatures(featureDict);
            if (!EcgQualityRules.IsAcceptable(features))
                continue;
            var waveform = WaveformCompressor.Decompress(payload.TryGetValue("waveformBlob", out var blobObj) ? blobObj?.ToString() : null);
            var samplingHz = payload.TryGetValue("samplingFrequencyHz", out var samplingObj) ? Convert.ToInt32(samplingObj) : 0;
            var scalingFactor = payload.TryGetValue("scalingFactor", out var scalingObj) ? Convert.ToInt32(scalingObj) : 0;

            sessions.Add(ToSession(
                userObj.ToString() ?? string.Empty,
                payload.TryGetValue("hrvDailyRmssd", out var hrv) ? Convert.ToDouble(hrv) : 0d,
                TryParseDateTimeOffset(payload.TryGetValue("collectedAtUtc", out var collectedAtObj) ? collectedAtObj : null),
                features,
                waveform,
                samplingHz,
                scalingFactor));
        }

        return AugmentSessions(sessions);
    }

    private static EcgFeatures ParseFeatures(Dictionary<string, object> dict)
    {
        double Get(string key) => dict.TryGetValue(key, out var val) ? Convert.ToDouble(val) : 0d;
        float[]? embedding = null;
        if (dict.TryGetValue("EmbeddingVector", out var embeddingObj) && embeddingObj is IEnumerable<object> points)
        {
            var list = new List<float>();
            foreach (var point in points)
                list.Add(Convert.ToSingle(point));
            embedding = list.ToArray();
        }

        var features = new EcgFeatures(
            Get("Mean"),
            Get("Std"),
            Get("Rms"),
            Get("Min"),
            Get("Max"),
            Get("Skewness"),
            Get("Kurtosis"),
            Get("EstimatedBpm"),
            Get("PeakCount"),
            Get("RrMeanMs"),
            Get("RrStdMs"),
            Get("QrsWidthMs"),
            Get("LowFreqPowerRatio"),
            Get("MidFreqPowerRatio"),
            Get("HighFreqPowerRatio"),
            Get("SpectralCentroidHz"),
            Get("SpectralEntropy"),
            Get("VeryLowFreqPowerRatio"),
            Get("SignalQualityScore"),
            Get("MotionArtifactIndex"),
            Get("BaselineDriftRatio"));

        return embedding is null ? features : features with { EmbeddingVector = embedding };
    }

    private static EcgSession ToSession(
        string userId,
        double hrv,
        DateTimeOffset? collectedAt,
        EcgFeatures features,
        int[]? waveform,
        int samplingHz,
        int scalingFactor)
    {
        return new EcgSession
        {
            FitbitUserId = userId,
            HrvDailyRmssd = hrv,
            CollectedAtUtc = collectedAt,
            Mean = features.Mean,
            Std = features.Std,
            Rms = features.Rms,
            Min = features.Min,
            Max = features.Max,
            Skewness = features.Skewness,
            Kurtosis = features.Kurtosis,
            EstimatedBpm = features.EstimatedBpm,
            PeakCount = features.PeakCount,
            RrMeanMs = features.RrMeanMs,
            RrStdMs = features.RrStdMs,
            QrsWidthMs = features.QrsWidthMs,
            LowFreqPowerRatio = features.LowFreqPowerRatio,
            MidFreqPowerRatio = features.MidFreqPowerRatio,
            HighFreqPowerRatio = features.HighFreqPowerRatio,
            SpectralCentroidHz = features.SpectralCentroidHz,
            SpectralEntropy = features.SpectralEntropy,
            VeryLowFreqPowerRatio = features.VeryLowFreqPowerRatio,
            SignalQualityScore = features.SignalQualityScore,
            MotionArtifactIndex = features.MotionArtifactIndex,
            BaselineDriftRatio = features.BaselineDriftRatio,
            Embedding = features.EmbeddingVector,
            WaveformSamples = waveform,
            SamplingHz = samplingHz,
            ScalingFactor = scalingFactor
        };
    }

    private List<EcgSession> AugmentSessions(List<EcgSession> baseSessions)
    {
        var augmented = new List<EcgSession>(baseSessions);
        foreach (var session in baseSessions)
        {
            if (session.WaveformSamples is null || session.WaveformSamples.Length == 0)
                continue;

            var sampling = session.SamplingHz > 0 ? session.SamplingHz : 250;
            var scaling = session.ScalingFactor > 0 ? session.ScalingFactor : 10922;
            var variantTarget = session.WaveformSamples.Length >= 2000 ? 2 : 1;
            var variants = _augmentor.Augment(session.WaveformSamples, sampling, variantTarget);

            foreach (var variant in variants)
            {
                var features = _extractor.Extract(variant, scaling, sampling);
                var embedding = _embedding.GenerateEmbedding(variant, scaling, sampling);
                if (embedding is { Length: > 0 })
                    features = features with { EmbeddingVector = embedding };

                if (!EcgQualityRules.IsAcceptable(features))
                    continue;

                augmented.Add(ToSession(session.FitbitUserId, session.HrvDailyRmssd, session.CollectedAtUtc, features, variant, sampling, scaling));
            }
        }

        return augmented;
    }

    private static DateTimeOffset? TryParseDateTimeOffset(object? value)
    {
        return value switch
        {
            null => null,
            Timestamp ts => ts.ToDateTime(),
            DateTimeOffset dto => dto,
            DateTime dt => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
            _ => null
        };
    }
    private static List<PairRow> BuildPairDataset(IReadOnlyList<EcgSession> sessions, int maxPairsPerUser)
    {
        var grouped = sessions
            .GroupBy(s => s.FitbitUserId)
            .Where(g => g.Count() >= 2)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.CollectedAtUtc ?? DateTimeOffset.MinValue).ToList());

        if (grouped.Count < 2)
            throw new InvalidOperationException("Training requires sessions from at least two users.");

        var rng = new Random(42);
        var rows = new List<PairRow>();

        foreach (var (userId, sameUserSessions) in grouped)
        {
            if (sameUserSessions.Count < 5)
                continue;

            var distinctPairs = GenerateDistinctPairs(sameUserSessions);
            Shuffle(distinctPairs, rng);

            var positiveTarget = Math.Min(Math.Max(10, maxPairsPerUser), distinctPairs.Count);
            for (int i = 0; i < positiveTarget; i++)
            {
                var (a, b) = distinctPairs[i];
                rows.Add(MakePair(a, b, true));
            }

            var negatives = GenerateNegativePairs(userId, sameUserSessions, grouped, positiveTarget, rng);
            rows.AddRange(negatives);
        }

        return rows;
    }

    private static List<(EcgSession A, EcgSession B)> GenerateDistinctPairs(IReadOnlyList<EcgSession> sessions)
    {
        var pairs = new List<(EcgSession, EcgSession)>();
        for (int i = 0; i < sessions.Count - 1; i++)
        {
            for (int j = i + 1; j < sessions.Count; j++)
            {
                pairs.Add((sessions[i], sessions[j]));
            }
        }

        return pairs;
    }

    private static IEnumerable<PairRow> GenerateNegativePairs(
        string anchorUser,
        IReadOnlyList<EcgSession> anchorSessions,
        IReadOnlyDictionary<string, List<EcgSession>> grouped,
        int target,
        Random rng)
    {
        if (grouped.Count < 2)
            yield break;

        var pool = grouped
            .Where(kvp => kvp.Key != anchorUser && kvp.Value.Count > 0)
            .Select(kvp => kvp.Value)
            .ToList();

        if (pool.Count == 0 || anchorSessions.Count == 0)
            yield break;

        var produced = 0;
        var anchorIndex = 0;
        while (produced < target)
        {
            var anchor = anchorSessions[anchorIndex % anchorSessions.Count];
            anchorIndex++;

            var otherSessions = pool[rng.Next(pool.Count)];
            var other = otherSessions[rng.Next(otherSessions.Count)];
            yield return MakePair(anchor, other, false);
            produced++;
        }
    }

    private static void Shuffle<T>(IList<T> items, Random rng)
    {
        for (int i = items.Count - 1; i > 0; i--)
        {
            var swapIndex = rng.Next(i + 1);
            (items[i], items[swapIndex]) = (items[swapIndex], items[i]);
        }
    }

    private static PairRow MakePair(EcgSession a, EcgSession b, bool label)
    {
        static float Diff(double x, double y) => (float)Math.Abs(x - y);
        static float Relative(double x, double y)
        {
            var denom = Math.Max(1e-6, Math.Max(Math.Abs(x), Math.Abs(y)));
            return denom <= 0 ? 0f : (float)(Math.Abs(x - y) / denom);
        }

        return new PairRow
        {
            Label = label,
            dMean = Diff(a.Mean, b.Mean),
            dStd = Diff(a.Std, b.Std),
            dRms = Diff(a.Rms, b.Rms),
            dMin = Diff(a.Min, b.Min),
            dMax = Diff(a.Max, b.Max),
            dSkewness = Diff(a.Skewness, b.Skewness),
            dKurtosis = Diff(a.Kurtosis, b.Kurtosis),
            dEstimatedBpm = Diff(a.EstimatedBpm, b.EstimatedBpm),
            dPeakCount = Diff(a.PeakCount, b.PeakCount),
            dRrMeanMs = Diff(a.RrMeanMs, b.RrMeanMs),
            dRrStdMs = Diff(a.RrStdMs, b.RrStdMs),
            dQrsWidthMs = Diff(a.QrsWidthMs, b.QrsWidthMs),
            dLowFreqPowerRatio = Diff(a.LowFreqPowerRatio, b.LowFreqPowerRatio),
            dMidFreqPowerRatio = Diff(a.MidFreqPowerRatio, b.MidFreqPowerRatio),
            dHighFreqPowerRatio = Diff(a.HighFreqPowerRatio, b.HighFreqPowerRatio),
            dSpectralCentroidHz = Diff(a.SpectralCentroidHz, b.SpectralCentroidHz),
            dSpectralEntropy = Diff(a.SpectralEntropy, b.SpectralEntropy),
            dHrvDailyRmssd = Diff(a.HrvDailyRmssd, b.HrvDailyRmssd),
            dVeryLowFreqPowerRatio = Diff(a.VeryLowFreqPowerRatio, b.VeryLowFreqPowerRatio),
            dSignalQualityScore = Diff(a.SignalQualityScore, b.SignalQualityScore),
            dMotionArtifactIndex = Diff(a.MotionArtifactIndex, b.MotionArtifactIndex),
            dBaselineDriftRatio = Diff(a.BaselineDriftRatio, b.BaselineDriftRatio),
            dEmbeddingL2 = ComputeEmbeddingDistance(a.Embedding, b.Embedding),
            dEmbeddingCosine = ComputeEmbeddingCosine(a.Embedding, b.Embedding),
            dMeanRelative = Relative(a.Mean, b.Mean),
            dStdRelative = Relative(a.Std, b.Std),
            dRrMeanRatio = Relative(a.RrMeanMs, b.RrMeanMs),
            dRrStdRatio = Relative(a.RrStdMs, b.RrStdMs),
            dHighLowBalance = Diff(
                a.HighFreqPowerRatio - a.LowFreqPowerRatio,
                b.HighFreqPowerRatio - b.LowFreqPowerRatio),
            dQualityRatio = Relative(a.SignalQualityScore, b.SignalQualityScore)
        };
    }

    private static float ComputeEmbeddingDistance(float[]? a, float[]? b)
    {
        if (a is null || b is null || a.Length == 0 || b.Length == 0)
            return 0f;

        var length = Math.Min(a.Length, b.Length);
        double sum = 0d;
        for (int i = 0; i < length; i++)
        {
            var diff = a[i] - b[i];
            sum += diff * diff;
        }

        return (float)Math.Sqrt(sum);
    }

    private static float ComputeEmbeddingCosine(float[]? a, float[]? b)
    {
        if (a is null || b is null || a.Length == 0 || b.Length == 0)
            return 0f;

        var length = Math.Min(a.Length, b.Length);
        double dot = 0d;
        double magA = 0d;
        double magB = 0d;
        for (int i = 0; i < length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }

        if (magA <= 0 || magB <= 0)
            return 0f;

        return (float)(dot / (Math.Sqrt(magA) * Math.Sqrt(magB)));
    }
}
