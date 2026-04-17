using FitServer.Models;
using Google.Cloud.Firestore;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.ML;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FitServer.Services;

public sealed record EcgSessionRecord(
    string DocumentId,
    string FitbitUserId,
    string DataSource,
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
    ConfidenceSnapshot? Confidence,
    double PassRatio = 0d,
    double MedianScore = 0d,
    double WorstScore = 0d,
    double ImpostorBestScore = 0d,
    double ImpostorConsensusScore = 0d,
    double BestSeparation = 0d,
    double ConsensusSeparation = 0d);

public interface IEcgAuthService
{
    Task<CurrentFitbitUserResponse> GetCurrentUserAsync(string accessToken, CancellationToken ct = default);
    Task<EcgSessionRecord> CollectSessionAsync(string accessToken, SessionCaptureRequest? request, CancellationToken ct = default);
    Task<ModelTrainingResult> TrainModelAsync(int maxPairsPerUser, CancellationToken ct = default);
    Task<VerifyResult> VerifyAsync(string accessToken, double threshold, CancellationToken ct = default);
    Task<ContinuousVerifyResponse> VerifyContinuouslyAsync(string accessToken, ContinuousVerifyRequest request, CancellationToken ct = default);
    Task<FalseAttemptReportResponse> ReportFalseAttemptAsync(FalseAttemptReportRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<EcgSessionRecord>> GetSessionsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<VerificationLogRecord>> GetVerificationLogsAsync(string? fitbitUserId = null, int limit = 100, CancellationToken ct = default);
    Task<EcgDataOverviewResponse> GetDataOverviewAsync(CancellationToken ct = default);
    Task<EcgBenchmarkResponse> BenchmarkEcgIdAsync(EcgBenchmarkRequest request, CancellationToken ct = default);
}

public sealed class EcgAuthService : IEcgAuthService
{
    private const double DefaultThreshold = 0.85;
    private const int VerificationBaselineCount = 0;
    private const int ScoreConsensusTopK = 3;
    private const int ImpostorConsensusTopK = 3;
    private const int ImpostorUsersToSample = 8;
    private const int ImpostorSessionsPerUser = 2;
    private const int MinimumBaselineSessions = 5;
    private const double MinimumPassingRatio = 0.9;
    private const double MaxOutlierDrop = 0.15;
    private const double MinimumGenuineImpostorMargin = 0.03;
    private const bool IncludeAutoVerifySessionsInBaseline = false;
    private const bool EnableAutoVerifyEnrollment = false;
    private const string AutoVerifyTag = "auto-verify";
    private const string AutoVerifyNote = "Captured automatically after successful /verify.";
    private const string VerifyAttemptTag = "verify-attempt";
    private const string FalseAttemptTag = "false-attempt";
    private const string ImpostorTag = "impostor";
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
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly bool _bypassFitbit;
    private readonly string? _primaryFitbitUserId;
    private readonly bool _enrollVerifyAttempts;
    private readonly bool _treatNonPrimaryAsImpostor;
    private readonly bool _rewriteFailedVerifyToImpostorUser;

    public EcgAuthService(
        IFitbitEcgService fitbit,
        IEcgFeatureExtractor extractor,
        IEcgMlTrainer trainer,
        IEcgEmbeddingService embedding,
        IConfidenceModelingService confidence,
        IEcgModelStateRepository modelState,
        IOptionsMonitor<AdaptiveModelOptions> adaptiveOptions,
        IWebHostEnvironment environment,
        FirestoreDb db,
        IHttpContextAccessor httpContextAccessor,
        IConfiguration configuration)
    {
        _fitbit = fitbit;
        _extractor = extractor;
        _trainer = trainer;
        _embedding = embedding;
        _confidence = confidence;
        _modelState = modelState;
        _adaptiveOptions = adaptiveOptions;
        _db = db;
        _httpContextAccessor = httpContextAccessor;
        _bypassFitbit = configuration.GetValue("Fitbit:DisableAuthMiddleware", false);
        _primaryFitbitUserId = configuration["EcgAuth:PrimaryFitbitUserId"]?.Trim();
        if (string.IsNullOrWhiteSpace(_primaryFitbitUserId))
            _primaryFitbitUserId = null;
        _enrollVerifyAttempts = configuration.GetValue("EcgAuth:EnableVerifyAttemptEnrollment", true);
        _treatNonPrimaryAsImpostor = configuration.GetValue("EcgAuth:TreatNonPrimaryAsImpostor", true);
        _rewriteFailedVerifyToImpostorUser = configuration.GetValue("EcgAuth:RewriteFailedVerifyToImpostorUser", true);
        Console.WriteLine($"[EcgAuthService] Fitbit bypass mode: {_bypassFitbit}");
        _modelPath = Path.Combine(environment.ContentRootPath, "ecg_auth_model.zip");
        _correctionModelPath = BuildCorrectionModelPath(_modelPath);
    }

    public async Task<EcgSessionRecord> CollectSessionAsync(string accessToken, SessionCaptureRequest? request, CancellationToken ct = default)
    {
        var capture = await CaptureSessionAsync(accessToken, ct);
        var tags = request?.Tags?.Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();

        return await PersistSessionAsync(
            capture,
            request?.Metadata,
            tags,
            request?.Notes,
            EcgDataSource.Fitbit,
            ct);
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
            sessions.Add(ToSessionRecord(doc));

        return sessions;
    }

    public async Task<IReadOnlyList<VerificationLogRecord>> GetVerificationLogsAsync(string? fitbitUserId = null, int limit = 100, CancellationToken ct = default)
    {
        var normalizedUserId = string.IsNullOrWhiteSpace(fitbitUserId) ? null : fitbitUserId.Trim();
        var cappedLimit = Math.Clamp(limit, 1, 500);
        var snapshot = await _db.Collection("ecg_auth_logs").GetSnapshotAsync(ct);

        var logs = snapshot.Documents
            .Select(MapVerificationLogRecord)
            .Where(log => normalizedUserId is null || string.Equals(log.FitbitUserId, normalizedUserId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(log => log.AttemptedAtUtc ?? DateTimeOffset.MinValue)
            .Take(cappedLimit)
            .ToList();

        return logs;
    }

    public async Task<EcgDataOverviewResponse> GetDataOverviewAsync(CancellationToken ct = default)
    {
        var sessionsSnapshot = await _db.Collection("ecg_sessions").GetSnapshotAsync(ct);
        var authLogsSnapshot = await _db.Collection("ecg_auth_logs").GetSnapshotAsync(ct);
        var confidenceSnapshot = await _db.Collection("ecg_confidence").GetSnapshotAsync(ct);
        var fitbitDataSnapshot = await _db.Collection("fitbit_data").GetSnapshotAsync(ct);
        var modelState = await _modelState.GetAsync(ct);

        var sessions = sessionsSnapshot.Documents
            .Select(ToSessionRecord)
            .OrderByDescending(session => session.EcgStartTime ?? DateTimeOffset.MinValue)
            .ToList();

        var participants = sessions
            .Where(session => !string.IsNullOrWhiteSpace(session.FitbitUserId))
            .GroupBy(session => session.FitbitUserId, StringComparer.OrdinalIgnoreCase)
            .Select(group => new EcgParticipantOverview
            {
                FitbitUserId = group.Key,
                SessionCount = group.Count(),
                LastSessionAtUtc = group.Max(item => item.EcgStartTime)
            })
            .OrderByDescending(item => item.SessionCount)
            .ThenBy(item => item.FitbitUserId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var recentSessions = sessions
            .Take(5)
            .Select(session => new EcgSessionPreview
            {
                DocumentId = session.DocumentId,
                FitbitUserId = session.FitbitUserId,
                DataSource = session.DataSource,
                EcgStartTimeUtc = session.EcgStartTime,
                SignalQualityScore = session.SignalQualityScore,
                Tags = session.Tags.ToList()
            })
            .ToList();

        var recentVerificationLogs = authLogsSnapshot.Documents
            .Select(MapVerificationLog)
            .OrderByDescending(log => log.AttemptedAtUtc ?? DateTimeOffset.MinValue)
            .Take(5)
            .ToList();

        var collections = new List<EcgCollectionOverview>
        {
            BuildCollectionOverview("ecg_sessions", sessionsSnapshot.Count, sessions.Max(session => session.EcgStartTime), $"{participants.Count} known participant(s)"),
            BuildCollectionOverview("ecg_auth_logs", authLogsSnapshot.Count, ExtractLatestTimestamp(authLogsSnapshot.Documents, "attemptedAtUtc"), $"{recentVerificationLogs.Count} recent verification preview item(s)"),
            BuildCollectionOverview("ecg_confidence", confidenceSnapshot.Count, ExtractLatestTimestamp(confidenceSnapshot.Documents, "updatedAtUtc"), "Per-user confidence drift state"),
            BuildCollectionOverview("fitbit_data", fitbitDataSnapshot.Count, ExtractLatestTimestamp(fitbitDataSnapshot.Documents, "date"), "Raw Fitbit data snapshots"),
            BuildCollectionOverview("ecg_model_state", modelState.LastTrainedUtc is null && modelState.SessionCount == 0 ? 0 : 1, modelState.LastTrainedUtc, modelState.RetrainPending ? "Retrain pending" : "Model state available")
        };

        var notes = new List<string>();
        if (sessions.Count == 0)
            notes.Add("No documents found in Firestore collection ecg_sessions. The UI participant selector will stay empty.");
        if (sessions.Count > 0 && participants.Count == 0)
            notes.Add("ECG sessions exist but no usable fitbitUserId was found.");
        if (sessions.Count == 0 && fitbitDataSnapshot.Count > 0)
            notes.Add("fitbit_data contains documents, but ecg_sessions is still empty. Collection has not been persisted into the authentication dataset yet.");
        if (authLogsSnapshot.Count == 0)
            notes.Add("No verification logs found yet in ecg_auth_logs.");
        if (modelState.SessionCount > 0 && modelState.SessionCount != sessions.Count)
            notes.Add($"Model state reports {modelState.SessionCount} session(s) while ecg_sessions currently exposes {sessions.Count} document(s).");

        return new EcgDataOverviewResponse
        {
            Collections = collections,
            Participants = participants,
            RecentSessions = recentSessions,
            RecentVerificationLogs = recentVerificationLogs,
            ModelState = new EcgModelStateOverview
            {
                LastTrainedUtc = modelState.LastTrainedUtc,
                SessionCount = modelState.SessionCount,
                SessionCountAtLastTrain = modelState.SessionCountAtLastTrain,
                RetrainPending = modelState.RetrainPending,
                RetrainReason = modelState.RetrainReason,
                LastAccuracy = modelState.LastAccuracy,
                LastAreaUnderRocCurve = modelState.LastAreaUnderRocCurve,
                LastF1Score = modelState.LastF1Score
            },
            Notes = notes
        };
    }

    public async Task<EcgBenchmarkResponse> BenchmarkEcgIdAsync(EcgBenchmarkRequest request, CancellationToken ct = default)
    {
        var maxPairs = Math.Max(50, request.MaxPairsPerUser);
        var testFraction = Math.Clamp(request.TestFraction, 0.2, 0.8);
        var modelPath = BuildBenchmarkModelPath(_modelPath, EcgDataSource.EcgId);
        var result = await _trainer.TrainWithScopeAsync(modelPath, maxPairs, EcgDatasetScope.EcgIdOnly, testFraction, ct);
        var stats = await GetDatasetStatsAsync(EcgDataSource.EcgId, ct);

        return new EcgBenchmarkResponse
        {
            Dataset = EcgDataSource.EcgId,
            SubjectCount = stats.SubjectCount,
            SessionCount = stats.SessionCount,
            TrainFraction = Math.Round(1 - testFraction, 4),
            TestFraction = Math.Round(testFraction, 4),
            Metrics = result
        };
    }

    public Task<CurrentFitbitUserResponse> GetCurrentUserAsync(string accessToken, CancellationToken ct = default)
    {
        return _fitbit.GetCurrentUserAsync(accessToken, ct);
    }

    public async Task<VerifyResult> VerifyAsync(string accessToken, double threshold, CancellationToken ct = default)
    {
        var currentUserId = await _fitbit.GetFitbitUserIdAsync(accessToken, ct);
        var claimedUserId = ResolveClaimedUserId(currentUserId);
        var nonPrimaryAttempt = _primaryFitbitUserId is not null &&
                                !string.Equals(currentUserId, claimedUserId, StringComparison.OrdinalIgnoreCase);
        var operatorMarkedImpostor = IsOperatorMarkedImpostor();

        var limit = VerificationBaselineCount > 0 ? VerificationBaselineCount : (int?)null;
        var baselineSessions = await LoadSessionsForUserAsync(claimedUserId, limit, ct, IncludeAutoVerifySessionsInBaseline);
        if (baselineSessions.Count < MinimumBaselineSessions && !IncludeAutoVerifySessionsInBaseline)
            baselineSessions = await LoadSessionsForUserAsync(claimedUserId, limit, ct, includeAutoVerify: true);
        if (baselineSessions.Count == 0)
            throw new InvalidOperationException("No stored ECG sessions. Call /api/ecg-auth/collect-session first.");
        if (baselineSessions.Count < MinimumBaselineSessions)
            throw new InvalidOperationException($"At least {MinimumBaselineSessions} ECG enrollment sessions are required for reliable verification.");

        var impostorSessions = await LoadImpostorSessionsAsync(claimedUserId, ImpostorUsersToSample, ImpostorSessionsPerUser, ct, IncludeAutoVerifySessionsInBaseline);

        var attempt = await CaptureSessionAsync(accessToken, ct);
        var attemptFeatures = attempt.Features;

        var summary = await ScoreAttemptAsync(
            claimedUserId,
            attemptFeatures,
            attempt.Reading.StartTime,
            attempt.Hrv,
            threshold,
            ct,
            baselineSessions,
            impostorSessions);

        var shouldTreatAsImpostor = operatorMarkedImpostor || (_treatNonPrimaryAsImpostor && nonPrimaryAttempt);
        var result = shouldTreatAsImpostor && summary.Result.Authenticated
            ? summary.Result with { Authenticated = false }
            : summary.Result;
        var effectiveSummary = shouldTreatAsImpostor && summary.Result.Authenticated
            ? summary with { Result = result }
            : summary;

        await LogVerificationAttemptAsync(effectiveSummary, ct);

        if (_enrollVerifyAttempts)
        {
            var tags = new List<string> { VerifyAttemptTag };
            string? notes = null;
            string? userIdOverride = null;
            var markImpostor = shouldTreatAsImpostor || (_rewriteFailedVerifyToImpostorUser && !result.Authenticated);
            if (markImpostor)
            {
                tags.Add(FalseAttemptTag);
                tags.Add(ImpostorTag);
                userIdOverride = BuildImpostorUserId(claimedUserId, currentUserId, nonPrimaryAttempt);
                notes = $"Captured from user {currentUserId} during /verify and stored as impostor sample for claimed user {claimedUserId} under {userIdOverride}.";
            }

            await PersistSessionAsync(
                attempt,
                null,
                tags,
                notes,
                EcgDataSource.Fitbit,
                ct,
                userIdOverride);
        }

        if (result.Authenticated &&
            EnableAutoVerifyEnrollment &&
            result.PassRatio >= 0.98 &&
            result.BestSeparation >= 0.1 &&
            result.ConsensusSeparation >= 0.1)
        {
            await PersistSessionAsync(
                attempt,
                null,
                new[] { AutoVerifyTag },
                AutoVerifyNote,
                EcgDataSource.Fitbit,
                ct);
        }
        return result;
    }

    private string ResolveClaimedUserId(string currentUserId)
    {
        var httpContext = _httpContextAccessor?.HttpContext;
        if (httpContext?.Request.Headers.TryGetValue("X-Claimed-Fitbit-UserId", out var claimedHeader) == true)
        {
            var headerValue = claimedHeader.ToString()?.Trim();
            if (!string.IsNullOrWhiteSpace(headerValue))
                return headerValue;
        }

        return _primaryFitbitUserId ?? currentUserId;
    }

    private bool IsOperatorMarkedImpostor()
    {
        var httpContext = _httpContextAccessor?.HttpContext;
        if (httpContext?.Request.Headers.TryGetValue("X-Impostor-Attempt", out var impostorHeader) == true)
        {
            var raw = impostorHeader.ToString()?.Trim();
            return string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static string BuildImpostorUserId(string claimedUserId, string currentUserId, bool nonPrimaryAttempt)
    {
        var claimed = string.IsNullOrWhiteSpace(claimedUserId) ? "unknown" : claimedUserId.Trim();
        if (nonPrimaryAttempt && !string.IsNullOrWhiteSpace(currentUserId) &&
            !string.Equals(claimedUserId, currentUserId, StringComparison.OrdinalIgnoreCase))
            return $"{claimed}#impostor#{currentUserId.Trim()}";

        return $"{claimed}#impostor";
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
        var baselineSessions = await LoadSessionsForUserAsync(userId, baselineLimit, ct, IncludeAutoVerifySessionsInBaseline);
        if (baselineSessions.Count < MinimumBaselineSessions && !IncludeAutoVerifySessionsInBaseline)
            baselineSessions = await LoadSessionsForUserAsync(userId, baselineLimit, ct, includeAutoVerify: true);
        if (baselineSessions.Count == 0)
            throw new InvalidOperationException("No stored ECG sessions. Call /api/ecg-auth/collect-session first.");
        if (baselineSessions.Count < MinimumBaselineSessions)
            throw new InvalidOperationException($"At least {MinimumBaselineSessions} ECG enrollment sessions are required for reliable verification.");

        var impostorSessions = await LoadImpostorSessionsAsync(userId, ImpostorUsersToSample, ImpostorSessionsPerUser, ct, IncludeAutoVerifySessionsInBaseline);

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
                baselineSessions,
                impostorSessions);

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

    public async Task<FalseAttemptReportResponse> ReportFalseAttemptAsync(FalseAttemptReportRequest request, CancellationToken ct = default)
    {
        if (request is null)
            throw new InvalidOperationException("Request body is required.");
        if (string.IsNullOrWhiteSpace(request.FitbitUserId))
            throw new InvalidOperationException("fitbitUserId is required.");
        if (request.EcgStartTime is null)
            throw new InvalidOperationException("ecgStartTime is required.");

        var userId = request.FitbitUserId.Trim();
        var startTimeUtc = request.EcgStartTime.Value.UtcDateTime;
        var snapshot = await _db.Collection("ecg_sessions")
            .WhereEqualTo("fitbitUserId", userId)
            .WhereEqualTo("ecgStartTime", startTimeUtc)
            .Limit(1)
            .GetSnapshotAsync(ct);

        var session = snapshot.Documents.FirstOrDefault();
        if (session is null)
            throw new InvalidOperationException($"No ECG session found for {userId} at {request.EcgStartTime.Value:O}. The sample may not have been persisted.");

        var payload = session.ToDictionary();
        var existingTags = TryParseStringList(payload, "tags") ?? Array.Empty<string>();
        var tags = existingTags
            .Concat(new[] { FalseAttemptTag, ImpostorTag })
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var existingNotes = payload.TryGetValue("notes", out var notesObj) ? notesObj?.ToString() ?? string.Empty : string.Empty;
        var reason = string.IsNullOrWhiteSpace(request.Notes) ? "Reported by operator." : request.Notes.Trim();
        var securityNote = $"[SECURITY {DateTime.UtcNow:O}] Marked as false attempt/impostor. {reason}";
        var updatedNotes = string.IsNullOrWhiteSpace(existingNotes)
            ? securityNote
            : $"{existingNotes}\n{securityNote}";

        await session.Reference.SetAsync(new Dictionary<string, object>
        {
            ["tags"] = tags,
            ["notes"] = updatedNotes,
            ["securityLabel"] = ImpostorTag,
            ["falseAttemptReportedAtUtc"] = Timestamp.FromDateTime(DateTime.UtcNow)
        }, SetOptions.MergeAll, ct);

        var retrainRequested = await _modelState.TryRequestRetrainAsync(
            $"False attempt reported for {userId} at {request.EcgStartTime.Value:O}",
            ct);

        return new FalseAttemptReportResponse
        {
            FitbitUserId = userId,
            EcgStartTime = request.EcgStartTime,
            SessionDocumentId = session.Id,
            Tags = tags,
            RetrainRequested = retrainRequested
        };
    }

    private async Task<CaptureContext> CaptureSessionAsync(string accessToken, CancellationToken ct)
    {
        var testCapture = await TryCaptureTestSessionAsync(accessToken, ct);
        if (testCapture is not null)
            return testCapture;

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

    private async Task<CaptureContext?> TryCaptureTestSessionAsync(string accessToken, CancellationToken ct)
    {
        if (!_bypassFitbit)
            return null;

        var httpContext = _httpContextAccessor?.HttpContext;
        string? explicitSessionId = null;
        if (httpContext is not null && httpContext.Items.TryGetValue("TestSessionId", out var item) && item is string sessionId && !string.IsNullOrWhiteSpace(sessionId))
            explicitSessionId = sessionId;

        var selection = !string.IsNullOrWhiteSpace(explicitSessionId)
            ? new TestSelection(explicitSessionId, null)
            : TestSelection.FromAccessToken(accessToken);

        var snapshot = await LoadTestSessionSnapshotAsync(selection, ct);
        if (snapshot is null || !snapshot.Exists)
            throw new InvalidOperationException("No stored ECG sessions available for offline verification.");

        return BuildCaptureContextFromSnapshot(snapshot);
    }

    private async Task<DocumentSnapshot?> LoadTestSessionSnapshotAsync(TestSelection selection, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(selection.SessionId))
            return await _db.Collection("ecg_sessions").Document(selection.SessionId).GetSnapshotAsync(ct);

        Query query = _db.Collection("ecg_sessions");
        if (!string.IsNullOrWhiteSpace(selection.UserId))
            query = query.WhereEqualTo("fitbitUserId", selection.UserId);

        query = query.OrderByDescending("collectedAtUtc").Limit(1);
        var results = await query.GetSnapshotAsync(ct);
        return results.Documents.FirstOrDefault();
    }

    private CaptureContext BuildCaptureContextFromSnapshot(DocumentSnapshot doc)
    {
        var data = doc.ToDictionary();
        var userId = data.TryGetValue("fitbitUserId", out var userObj) ? userObj?.ToString() ?? string.Empty : string.Empty;
        if (string.IsNullOrWhiteSpace(userId))
            throw new InvalidOperationException("Stored session missing fitbitUserId.");

        var waveform = WaveformCompressor.Decompress(data.TryGetValue("waveformBlob", out var blobObj) ? blobObj?.ToString() : null);
        if (waveform is null || waveform.Length == 0)
            throw new InvalidOperationException("Stored session missing waveform data.");

        var samplingHz = data.TryGetValue("samplingFrequencyHz", out var samplingObj) ? Convert.ToInt32(samplingObj) : 250;
        var scalingFactor = data.TryGetValue("scalingFactor", out var scalingObj) ? Convert.ToInt32(scalingObj) : 10922;
        var features = TryParseFeatures(data);
        if (features.EmbeddingVector is null || features.EmbeddingVector.Length == 0)
        {
            var embedding = _embedding.GenerateEmbedding(waveform, scalingFactor, samplingHz);
            if (embedding is { Length: > 0 })
                features = features with { EmbeddingVector = embedding };
        }

        var reading = new EcgReading
        {
            StartTime = TryParseDateTimeOffset(data.TryGetValue("ecgStartTime", out var startObj) ? startObj : null),
            SamplingFrequencyHz = samplingHz,
            ScalingFactor = scalingFactor,
            NumberOfWaveformSamples = waveform.Length,
            WaveFormSamples = waveform.ToList()
        };

        var preview = TryParseIntList(data, "waveformPreview") ?? waveform.Take(Math.Min(64, waveform.Length)).ToArray();
        var compressed = data.TryGetValue("waveformBlob", out var compressedObj) ? compressedObj?.ToString() ?? string.Empty : string.Empty;
        if (string.IsNullOrWhiteSpace(compressed))
            compressed = WaveformCompressor.Compress(waveform);

        return new CaptureContext(
            userId,
            reading,
            features,
            TryGetDouble(data, "hrvDailyRmssd"),
            compressed,
            preview,
            samplingHz,
            scalingFactor);
    }

    private async Task<EcgSessionRecord> PersistSessionAsync(
        CaptureContext capture,
        SessionMetadata? metadata,
        IReadOnlyCollection<string>? tags,
        string? notes,
        string dataSource,
        CancellationToken ct,
        string? userIdOverride = null)
    {
        var persistedUserId = string.IsNullOrWhiteSpace(userIdOverride) ? capture.UserId : userIdOverride.Trim();
        if (capture.Reading.StartTime is DateTimeOffset startTime)
        {
            var existing = await TryGetExistingSessionAsync(persistedUserId, startTime, ct);
            if (existing is not null)
                return existing;
        }

        var collectedAtUtc = DateTime.UtcNow;
        var metadataDict = ToMetadataDictionary(metadata);
        var normalizedTags = tags?.Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();

        var payload = new Dictionary<string, object>
        {
            ["fitbitUserId"] = persistedUserId,
            ["dataSource"] = dataSource,
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
            ["tags"] = normalizedTags,
            ["notes"] = notes ?? string.Empty,
            ["embeddingVector"] = capture.Features.EmbeddingVector?.Select(v => (double)v).ToArray()
        };

        var docRef = await _db.Collection("ecg_sessions").AddAsync(payload, ct);
        await _modelState.IncrementSessionCountAsync(1, ct);

        return new EcgSessionRecord(
            docRef.Id,
            persistedUserId,
            dataSource,
            capture.Reading.StartTime,
            capture.Hrv,
            capture.Features,
            metadata,
            capture.WaveformPreview,
            capture.Features.SignalQualityScore,
            capture.Features.MotionArtifactIndex,
            capture.Features.BaselineDriftRatio,
            capture.SamplingHz,
            capture.ScalingFactor,
            normalizedTags,
            notes);
    }

    private async Task<EcgSessionRecord?> TryGetExistingSessionAsync(string userId, DateTimeOffset startTime, CancellationToken ct)
    {
        var query = _db.Collection("ecg_sessions")
            .WhereEqualTo("fitbitUserId", userId)
            .WhereEqualTo("ecgStartTime", startTime.UtcDateTime)
            .Limit(1);
        var snapshot = await query.GetSnapshotAsync(ct);
        var existing = snapshot.Documents.FirstOrDefault();
        return existing is null ? null : ToSessionRecord(existing);
    }

    private async Task<VerificationSummary> ScoreAttemptAsync(
        string userId,
        EcgFeatures features,
        DateTimeOffset? startTime,
        double? hrv,
        double threshold,
        CancellationToken ct,
        IReadOnlyList<EcgSession>? cachedSessions = null,
        IReadOnlyList<EcgSession>? cachedImpostorSessions = null)
    {
        IReadOnlyList<EcgSession> storedSessions;
        if (cachedSessions is null)
        {
            var limit = VerificationBaselineCount > 0 ? VerificationBaselineCount : (int?)null;
            storedSessions = await LoadSessionsForUserAsync(userId, limit, ct, IncludeAutoVerifySessionsInBaseline);
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
        var worstScore = orderedScores.Count > 0 ? orderedScores[^1] : 0d;
        var medianScore = ComputeMedian(scores);
        var passingVotes = scores.Count(s => s >= appliedThreshold);
        var passRatio = scores.Count > 0 ? passingVotes / (double)scores.Count : 0d;
        var latestPasses = latestScore is not null && latestScore.Value >= appliedThreshold;
        var outlierFloor = Math.Max(0d, appliedThreshold - MaxOutlierDrop);
        var noExtremeOutlier = worstScore >= outlierFloor;

        IReadOnlyList<EcgSession> impostorSessions;
        if (cachedImpostorSessions is null)
        {
            impostorSessions = await LoadImpostorSessionsAsync(
                userId,
                ImpostorUsersToSample,
                ImpostorSessionsPerUser,
                ct,
                IncludeAutoVerifySessionsInBaseline);
        }
        else
        {
            impostorSessions = cachedImpostorSessions;
        }

        var impostorScores = new List<double>(impostorSessions.Count);
        foreach (var session in impostorSessions)
        {
            var pair = BuildPair(session, features, hrv ?? 0d);
            var score = EvaluateScore(predictor, corrector, pair);
            impostorScores.Add(score);
        }

        var orderedImpostorScores = impostorScores.OrderByDescending(s => s).ToList();
        var impostorBestScore = orderedImpostorScores.Count > 0 ? orderedImpostorScores[0] : 0d;
        var impostorTopK = Math.Min(ImpostorConsensusTopK, orderedImpostorScores.Count);
        var impostorConsensusScore = impostorTopK > 0
            ? orderedImpostorScores.Take(impostorTopK).Average()
            : 0d;
        var bestSeparation = bestScore - impostorBestScore;
        var consensusSeparation = consensusScore - impostorConsensusScore;
        var separationPasses = orderedImpostorScores.Count == 0 ||
                               (bestSeparation >= MinimumGenuineImpostorMargin &&
                                consensusSeparation >= MinimumGenuineImpostorMargin);

        var authenticated = latestPasses &&
                            bestScore >= appliedThreshold &&
                            consensusScore >= appliedThreshold &&
                            medianScore >= appliedThreshold &&
                            passRatio >= MinimumPassingRatio &&
                            noExtremeOutlier &&
                            separationPasses;

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
            confidence,
            passRatio,
            medianScore,
            worstScore,
            impostorBestScore,
            impostorConsensusScore,
            bestSeparation,
            consensusSeparation);

        return new VerificationSummary(
            result,
            consensusScore,
            passingVotes,
            appliedThreshold,
            latestScore,
            latestPasses,
            confidence,
            passRatio,
            medianScore,
            worstScore,
            outlierFloor,
            separationPasses);
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
            ["passRatio"] = summary.PassRatio,
            ["medianScore"] = summary.MedianScore,
            ["worstScore"] = summary.WorstScore,
            ["outlierFloor"] = summary.OutlierFloor,
            ["consensusScore"] = summary.Result.ConsensusScore,
            ["impostorBestScore"] = summary.Result.ImpostorBestScore,
            ["impostorConsensusScore"] = summary.Result.ImpostorConsensusScore,
            ["bestSeparation"] = summary.Result.BestSeparation,
            ["consensusSeparation"] = summary.Result.ConsensusSeparation,
            ["separationPasses"] = summary.SeparationPasses,
            ["latestScore"] = summary.LatestScore ?? 0d,
            ["latestPasses"] = summary.LatestPasses,
            ["authenticated"] = summary.Result.Authenticated,
            ["comparisonCount"] = summary.Result.ComparisonScores.Count,
            ["confidenceLevel"] = summary.Result.Confidence?.ConfidenceLevel ?? 0d,
            ["confidenceDrift"] = summary.Result.Confidence?.Drift ?? 0d,
            ["confidenceSamples"] = summary.Result.Confidence?.SampleCount ?? 0
        }, ct);
    }

    private async Task<List<EcgSession>> LoadSessionsForUserAsync(
        string userId,
        int? limit = null,
        CancellationToken ct = default,
        bool includeAutoVerify = true)
    {
        var snapshot = await _db.Collection("ecg_sessions")
            .WhereEqualTo("fitbitUserId", userId)
            .GetSnapshotAsync(ct);
        var sessions = new List<EcgSession>();

        foreach (var doc in snapshot.Documents)
        {
            var data = doc.ToDictionary();
            if (!includeAutoVerify && HasTag(data, AutoVerifyTag))
                continue;
            if (IsKnownImpostorSession(data))
                continue;
            var session = TryMapSession(data, userId);
            if (session is not null)
                sessions.Add(session);
        }

        var ordered = sessions
            .OrderByDescending(s => s.CollectedAtUtc ?? DateTimeOffset.MinValue)
            .ToList();

        if (limit is > 0)
            return ordered.Take(limit.Value).ToList();

        return ordered;
    }

    private async Task<List<EcgSession>> LoadImpostorSessionsAsync(
        string claimedUserId,
        int maxUsers,
        int maxSessionsPerUser,
        CancellationToken ct = default,
        bool includeAutoVerify = true)
    {
        if (maxUsers <= 0 || maxSessionsPerUser <= 0)
            return new List<EcgSession>();

        var snapshot = await _db.Collection("ecg_sessions").GetSnapshotAsync(ct);
        var grouped = new Dictionary<string, List<EcgSession>>(StringComparer.OrdinalIgnoreCase);

        foreach (var doc in snapshot.Documents)
        {
            var data = doc.ToDictionary();
            var rawUserId = data.TryGetValue("fitbitUserId", out var userObj) ? userObj?.ToString() : null;
            if (string.IsNullOrWhiteSpace(rawUserId))
                continue;

            var userId = rawUserId.Trim();
            var sameClaimedUser = string.Equals(userId, claimedUserId, StringComparison.OrdinalIgnoreCase);
            var knownImpostorForClaimedUser = sameClaimedUser && IsKnownImpostorSession(data);
            if (sameClaimedUser && !knownImpostorForClaimedUser)
                continue;
            if (!includeAutoVerify && HasTag(data, AutoVerifyTag))
                continue;

            var session = TryMapSession(data, userId);
            if (session is null)
                continue;

            var bucketUserId = knownImpostorForClaimedUser ? $"{userId}#known-impostor" : userId;
            if (!grouped.TryGetValue(bucketUserId, out var sessions))
            {
                sessions = new List<EcgSession>();
                grouped[bucketUserId] = sessions;
            }

            sessions.Add(session);
        }

        return grouped
            .Select(entry => new
            {
                UserId = entry.Key,
                Sessions = entry.Value
                    .OrderByDescending(s => s.CollectedAtUtc ?? DateTimeOffset.MinValue)
                    .Take(maxSessionsPerUser)
                    .ToList()
            })
            .OrderByDescending(entry => entry.Sessions.Count)
            .ThenBy(entry => entry.UserId, StringComparer.OrdinalIgnoreCase)
            .Take(maxUsers)
            .SelectMany(entry => entry.Sessions)
            .ToList();
    }

    private sealed record VerificationSummary(
        VerifyResult Result,
        double MeanScore,
        int VotesPassing,
        double AppliedThreshold,
        double? LatestScore,
        bool LatestPasses,
        ConfidenceSnapshot? Confidence,
        double PassRatio,
        double MedianScore,
        double WorstScore,
        double OutlierFloor,
        bool SeparationPasses);

    private static EcgSession? TryMapSession(Dictionary<string, object> data, string userId)
    {
        var features = TryParseFeatures(data);
        if (!EcgQualityRules.IsAcceptable(features))
            return null;

        var waveform = WaveformCompressor.Decompress(data.TryGetValue("waveformBlob", out var blobObj) ? blobObj?.ToString() : null);
        return new EcgSession
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
        };
    }

    private static bool HasTag(Dictionary<string, object> payload, string tag)
    {
        var tags = TryParseStringList(payload, "tags");
        return tags is not null && tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsKnownImpostorSession(Dictionary<string, object> payload)
    {
        return HasTag(payload, FalseAttemptTag) || HasTag(payload, ImpostorTag);
    }

    private static double ComputeMedian(IReadOnlyList<double> values)
    {
        if (values is null || values.Count == 0)
            return 0d;

        var ordered = values.OrderBy(v => v).ToList();
        var mid = ordered.Count / 2;
        if (ordered.Count % 2 == 0)
            return (ordered[mid - 1] + ordered[mid]) / 2d;

        return ordered[mid];
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

    private async Task<(int SessionCount, int SubjectCount)> GetDatasetStatsAsync(string dataset, CancellationToken ct)
    {
        var snapshot = await _db.Collection("ecg_sessions").GetSnapshotAsync(ct);
        var normalizedTarget = dataset?.Trim().ToLowerInvariant() ?? string.Empty;
        var uniqueUsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sessionCount = 0;

        foreach (var doc in snapshot.Documents)
        {
            var payload = doc.ToDictionary();
            var source = EcgDataSource.Resolve(payload);
            if (!string.Equals(source, normalizedTarget, StringComparison.OrdinalIgnoreCase))
                continue;

            sessionCount++;
            if (payload.TryGetValue("fitbitUserId", out var userObj) && userObj is not null)
            {
                var userId = userObj.ToString();
                if (!string.IsNullOrWhiteSpace(userId))
                    uniqueUsers.Add(userId);
            }
        }

        return (sessionCount, uniqueUsers.Count);
    }

    private static string BuildBenchmarkModelPath(string modelPath, string suffix)
    {
        var directory = Path.GetDirectoryName(modelPath);
        var fileName = Path.GetFileNameWithoutExtension(modelPath);
        var sanitizedSuffix = string.IsNullOrWhiteSpace(suffix) ? "dataset" : suffix.Trim().ToLowerInvariant();
        var benchmarkName = string.IsNullOrWhiteSpace(fileName)
            ? $"ecg_auth_model_{sanitizedSuffix}.zip"
            : $"{fileName}_{sanitizedSuffix}.zip";
        return string.IsNullOrWhiteSpace(directory) ? benchmarkName : Path.Combine(directory, benchmarkName);
    }

    private sealed record TestSelection(string? SessionId, string? UserId)
    {
        public static TestSelection FromAccessToken(string? token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return new TestSelection(null, null);

            var value = token.Trim();
            const string SessionPrefix = "session:";
            const string UserPrefix = "user:";

            if (value.StartsWith(SessionPrefix, StringComparison.OrdinalIgnoreCase))
                return new TestSelection(value[SessionPrefix.Length..], null);

            if (value.StartsWith(UserPrefix, StringComparison.OrdinalIgnoreCase))
                return new TestSelection(null, value[UserPrefix.Length..]);

            return new TestSelection(null, null);
        }
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
            string text when DateTimeOffset.TryParse(text, out var parsed) => parsed,
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

    private EcgSessionRecord ToSessionRecord(DocumentSnapshot doc)
    {
        var data = doc.ToDictionary();
        var userId = data.TryGetValue("fitbitUserId", out var userObj) ? userObj?.ToString() ?? string.Empty : string.Empty;
        var dataSource = EcgDataSource.Resolve(data);
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

        return new EcgSessionRecord(
            doc.Id,
            userId,
            dataSource,
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
            notes);
    }

    private static EcgCollectionOverview BuildCollectionOverview(string name, int count, DateTimeOffset? updatedAtUtc, string summary)
        => new()
        {
            Name = name,
            DocumentCount = count,
            LastUpdatedUtc = updatedAtUtc,
            Summary = summary
        };

    private static EcgVerificationLogPreview MapVerificationLog(DocumentSnapshot doc)
    {
        var payload = doc.ToDictionary();
        return new EcgVerificationLogPreview
        {
            FitbitUserId = TryGetString(payload, "fitbitUserId") ?? string.Empty,
            AttemptedAtUtc = TryParseDateTimeOffset(payload.TryGetValue("attemptedAtUtc", out var attemptedAt) ? attemptedAt : null),
            Authenticated = payload.TryGetValue("authenticated", out var authenticated) && authenticated is not null && Convert.ToBoolean(authenticated),
            Score = TryGetDouble(payload, "score") ?? 0d,
            Threshold = TryGetDouble(payload, "threshold") ?? 0d,
            ConfidenceLevel = TryGetDouble(payload, "confidenceLevel") ?? 0d
        };
    }

    private static VerificationLogRecord MapVerificationLogRecord(DocumentSnapshot doc)
    {
        var payload = doc.ToDictionary();
        return new VerificationLogRecord
        {
            Id = doc.Id,
            FitbitUserId = TryGetString(payload, "fitbitUserId") ?? string.Empty,
            Alias = TryGetString(payload, "alias"),
            AttemptedAtUtc = TryParseDateTimeOffset(payload.TryGetValue("attemptedAtUtc", out var attemptedAt) ? attemptedAt : null),
            EcgStartTimeUtc = TryParseDateTimeOffset(payload.TryGetValue("ecgStartTimeUtc", out var ecgStartAt) ? ecgStartAt : null),
            Score = TryGetDouble(payload, "score") ?? 0d,
            Threshold = TryGetDouble(payload, "threshold") ?? 0d,
            Authenticated = payload.TryGetValue("authenticated", out var authenticated) && authenticated is not null && Convert.ToBoolean(authenticated),
            ConsensusScore = TryGetDouble(payload, "consensusScore") ?? 0d,
            VotesPassing = Convert.ToInt32(TryGetDouble(payload, "votesPassing") ?? 0d),
            ComparisonCount = Convert.ToInt32(TryGetDouble(payload, "comparisonCount") ?? 0d),
            ConfidenceLevel = TryGetDouble(payload, "confidenceLevel") ?? 0d,
            ConfidenceDrift = TryGetDouble(payload, "confidenceDrift") ?? 0d,
            ConfidenceSamples = Convert.ToInt32(TryGetDouble(payload, "confidenceSamples") ?? 0d),
            Label = TryGetString(payload, "label"),
            Notes = TryGetString(payload, "notes")
        };
    }

    private static DateTimeOffset? ExtractLatestTimestamp(IEnumerable<DocumentSnapshot> documents, string fieldName)
    {
        return documents
            .Select(doc =>
            {
                var payload = doc.ToDictionary();
                return TryParseDateTimeOffset(payload.TryGetValue(fieldName, out var value) ? value : null);
            })
            .Where(value => value is not null)
            .Max();
    }

}
