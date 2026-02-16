using FitServer.Models;
using Google.Cloud.Firestore;
using Microsoft.Extensions.Options;
using Microsoft.ML;
using System.Linq;

namespace FitServer.Services;

public sealed record EcgSessionRecord(
    string DocumentId,
    string FitbitUserId,
    DateTimeOffset? EcgStartTime,
    double? HrvDailyRmssd,
    EcgFeatures Features,
    SessionMetadata? Metadata,
    IReadOnlyList<int> WaveformPreview,
    double SignalQualityScore,
    double MotionArtifactIndex,
    double BaselineDriftRatio,
    int SamplingHz,
    int ScalingFactor,
    IReadOnlyList<string> Tags,
    string? Notes);

public sealed record VerifyResult(
    string FitbitUserId,
    bool Authenticated,
    double Score,
    double Threshold,
    DateTimeOffset? EcgStartTime,
    double? HrvDailyRmssd,
    IReadOnlyList<double> ComparisonScores,
    double ConsensusScore,
    int PassingVotes,
    ConfidenceSnapshot? Confidence);

public interface IEcgAuthService
{
    Task<EcgSessionRecord> CollectSessionAsync(string accessToken, SessionCaptureRequest? request, CancellationToken ct = default);
    Task<ModelTrainingResult> TrainModelAsync(int maxPairsPerUser, CancellationToken ct = default);
    Task<VerifyResult> VerifyAsync(string accessToken, double threshold, CancellationToken ct = default);
    Task<ContinuousVerifyResponse> VerifyContinuouslyAsync(string accessToken, ContinuousVerifyRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<EcgSessionRecord>> GetSessionsAsync(CancellationToken ct = default);
}

public sealed class EcgAuthService : IEcgAuthService
{
    private const double DefaultThreshold = 0.85;
    private const int VerificationBaselineCount = 0;
    private const int ScoreConsensusTopK = 3;
    private readonly IFitbitEcgService _fitbit;
    private readonly IEcgFeatureExtractor _extractor;
    private readonly IEcgMlTrainer _trainer;
    private readonly IEcgEmbeddingService _embedding;
    private readonly IConfidenceModelingService _confidence;
    private readonly IEcgModelStateRepository _modelState;
    private readonly IOptionsMonitor<AdaptiveModelOptions> _adaptiveOptions;
    private readonly FirestoreDb _db;
    private readonly MLContext _mlContext = new();
    private readonly string _modelPath;
    private readonly string _correctionModelPath;
    private ITransformer? _model;
    private DataViewSchema? _modelSchema;
    private readonly object _modelLock = new();
    private ITransformer? _correctionModel;
    private DataViewSchema? _correctionSchema;
    private readonly object _correctionLock = new();

    public EcgAuthService(
        IFitbitEcgService fitbit,
        IEcgFeatureExtractor extractor,
        IEcgMlTrainer trainer,
        IEcgEmbeddingService embedding,
        IConfidenceModelingService confidence,
        IEcgModelStateRepository modelState,
        IOptionsMonitor<AdaptiveModelOptions> adaptiveOptions,
        IWebHostEnvironment environment,
        FirestoreDb db)
    {
        _fitbit = fitbit;
        _extractor = extractor;
        _trainer = trainer;
        _embedding = embedding;
        _confidence = confidence;
        _modelState = modelState;
        _adaptiveOptions = adaptiveOptions;
        _db = db;
        _modelPath = Path.Combine(environment.ContentRootPath, "ecg_auth_model.zip");
        _correctionModelPath = BuildCorrectionModelPath(_modelPath);
    }

    public async Task<EcgSessionRecord> CollectSessionAsync(string accessToken, SessionCaptureRequest? request, CancellationToken ct = default)
    {
        var capture = await CaptureSessionAsync(accessToken, ct);
        var collectedAtUtc = DateTime.UtcNow;
        var metadataDict = ToMetadataDictionary(request?.Metadata);
        var tags = request?.Tags?.Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();

        var payload = new Dictionary<string, object>
        {
            ["fitbitUserId"] = capture.UserId,
            ["sessionTimeUtc"] = Timestamp.FromDateTime(collectedAtUtc),
            ["collectedAtUtc"] = Timestamp.FromDateTime(collectedAtUtc),
            ["hrvDailyRmssd"] = capture.Hrv ?? 0d,
            ["ecgStartTime"] = capture.Reading.StartTime?.UtcDateTime,
            ["ecgFeatures"] = FeaturesToDictionary(capture.Features),
            ["samplingFrequencyHz"] = capture.SamplingHz,
            ["scalingFactor"] = capture.ScalingFactor,
            ["signalQualityScore"] = capture.Features.SignalQualityScore,
            ["motionArtifactIndex"] = capture.Features.MotionArtifactIndex,
            ["baselineDriftRatio"] = capture.Features.BaselineDriftRatio,
            ["waveformBlob"] = capture.CompressedWaveform,
            ["waveformPreview"] = capture.WaveformPreview.ToArray(),
            ["metadata"] = metadataDict,
            ["tags"] = tags,
            ["notes"] = request?.Notes ?? string.Empty,
            ["embeddingVector"] = capture.Features.EmbeddingVector?.Select(v => (double)v).ToArray()
        };

        var docRef = await _db.Collection("ecg_sessions").AddAsync(payload, ct);
        await _modelState.IncrementSessionCountAsync(1, ct);

        return new EcgSessionRecord(
            docRef.Id,
            capture.UserId,
            capture.Reading.StartTime,
            capture.Hrv,
            capture.Features,
            request?.Metadata,
            capture.WaveformPreview,
            capture.Features.SignalQualityScore,
            capture.Features.MotionArtifactIndex,
            capture.Features.BaselineDriftRatio,
            capture.SamplingHz,
            capture.ScalingFactor,
            tags,
            request?.Notes);
    }

    public async Task<ModelTrainingResult> TrainModelAsync(int maxPairsPerUser, CancellationToken ct = default)
    {
        var result = await _trainer.TrainAndSaveAsync(_modelPath, maxPairsPerUser, ct);
        var sessionCount = await _modelState.GetSessionCountAsync(ct);
        await _modelState.MarkModelTrainedAsync(result, sessionCount, ct);
        return result;
    }

    public async Task<IReadOnlyList<EcgSessionRecord>> GetSessionsAsync(CancellationToken ct = default)
    {
        var snapshot = await _db.Collection("ecg_sessions").GetSnapshotAsync(ct);
        var sessions = new List<EcgSessionRecord>(snapshot.Count);

        foreach (var doc in snapshot.Documents)
        {
            var data = doc.ToDictionary();
            var userId = data.TryGetValue("fitbitUserId", out var userObj) ? userObj?.ToString() ?? string.Empty : string.Empty;
            var features = TryParseFeatures(data);
            var metadata = TryParseMetadata(data.TryGetValue("metadata", out var metaObj) ? metaObj : null);
            var preview = TryParseIntList(data, "waveformPreview") ?? Array.Empty<int>();
            var tags = TryParseStringList(data, "tags") ?? Array.Empty<string>();
            var notes = data.TryGetValue("notes", out var notesObj) ? notesObj?.ToString() : null;
            var samplingHz = data.TryGetValue("samplingFrequencyHz", out var samplingObj) ? Convert.ToInt32(samplingObj) : 0;
            var scalingFactor = data.TryGetValue("scalingFactor", out var scalingObj) ? Convert.ToInt32(scalingObj) : 0;
            var signalQuality = TryGetDouble(data, "signalQualityScore") ?? features.SignalQualityScore;
            var motionArtifact = TryGetDouble(data, "motionArtifactIndex") ?? features.MotionArtifactIndex;
            var baselineDrift = TryGetDouble(data, "baselineDriftRatio") ?? features.BaselineDriftRatio;

            sessions.Add(new EcgSessionRecord(
                doc.Id,
                userId,
                TryParseDateTimeOffset(data.TryGetValue("ecgStartTime", out var startTimeObj) ? startTimeObj : null),
                TryGetDouble(data, "hrvDailyRmssd"),
                features,
                metadata,
                preview,
                signalQuality,
                motionArtifact,
                baselineDrift,
                samplingHz,
                scalingFactor,
                tags,
                notes));
        }

        return sessions;
    }

    public async Task<VerifyResult> VerifyAsync(string accessToken, double threshold, CancellationToken ct = default)
    {
        var userId = await _fitbit.GetFitbitUserIdAsync(accessToken, ct);
        var limit = VerificationBaselineCount > 0 ? VerificationBaselineCount : (int?)null;
        var baselineSessions = await LoadSessionsForUserAsync(userId, limit, ct);
        if (baselineSessions.Count == 0)
            throw new InvalidOperationException("No stored ECG sessions. Call /api/ecg-auth/collect-session first.");

        var attempt = await CaptureSessionAsync(accessToken, ct);
        var attemptFeatures = attempt.Features;

        var summary = await ScoreAttemptAsync(
            userId,
            attemptFeatures,
            attempt.Reading.StartTime,
            attempt.Hrv,
            threshold,
            ct,
            baselineSessions);

        await LogVerificationAttemptAsync(summary, ct);
        return summary.Result;
    }

    public async Task<ContinuousVerifyResponse> VerifyContinuouslyAsync(string accessToken, ContinuousVerifyRequest request, CancellationToken ct = default)
    {
        var userId = await _fitbit.GetFitbitUserIdAsync(accessToken, ct);
        var threshold = request.Threshold is > 0 ? request.Threshold.Value : DefaultThreshold;
        var windowMinutes = Math.Clamp(request.WindowMinutes, 5, 120);
        var strideMinutes = Math.Clamp(request.StrideMinutes, 1, windowMinutes);
        var slices = Math.Max(1, (int)Math.Ceiling(windowMinutes / (double)strideMinutes));
        var perRequestLimit = Math.Min(20, slices + 1);
        var baselineLimit = VerificationBaselineCount > 0 ? VerificationBaselineCount : (int?)null;
        var baselineSessions = await LoadSessionsForUserAsync(userId, baselineLimit, ct);
        if (baselineSessions.Count == 0)
            throw new InvalidOperationException("No stored ECG sessions. Call /api/ecg-auth/collect-session first.");

        var hrv = await _fitbit.GetDailyHrvAsync(accessToken, DateOnly.FromDateTime(DateTime.UtcNow), ct);
        var readings = await _fitbit.GetRecentEcgsAsync(accessToken, perRequestLimit, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-2)), ct);
        if (readings.Count == 0)
            throw new InvalidOperationException("No ECG waveform found. Ask the user to run an ECG recording on the watch.");

        var samples = new List<ContinuousVerifySample>();
        var scores = new List<double>();
        foreach (var reading in readings.OrderBy(r => r.StartTime ?? DateTimeOffset.MinValue))
        {
            if (reading?.WaveFormSamples == null || reading.WaveFormSamples.Count == 0)
                continue;

            var scalingFactor = reading.ScalingFactor is > 0 ? reading.ScalingFactor.Value : 10922;
            var samplingHz = reading.SamplingFrequencyHz is > 0 ? reading.SamplingFrequencyHz.Value : 250;
            var features = _extractor.Extract(reading.WaveFormSamples, scalingFactor, samplingHz);
            var embedding = _embedding.GenerateEmbedding(reading.WaveFormSamples, scalingFactor, samplingHz);
            if (embedding is { Length: > 0 })
                features = features with { EmbeddingVector = embedding };
            EcgQualityRules.EnsureAcceptable(features);

            var summary = await ScoreAttemptAsync(
                userId,
                features,
                reading.StartTime,
                hrv,
                threshold,
                ct,
                baselineSessions);

            await LogVerificationAttemptAsync(summary, ct);
            samples.Add(new ContinuousVerifySample
            {
                WindowStartUtc = reading.StartTime ?? DateTimeOffset.UtcNow,
                WindowEndUtc = (reading.StartTime ?? DateTimeOffset.UtcNow).AddMinutes(strideMinutes),
                Score = summary.Result.Score,
                Passes = summary.Result.Authenticated
            });
            scores.Add(summary.Result.Score);
        }

        if (samples.Count == 0)
            throw new InvalidOperationException("Unable to evaluate ECG stream. Ensure new Fitbit ECG traces are available.");

        return new ContinuousVerifyResponse
        {
            Authenticated = samples.Count > 0 && samples.All(s => s.Passes),
            RollingMeanScore = scores.Average(),
            RollingWorstScore = scores.Min(),
            Samples = samples
        };
    }

    private async Task<CaptureContext> CaptureSessionAsync(string accessToken, CancellationToken ct)
    {
        var userId = await _fitbit.GetFitbitUserIdAsync(accessToken, ct);
        var ecg = await _fitbit.GetLatestEcgAsync(accessToken, null, ct);
        if (ecg?.WaveFormSamples == null || ecg.WaveFormSamples.Count == 0)
            throw new InvalidOperationException("No ECG waveform found. Ask the user to run an ECG recording on the watch.");

        var scalingFactor = ecg.ScalingFactor is > 0 ? ecg.ScalingFactor.Value : 10922;
        var samplingHz = ecg.SamplingFrequencyHz is > 0 ? ecg.SamplingFrequencyHz.Value : 250;
        var features = _extractor.Extract(ecg.WaveFormSamples, scalingFactor, samplingHz);
        var embedding = _embedding.GenerateEmbedding(ecg.WaveFormSamples, scalingFactor, samplingHz);
        if (embedding is { Length: > 0 })
            features = features with { EmbeddingVector = embedding };
        EcgQualityRules.EnsureAcceptable(features);
        var hrv = await _fitbit.GetDailyHrvAsync(accessToken, DateOnly.FromDateTime(DateTime.UtcNow), ct);
        var compressed = WaveformCompressor.Compress(ecg.WaveFormSamples);
        var preview = ecg.WaveFormSamples.Take(Math.Min(64, ecg.WaveFormSamples.Count)).ToArray();

        return new CaptureContext(userId, ecg, features, hrv, compressed, preview, samplingHz, scalingFactor);
    }

    private async Task<VerificationSummary> ScoreAttemptAsync(
        string userId,
        EcgFeatures features,
        DateTimeOffset? startTime,
        double? hrv,
        double threshold,
        CancellationToken ct,
        IReadOnlyList<EcgSession>? cachedSessions = null)
    {
        IReadOnlyList<EcgSession> storedSessions;
        if (cachedSessions is null)
        {
            var limit = VerificationBaselineCount > 0 ? VerificationBaselineCount : (int?)null;
            storedSessions = await LoadSessionsForUserAsync(userId, limit, ct);
        }
        else
        {
            storedSessions = cachedSessions;
        }

        if (storedSessions.Count == 0)
            throw new InvalidOperationException("No stored ECG sessions. Call /api/ecg-auth/collect-session first.");

        var predictor = CreatePredictionEngine();
        var corrector = CreateCorrectionEngine();
        var scores = new List<double>(storedSessions.Count);
        double? latestScore = null;

        for (int i = 0; i < storedSessions.Count; i++)
        {
            var session = storedSessions[i];
            var pair = BuildPair(session, features, hrv ?? 0d);
            var score = EvaluateScore(predictor, corrector, pair);
            scores.Add(score);
            if (i == 0)
                latestScore = score;
        }

        var appliedThreshold = threshold <= 0 ? DefaultThreshold : threshold;
        var orderedScores = scores.OrderByDescending(s => s).ToList();
        var topK = Math.Min(ScoreConsensusTopK, orderedScores.Count);
        var topScores = orderedScores.Take(topK).ToList();
        var consensusScore = topScores.Count > 0 ? topScores.Average() : 0d;
        var bestScore = orderedScores.Count > 0 ? orderedScores[0] : 0d;
        var passingVotes = scores.Count(s => s >= appliedThreshold);
        var latestPasses = latestScore is not null && latestScore.Value >= appliedThreshold;
        var authenticated = latestPasses &&
                            bestScore >= appliedThreshold &&
                            consensusScore >= appliedThreshold;

        ConfidenceSnapshot? confidence = null;
        try
        {
            confidence = await _confidence.AppendAsync(userId, bestScore, appliedThreshold, authenticated, ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Confidence modeling failed: {ex.Message}");
        }

        var result = new VerifyResult(
            userId,
            authenticated,
            bestScore,
            appliedThreshold,
            startTime,
            hrv,
            scores,
            consensusScore,
            passingVotes,
            confidence);

        return new VerificationSummary(result, consensusScore, passingVotes, appliedThreshold, latestScore, latestPasses, confidence);
    }

    private Task LogVerificationAttemptAsync(VerificationSummary summary, CancellationToken ct)
    {
        return _db.Collection("ecg_auth_logs").AddAsync(new Dictionary<string, object>
        {
            ["fitbitUserId"] = summary.Result.FitbitUserId,
            ["attemptedAtUtc"] = Timestamp.FromDateTime(DateTime.UtcNow),
            ["score"] = summary.Result.Score,
            ["threshold"] = summary.AppliedThreshold,
            ["meanScore"] = summary.MeanScore,
            ["votesPassing"] = summary.VotesPassing,
            ["consensusScore"] = summary.Result.ConsensusScore,
            ["latestScore"] = summary.LatestScore ?? 0d,
            ["latestPasses"] = summary.LatestPasses,
            ["authenticated"] = summary.Result.Authenticated,
            ["comparisonCount"] = summary.Result.ComparisonScores.Count,
            ["confidenceLevel"] = summary.Result.Confidence?.ConfidenceLevel ?? 0d,
            ["confidenceDrift"] = summary.Result.Confidence?.Drift ?? 0d,
            ["confidenceSamples"] = summary.Result.Confidence?.SampleCount ?? 0
        }, ct);
    }

    private async Task<List<EcgSession>> LoadSessionsForUserAsync(string userId, int? limit = null, CancellationToken ct = default)
    {
        var snapshot = await _db.Collection("ecg_sessions")
            .WhereEqualTo("fitbitUserId", userId)
            .GetSnapshotAsync(ct);
        var sessions = new List<EcgSession>();

        foreach (var doc in snapshot.Documents)
        {
            var data = doc.ToDictionary();
            var features = TryParseFeatures(data);
            if (!EcgQualityRules.IsAcceptable(features))
                continue;
            var waveform = WaveformCompressor.Decompress(data.TryGetValue("waveformBlob", out var blobObj) ? blobObj?.ToString() : null);

            sessions.Add(new EcgSession
            {
                FitbitUserId = userId,
                HrvDailyRmssd = data.TryGetValue("hrvDailyRmssd", out var hrv) ? Convert.ToDouble(hrv) : 0d,
                CollectedAtUtc = TryParseDateTimeOffset(data.TryGetValue("collectedAtUtc", out var collectedAtObj) ? collectedAtObj : null),
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
                SamplingHz = data.TryGetValue("samplingFrequencyHz", out var samplingObj) ? Convert.ToInt32(samplingObj) : 0,
                ScalingFactor = data.TryGetValue("scalingFactor", out var scalingObj) ? Convert.ToInt32(scalingObj) : 0
            });
        }

        var ordered = sessions
            .OrderByDescending(s => s.CollectedAtUtc ?? DateTimeOffset.MinValue)
            .ToList();

        if (limit is > 0)
            return ordered.Take(limit.Value).ToList();

        return ordered;
    }

    private sealed record VerificationSummary(
        VerifyResult Result,
        double MeanScore,
        int VotesPassing,
        double AppliedThreshold,
        double? LatestScore,
        bool LatestPasses,
        ConfidenceSnapshot? Confidence);

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

    private PredictionEngine<PairCorrectionInput, PairPrediction>? CreateCorrectionEngine()
    {
        lock (_correctionLock)
        {
            if (_correctionModel == null)
            {
                if (!File.Exists(_correctionModelPath))
                    return null;

                _correctionModel = _mlContext.Model.Load(_correctionModelPath, out _correctionSchema);
            }

            return _mlContext.Model.CreatePredictionEngine<PairCorrectionInput, PairPrediction>(_correctionModel, _correctionSchema!);
        }
    }

    private static PairRow BuildPair(EcgSession baseline, EcgFeatures attempt, double attemptHrv)
    {
        static float Diff(double a, double b) => (float)Math.Abs(a - b);
        var attemptEmbedding = attempt.EmbeddingVector;

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
            dRrMeanMs = Diff(baseline.RrMeanMs, attempt.RrMeanMs),
            dRrStdMs = Diff(baseline.RrStdMs, attempt.RrStdMs),
            dQrsWidthMs = Diff(baseline.QrsWidthMs, attempt.QrsWidthMs),
            dLowFreqPowerRatio = Diff(baseline.LowFreqPowerRatio, attempt.LowFreqPowerRatio),
            dMidFreqPowerRatio = Diff(baseline.MidFreqPowerRatio, attempt.MidFreqPowerRatio),
            dHighFreqPowerRatio = Diff(baseline.HighFreqPowerRatio, attempt.HighFreqPowerRatio),
            dSpectralCentroidHz = Diff(baseline.SpectralCentroidHz, attempt.SpectralCentroidHz),
            dSpectralEntropy = Diff(baseline.SpectralEntropy, attempt.SpectralEntropy),
            dVeryLowFreqPowerRatio = Diff(baseline.VeryLowFreqPowerRatio, attempt.VeryLowFreqPowerRatio),
            dSignalQualityScore = Diff(baseline.SignalQualityScore, attempt.SignalQualityScore),
            dMotionArtifactIndex = Diff(baseline.MotionArtifactIndex, attempt.MotionArtifactIndex),
            dBaselineDriftRatio = Diff(baseline.BaselineDriftRatio, attempt.BaselineDriftRatio),
            dHrvDailyRmssd = Diff(baseline.HrvDailyRmssd, attemptHrv),
            dEmbeddingL2 = ComputeEmbeddingDistance(baseline.Embedding, attemptEmbedding),
            dEmbeddingCosine = ComputeEmbeddingCosine(baseline.Embedding, attemptEmbedding)
        };
    }

    private static EcgFeatures BuildFeaturesFromSession(EcgSession session)
    {
        var features = new EcgFeatures(
            session.Mean,
            session.Std,
            session.Rms,
            session.Min,
            session.Max,
            session.Skewness,
            session.Kurtosis,
            session.EstimatedBpm,
            session.PeakCount,
            session.RrMeanMs,
            session.RrStdMs,
            session.QrsWidthMs,
            session.LowFreqPowerRatio,
            session.MidFreqPowerRatio,
            session.HighFreqPowerRatio,
            session.SpectralCentroidHz,
            session.SpectralEntropy,
            session.VeryLowFreqPowerRatio,
            session.SignalQualityScore,
            session.MotionArtifactIndex,
            session.BaselineDriftRatio);

        return session.Embedding is { Length: > 0 }
            ? features with { EmbeddingVector = session.Embedding }
            : features;
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
        ["PeakCount"] = features.PeakCount,
        ["RrMeanMs"] = features.RrMeanMs,
        ["RrStdMs"] = features.RrStdMs,
        ["QrsWidthMs"] = features.QrsWidthMs,
        ["LowFreqPowerRatio"] = features.LowFreqPowerRatio,
        ["MidFreqPowerRatio"] = features.MidFreqPowerRatio,
        ["HighFreqPowerRatio"] = features.HighFreqPowerRatio,
        ["SpectralCentroidHz"] = features.SpectralCentroidHz,
        ["SpectralEntropy"] = features.SpectralEntropy,
        ["VeryLowFreqPowerRatio"] = features.VeryLowFreqPowerRatio,
        ["SignalQualityScore"] = features.SignalQualityScore,
        ["MotionArtifactIndex"] = features.MotionArtifactIndex,
        ["BaselineDriftRatio"] = features.BaselineDriftRatio,
        ["EmbeddingVector"] = features.EmbeddingVector?.Select(v => (double)v).ToArray()
    };

    private static EcgFeatures TryParseFeatures(Dictionary<string, object> payload)
    {
        if (payload.TryGetValue("ecgFeatures", out var featuresObj) && featuresObj is Dictionary<string, object> dict)
        {
            double Get(string key) => dict.TryGetValue(key, out var value) ? Convert.ToDouble(value) : 0d;
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

        return new EcgFeatures(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    }

    private static Dictionary<string, object>? ToMetadataDictionary(SessionMetadata? metadata)
    {
        if (metadata is null)
            return null;

        var dict = new Dictionary<string, object>();
        if (!string.IsNullOrWhiteSpace(metadata.ActivityLabel))
            dict["activityLabel"] = metadata.ActivityLabel;
        if (!string.IsNullOrWhiteSpace(metadata.StressLevel))
            dict["stressLevel"] = metadata.StressLevel;
        if (!string.IsNullOrWhiteSpace(metadata.SensorPlacement))
            dict["sensorPlacement"] = metadata.SensorPlacement;
        if (!string.IsNullOrWhiteSpace(metadata.DeviceModel))
            dict["deviceModel"] = metadata.DeviceModel;
        return dict;
    }

    private sealed record CaptureContext(
        string UserId,
        EcgReading Reading,
        EcgFeatures Features,
        double? Hrv,
        string CompressedWaveform,
        IReadOnlyList<int> WaveformPreview,
        int SamplingHz,
        int ScalingFactor);

    private static string BuildCorrectionModelPath(string modelPath)
    {
        var directory = Path.GetDirectoryName(modelPath);
        var fileName = Path.GetFileNameWithoutExtension(modelPath);
        var corrected = string.IsNullOrWhiteSpace(fileName) ? "ecg_correction_model.zip" : $"{fileName}_correction.zip";
        return string.IsNullOrWhiteSpace(directory) ? corrected : Path.Combine(directory, corrected);
    }

    private static SessionMetadata? TryParseMetadata(object? value)
    {
        if (value is not Dictionary<string, object> dict)
            return null;

        return new SessionMetadata
        {
            ActivityLabel = TryGetString(dict, "activityLabel"),
            StressLevel = TryGetString(dict, "stressLevel"),
            SensorPlacement = TryGetString(dict, "sensorPlacement"),
            DeviceModel = TryGetString(dict, "deviceModel")
        };
    }

    private static IReadOnlyList<int>? TryParseIntList(Dictionary<string, object> payload, string key)
    {
        if (!payload.TryGetValue(key, out var raw) || raw is null)
            return null;

        if (raw is IEnumerable<object> enumerable)
        {
            var list = new List<int>();
            foreach (var item in enumerable)
                list.Add(Convert.ToInt32(item));
            return list;
        }

        if (raw is IEnumerable<int> ints)
            return ints.ToArray();

        return null;
    }

    private static IReadOnlyList<string>? TryParseStringList(Dictionary<string, object> payload, string key)
    {
        if (!payload.TryGetValue(key, out var raw) || raw is null)
            return null;

        if (raw is IEnumerable<object> enumerable)
        {
            var list = new List<string>();
            foreach (var item in enumerable)
                list.Add(item?.ToString() ?? string.Empty);
            return list;
        }

        if (raw is IEnumerable<string> strings)
            return strings.ToArray();

        return null;
    }

    private static string? TryGetString(Dictionary<string, object> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value) || value is null)
            return null;

        return value.ToString();
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

        var cosine = dot / (Math.Sqrt(magA) * Math.Sqrt(magB));
        return (float)cosine;
    }

    private static double EvaluateScore(
        PredictionEngine<PairRow, PairPrediction> predictor,
        PredictionEngine<PairCorrectionInput, PairPrediction>? correction,
        PairRow pair)
    {
        var basePrediction = predictor.Predict(pair);
        if (correction is null)
            return basePrediction.Probability;

        var correctionInput = new PairCorrectionInput
        {
            Score = basePrediction.Score,
            Probability = basePrediction.Probability,
            dSignalQualityScore = pair.dSignalQualityScore,
            dMotionArtifactIndex = pair.dMotionArtifactIndex,
            dBaselineDriftRatio = pair.dBaselineDriftRatio,
            dEmbeddingL2 = pair.dEmbeddingL2,
            dEmbeddingCosine = pair.dEmbeddingCosine
        };

        var corrected = correction.Predict(correctionInput);
        return corrected.Probability;
    }
}
