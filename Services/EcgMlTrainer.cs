using Google.Cloud.Firestore;
using Microsoft.ML;
using Microsoft.ML.Data;

namespace FitServer.Services;

public sealed class EcgSession
{
    public string FitbitUserId { get; set; } = string.Empty;
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
    [LoadColumn(10)] public float dHrvDailyRmssd { get; set; }
}

public sealed class PairPrediction
{
    [ColumnName("PredictedLabel")]
    public bool PredictedLabel { get; set; }

    public float Probability { get; set; }
    public float Score { get; set; }
}

public sealed record ModelTrainingResult(
    string ModelPath,
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

    public EcgMlTrainer()
    {
        _db = FirestoreDb.Create("fyp-assistant-7a216");
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

        var pipeline = ml.Transforms.Concatenate("Features",
                nameof(PairRow.dMean),
                nameof(PairRow.dStd),
                nameof(PairRow.dRms),
                nameof(PairRow.dMin),
                nameof(PairRow.dMax),
                nameof(PairRow.dSkewness),
                nameof(PairRow.dKurtosis),
                nameof(PairRow.dEstimatedBpm),
                nameof(PairRow.dPeakCount),
                nameof(PairRow.dHrvDailyRmssd))
            .Append(ml.BinaryClassification.Trainers.FastTree(
                numberOfLeaves: 32,
                numberOfTrees: 200,
                minimumExampleCountPerLeaf: 10));

        var model = pipeline.Fit(split.TrainSet);
        var predictions = model.Transform(split.TestSet);
        var metrics = ml.BinaryClassification.Evaluate(predictions);

        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
        ml.Model.Save(model, split.TrainSet.Schema, modelPath);

        return new ModelTrainingResult(
            modelPath,
            metrics.Accuracy,
            metrics.AreaUnderRocCurve,
            metrics.F1Score,
            sessions.Count,
            pairs.Count);
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

            double GetFeature(string key) => featureDict.TryGetValue(key, out var val) ? Convert.ToDouble(val) : 0d;

            sessions.Add(new EcgSession
            {
                FitbitUserId = userObj.ToString() ?? string.Empty,
                HrvDailyRmssd = payload.TryGetValue("hrvDailyRmssd", out var hrv) ? Convert.ToDouble(hrv) : 0d,
                Mean = GetFeature("Mean"),
                Std = GetFeature("Std"),
                Rms = GetFeature("Rms"),
                Min = GetFeature("Min"),
                Max = GetFeature("Max"),
                Skewness = GetFeature("Skewness"),
                Kurtosis = GetFeature("Kurtosis"),
                EstimatedBpm = GetFeature("EstimatedBpm"),
                PeakCount = GetFeature("PeakCount")
            });
        }

        return sessions;
    }

    private static List<PairRow> BuildPairDataset(IReadOnlyList<EcgSession> sessions, int maxPairsPerUser)
    {
        var grouped = sessions.GroupBy(s => s.FitbitUserId).ToDictionary(g => g.Key, g => g.ToList());
        if (grouped.Count < 2)
            throw new InvalidOperationException("Training requires sessions from at least two users.");

        var rng = new Random(42);
        var rows = new List<PairRow>();

        foreach (var (userId, sameUserSessions) in grouped)
        {
            if (sameUserSessions.Count < 5)
                continue;

            var samePairsTarget = Math.Min(maxPairsPerUser, sameUserSessions.Count * 10);
            for (int i = 0; i < samePairsTarget; i++)
            {
                var a = sameUserSessions[rng.Next(sameUserSessions.Count)];
                var b = sameUserSessions[rng.Next(sameUserSessions.Count)];
                if (ReferenceEquals(a, b))
                    continue;

                rows.Add(MakePair(a, b, true));
            }

            var negativeTarget = samePairsTarget;
            for (int i = 0; i < negativeTarget; i++)
            {
                var otherUser = grouped.Keys.ElementAt(rng.Next(grouped.Count));
                if (otherUser == userId)
                    continue;

                var otherSessions = grouped[otherUser];
                if (otherSessions.Count == 0)
                    continue;

                var a = sameUserSessions[rng.Next(sameUserSessions.Count)];
                var b = otherSessions[rng.Next(otherSessions.Count)];
                rows.Add(MakePair(a, b, false));
            }
        }

        return rows;
    }

    private static PairRow MakePair(EcgSession a, EcgSession b, bool label)
    {
        static float Diff(double x, double y) => (float)Math.Abs(x - y);

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
            dHrvDailyRmssd = Diff(a.HrvDailyRmssd, b.HrvDailyRmssd)
        };
    }
}
