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
    public bool ThrowOnCollect { get; set; }
    public bool ThrowOnVerify { get; set; }

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

    public void Reset()
    {
        LastCaptureRequest = null;
        LastContinuousRequest = null;
        ThrowOnCollect = false;
        ThrowOnVerify = false;
    }
}
