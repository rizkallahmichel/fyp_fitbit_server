using System;
using System.Collections.Generic;
using FitServer.Models;
using FitServer.Services;
using FitServer.Tests.TestData;

namespace FitServer.Tests.Infrastructure;

public sealed class FakeEcgAuthService : IEcgAuthService
{
    public SessionCaptureRequest? LastCaptureRequest { get; private set; }
    public ContinuousVerifyRequest? LastContinuousRequest { get; private set; }
    public EcgBenchmarkRequest? LastBenchmarkRequest { get; private set; }
    public FalseAttemptReportRequest? LastFalseAttemptRequest { get; private set; }
    public bool ThrowOnCollect { get; set; }
    public bool ThrowOnVerify { get; set; }

    public Task<CurrentFitbitUserResponse> GetCurrentUserAsync(string accessToken, CancellationToken ct = default)
    {
        return Task.FromResult(new CurrentFitbitUserResponse
        {
            FitbitUserId = "BTNYKG",
            DisplayName = "Michel"
        });
    }

    public Task<EcgSessionRecord> CollectSessionAsync(string accessToken, SessionCaptureRequest? request, CancellationToken ct = default)
    {
        if (ThrowOnCollect)
            throw new InvalidOperationException("Collect failed");

        LastCaptureRequest = request;
        return Task.FromResult(TestDataFactory.CreateSessionRecord());
    }

    public Task<ModelTrainingResult> TrainModelAsync(int maxPairsPerUser, CancellationToken ct = default)
    {
        var result = new ModelTrainingResult("model.zip", "correction.zip", 0.9, 0.9, 0.9, 12, 400);
        return Task.FromResult(result);
    }

    public Task<VerifyResult> VerifyAsync(string accessToken, double threshold, CancellationToken ct = default)
    {
        if (ThrowOnVerify)
            throw new InvalidOperationException("Verification failed");

        return Task.FromResult(TestDataFactory.CreateVerifyResult(score: threshold + 0.05));
    }

    public Task<ContinuousVerifyResponse> VerifyContinuouslyAsync(string accessToken, ContinuousVerifyRequest request, CancellationToken ct = default)
    {
        LastContinuousRequest = request;
        return Task.FromResult(TestDataFactory.CreateContinuousResponse());
    }

    public Task<IReadOnlyList<EcgSessionRecord>> GetSessionsAsync(CancellationToken ct = default)
    {
        IReadOnlyList<EcgSessionRecord> sessions = new[]
        {
            TestDataFactory.CreateSessionRecord()
        };
        return Task.FromResult(sessions);
    }

    public Task<EcgDataOverviewResponse> GetDataOverviewAsync(CancellationToken ct = default)
    {
        var response = new EcgDataOverviewResponse
        {
            Collections =
            {
                new EcgCollectionOverview
                {
                    Name = "ecg_sessions",
                    DocumentCount = 1,
                    Summary = "1 known participant"
                }
            },
            Participants =
            {
                new EcgParticipantOverview
                {
                    FitbitUserId = "user-123",
                    SessionCount = 1,
                    LastSessionAtUtc = DateTimeOffset.UtcNow
                }
            },
            RecentSessions =
            {
                new EcgSessionPreview
                {
                    DocumentId = "session-1",
                    FitbitUserId = "user-123",
                    DataSource = "fitbit",
                    EcgStartTimeUtc = DateTimeOffset.UtcNow,
                    SignalQualityScore = 0.92,
                    Tags = new List<string> { "baseline" }
                }
            },
            RecentVerificationLogs =
            {
                new EcgVerificationLogPreview
                {
                    FitbitUserId = "user-123",
                    AttemptedAtUtc = DateTimeOffset.UtcNow,
                    Authenticated = true,
                    Score = 0.91,
                    Threshold = 0.85,
                    ConfidenceLevel = 0.92
                }
            },
            ModelState = new EcgModelStateOverview
            {
                SessionCount = 1,
                SessionCountAtLastTrain = 1,
                RetrainPending = false,
                LastAccuracy = 0.9,
                LastAreaUnderRocCurve = 0.91,
                LastF1Score = 0.89
            }
        };

        return Task.FromResult(response);
    }

    public Task<EcgBenchmarkResponse> BenchmarkEcgIdAsync(EcgBenchmarkRequest request, CancellationToken ct = default)
    {
        LastBenchmarkRequest = request;
        var response = new EcgBenchmarkResponse
        {
            Dataset = "ecg-id",
            SubjectCount = 90,
            SessionCount = 310,
            TrainFraction = 0.6,
            TestFraction = 0.4,
            Metrics = new ModelTrainingResult("benchmark_model.zip", "benchmark_model_correction.zip", 0.95, 0.99, 0.94, 180, 1200)
        };
        return Task.FromResult(response);
    }

    public Task<FalseAttemptReportResponse> ReportFalseAttemptAsync(FalseAttemptReportRequest request, CancellationToken ct = default)
    {
        LastFalseAttemptRequest = request;
        var response = new FalseAttemptReportResponse
        {
            FitbitUserId = request.FitbitUserId,
            EcgStartTime = request.EcgStartTime,
            SessionDocumentId = "session-1",
            Tags = new[] { "false-attempt", "impostor" },
            RetrainRequested = true
        };
        return Task.FromResult(response);
    }

    public void Reset()
    {
        LastCaptureRequest = null;
        LastContinuousRequest = null;
        LastBenchmarkRequest = null;
        LastFalseAttemptRequest = null;
        ThrowOnCollect = false;
        ThrowOnVerify = false;
    }
}
