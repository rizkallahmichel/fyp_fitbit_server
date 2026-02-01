using FitServer.Models;
using Google.Cloud.Firestore;
using Microsoft.ML;

namespace FitServer.Services;

public sealed record EcgSessionRecord(
    string DocumentId,
    string FitbitUserId,
    DateTimeOffset? EcgStartTime,
    double? HrvDailyRmssd,
    EcgFeatures Features);

public sealed record VerifyResult(
    string FitbitUserId,
    bool Authenticated,
    double Score,
    double Threshold,
    DateTimeOffset? EcgStartTime,
    double? HrvDailyRmssd,
    IReadOnlyList<double> ComparisonScores);

public interface IEcgAuthService
{
    Task<EcgSessionRecord> CollectSessionAsync(string accessToken, CancellationToken ct = default);
    Task<ModelTrainingResult> TrainModelAsync(int maxPairsPerUser, CancellationToken ct = default);
    Task<VerifyResult> VerifyAsync(string accessToken, double threshold, CancellationToken ct = default);
    Task<IReadOnlyList<EcgSessionRecord>> GetSessionsAsync(CancellationToken ct = default);
}

public sealed class EcgAuthService : IEcgAuthService
{
    private const double DefaultThreshold = 0.85;
    private readonly IFitbitEcgService _fitbit;
    private readonly IEcgFeatureExtractor _extractor;
    private readonly IEcgMlTrainer _trainer;
    private readonly FirestoreDb _db;
    private readonly MLContext _mlContext = new();
    private readonly string _modelPath;
    private ITransformer? _model;
    private DataViewSchema? _modelSchema;
    private readonly object _modelLock = new();

    public EcgAuthService(
        IFitbitEcgService fitbit,
        IEcgFeatureExtractor extractor,
        IEcgMlTrainer trainer,
        IWebHostEnvironment environment,
        FirestoreDb db)
    {
        _fitbit = fitbit;
        _extractor = extractor;
        _trainer = trainer;
        _db = db;
        _modelPath = Path.Combine(environment.ContentRootPath, "ecg_auth_model.zip");
    }

    public async Task<EcgSessionRecord> CollectSessionAsync(string accessToken, CancellationToken ct = default)
    {
        var capture = await CaptureSessionAsync(accessToken, ct);
        var collectedAtUtc = DateTime.UtcNow;

        var docRef = await _db.Collection("ecg_sessions").AddAsync(new Dictionary<string, object>
        {
            ["fitbitUserId"] = capture.UserId,
            ["sessionTimeUtc"] = Timestamp.FromDateTime(collectedAtUtc),
            ["collectedAtUtc"] = Timestamp.FromDateTime(collectedAtUtc),
            ["hrvDailyRmssd"] = capture.Hrv ?? 0d,
            ["ecgStartTime"] = capture.Reading.StartTime?.UtcDateTime,
            ["ecgFeatures"] = FeaturesToDictionary(capture.Features)
        }, ct);

        return new EcgSessionRecord(docRef.Id, capture.UserId, capture.Reading.StartTime, capture.Hrv, capture.Features);
    }

    public Task<ModelTrainingResult> TrainModelAsync(int maxPairsPerUser, CancellationToken ct = default)
        => _trainer.TrainAndSaveAsync(_modelPath, maxPairsPerUser, ct);

    public async Task<IReadOnlyList<EcgSessionRecord>> GetSessionsAsync(CancellationToken ct = default)
    {
        var snapshot = await _db.Collection("ecg_sessions").GetSnapshotAsync(ct);
        var sessions = new List<EcgSessionRecord>(snapshot.Count);

        foreach (var doc in snapshot.Documents)
        {
            var data = doc.ToDictionary();
            var userId = data.TryGetValue("fitbitUserId", out var userObj) ? userObj?.ToString() ?? string.Empty : string.Empty;
            var features = TryParseFeatures(data);

            sessions.Add(new EcgSessionRecord(
                doc.Id,
                userId,
                TryParseDateTimeOffset(data.TryGetValue("ecgStartTime", out var startTimeObj) ? startTimeObj : null),
                TryGetDouble(data, "hrvDailyRmssd"),
                features));
        }

        return sessions;
    }

    public async Task<VerifyResult> VerifyAsync(string accessToken, double threshold, CancellationToken ct = default)
    {
        var capture = await CaptureSessionAsync(accessToken, ct);
        var storedSessions = await LoadSessionsForUserAsync(capture.UserId, ct);
        if (storedSessions.Count == 0)
            throw new InvalidOperationException("No stored ECG sessions. Call /api/ecg-auth/collect-session first.");

        var predictor = CreatePredictionEngine();
        var scores = new List<double>();

        foreach (var session in storedSessions)
        {
            var pair = BuildPair(session, capture.Features, capture.Hrv ?? 0d);
            var prediction = predictor.Predict(pair);
            scores.Add(prediction.Probability);
        }

        var bestScore = scores.Count > 0 ? scores.Max() : 0d;
        var appliedThreshold = threshold <= 0 ? DefaultThreshold : threshold;
        var authenticated = bestScore >= appliedThreshold;

        await _db.Collection("ecg_auth_logs").AddAsync(new Dictionary<string, object>
        {
            ["fitbitUserId"] = capture.UserId,
            ["attemptedAtUtc"] = Timestamp.FromDateTime(DateTime.UtcNow),
            ["score"] = bestScore,
            ["threshold"] = appliedThreshold,
            ["authenticated"] = authenticated,
            ["comparisonCount"] = scores.Count
        }, ct);

        return new VerifyResult(
            capture.UserId,
            authenticated,
            bestScore,
            appliedThreshold,
            capture.Reading.StartTime,
            capture.Hrv,
            scores);
    }

    private async Task<(string UserId, EcgReading Reading, EcgFeatures Features, double? Hrv)> CaptureSessionAsync(string accessToken, CancellationToken ct)
    {
        var userId = await _fitbit.GetFitbitUserIdAsync(accessToken, ct);
        var ecg = await _fitbit.GetLatestEcgAsync(accessToken, null, ct);
        if (ecg?.WaveFormSamples == null || ecg.WaveFormSamples.Count == 0)
            throw new InvalidOperationException("No ECG waveform found. Ask the user to run an ECG recording on the watch.");

        var scalingFactor = ecg.ScalingFactor is > 0 ? ecg.ScalingFactor.Value : 10922;
        var samplingHz = ecg.SamplingFrequencyHz is > 0 ? ecg.SamplingFrequencyHz.Value : 250;
        var features = _extractor.Extract(ecg.WaveFormSamples, scalingFactor, samplingHz);
        var hrv = await _fitbit.GetDailyHrvAsync(accessToken, DateOnly.FromDateTime(DateTime.UtcNow), ct);

        return (userId, ecg, features, hrv);
    }

    private async Task<List<EcgSession>> LoadSessionsForUserAsync(string userId, CancellationToken ct)
    {
        var query = _db.Collection("ecg_sessions").WhereEqualTo("fitbitUserId", userId);
        var snapshot = await query.GetSnapshotAsync(ct);
        var sessions = new List<EcgSession>();

        foreach (var doc in snapshot.Documents)
        {
            var data = doc.ToDictionary();
            if (!data.TryGetValue("ecgFeatures", out var featuresObj) || featuresObj is not Dictionary<string, object> featureDict)
                continue;

            double GetFeature(string key) => featureDict.TryGetValue(key, out var val) ? Convert.ToDouble(val) : 0d;

            sessions.Add(new EcgSession
            {
                FitbitUserId = userId,
                HrvDailyRmssd = data.TryGetValue("hrvDailyRmssd", out var hrv) ? Convert.ToDouble(hrv) : 0d,
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

    private PredictionEngine<PairRow, PairPrediction> CreatePredictionEngine()
    {
        lock (_modelLock)
        {
            if (_model == null)
            {
                if (!File.Exists(_modelPath))
                    throw new InvalidOperationException("Model file not found. Run /api/ecg-auth/train first.");

                _model = _mlContext.Model.Load(_modelPath, out _modelSchema);
            }

            return _mlContext.Model.CreatePredictionEngine<PairRow, PairPrediction>(_model, _modelSchema!);
        }
    }

    private static PairRow BuildPair(EcgSession baseline, EcgFeatures attempt, double attemptHrv)
    {
        static float Diff(double a, double b) => (float)Math.Abs(a - b);

        return new PairRow
        {
            dMean = Diff(baseline.Mean, attempt.Mean),
            dStd = Diff(baseline.Std, attempt.Std),
            dRms = Diff(baseline.Rms, attempt.Rms),
            dMin = Diff(baseline.Min, attempt.Min),
            dMax = Diff(baseline.Max, attempt.Max),
            dSkewness = Diff(baseline.Skewness, attempt.Skewness),
            dKurtosis = Diff(baseline.Kurtosis, attempt.Kurtosis),
            dEstimatedBpm = Diff(baseline.EstimatedBpm, attempt.EstimatedBpm),
            dPeakCount = Diff(baseline.PeakCount, attempt.PeakCount),
            dHrvDailyRmssd = Diff(baseline.HrvDailyRmssd, attemptHrv)
        };
    }

    private static Dictionary<string, object> FeaturesToDictionary(EcgFeatures features) => new()
    {
        ["Mean"] = features.Mean,
        ["Std"] = features.Std,
        ["Rms"] = features.Rms,
        ["Min"] = features.Min,
        ["Max"] = features.Max,
        ["Skewness"] = features.Skewness,
        ["Kurtosis"] = features.Kurtosis,
        ["EstimatedBpm"] = features.EstimatedBpm,
        ["PeakCount"] = features.PeakCount
    };

    private static EcgFeatures TryParseFeatures(Dictionary<string, object> payload)
    {
        if (payload.TryGetValue("ecgFeatures", out var featuresObj) && featuresObj is Dictionary<string, object> dict)
        {
            double Get(string key) => dict.TryGetValue(key, out var value) ? Convert.ToDouble(value) : 0d;

            return new EcgFeatures(
                Get("Mean"),
                Get("Std"),
                Get("Rms"),
                Get("Min"),
                Get("Max"),
                Get("Skewness"),
                Get("Kurtosis"),
                Get("EstimatedBpm"),
                Get("PeakCount"));
        }

        return new EcgFeatures(0, 0, 0, 0, 0, 0, 0, 0, 0);
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

    private static double? TryGetDouble(Dictionary<string, object> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value) || value is null)
            return null;

        return Convert.ToDouble(value);
    }
}
