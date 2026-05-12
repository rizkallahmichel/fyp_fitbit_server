using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FitServer.Models;
using FitServer.Tests.Infrastructure;

namespace FitServer.Tests.Controllers;

public class EcgAuthControllerTests : IClassFixture<TestApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly FakeEcgAuthService _service;

    public EcgAuthControllerTests(TestApplicationFactory factory)
    {
        _client = factory.CreateClient();
        _service = factory.EcgAuthService;
    }

    [Fact]
    public async Task CollectSession_ReturnsUnauthorized_WhenAccessTokenMissing()
    {
        var response = await _client.PostAsync("/api/ecg-auth/collect-session", new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CollectSession_ReturnsRecord_WhenTokenProvided()
    {
        _service.Reset();
        var request = new SessionCaptureRequest { Notes = "baseline" };
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/ecg-auth/collect-session")
        {
            Content = JsonContent.Create(request)
        };
        httpRequest.Headers.Add("X-Test-AccessToken", "token");

        var response = await _client.SendAsync(httpRequest);

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadAsStringAsync();
        Assert.Contains("session-1", payload);
        Assert.Equal("baseline", _service.LastCaptureRequest?.Notes);
    }

    [Fact]
    public async Task Verify_ReturnsBadRequest_WhenServiceThrows()
    {
        _service.Reset();
        _service.ThrowOnVerify = true;
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/ecg-auth/verify?threshold=0.9");
        request.Headers.Add("X-Test-AccessToken", "token");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CurrentUser_ReturnsConnectedFitbitProfile()
    {
        _service.Reset();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/ecg-auth/current-user");
        request.Headers.Add("X-Test-AccessToken", "token");

        var response = await _client.SendAsync(request);

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadAsStringAsync();
        Assert.Contains("BTNYKG", payload);
        Assert.Contains("Michel", payload);
    }

    [Fact]
    public async Task ContinuousVerify_ReturnsGone()
    {
        _service.Reset();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/ecg-auth/continuous-verify")
        {
            Content = JsonContent.Create(new ContinuousVerifyRequest
            {
                Threshold = 0.8,
                WindowMinutes = 20,
                StrideMinutes = 10
            })
        };
        request.Headers.Add("X-Test-AccessToken", "token");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
    }

    [Fact]
    public async Task BenchmarkEcgId_ReturnsResultAndStoresRequest()
    {
        _service.Reset();
        var response = await _client.PostAsJsonAsync("/api/ecg-auth/benchmark-ecg-id", new EcgBenchmarkRequest
        {
            MaxPairsPerUser = 600,
            TestFraction = 0.4
        });

        response.EnsureSuccessStatusCode();
        Assert.NotNull(_service.LastBenchmarkRequest);
        Assert.Equal(600, _service.LastBenchmarkRequest!.MaxPairsPerUser);
        Assert.Equal(0.4, _service.LastBenchmarkRequest.TestFraction);
    }

    [Fact]
    public async Task DataOverview_ReturnsCollectionSummary()
    {
        _service.Reset();

        var response = await _client.GetAsync("/api/ecg-auth/data-overview");

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadAsStringAsync();
        Assert.Contains("ecg_sessions", payload);
        Assert.Contains("user-123", payload);
    }

    [Fact]
    public async Task ReportFalseAttempt_ReturnsTaggedResponse()
    {
        _service.Reset();
        var timestamp = DateTimeOffset.UtcNow;
        var response = await _client.PostAsJsonAsync("/api/ecg-auth/report-false-attempt", new FalseAttemptReportRequest
        {
            FitbitUserId = "BTNYKG",
            EcgStartTime = timestamp,
            Notes = "known impostor"
        });

        response.EnsureSuccessStatusCode();
        Assert.NotNull(_service.LastFalseAttemptRequest);
        Assert.Equal("BTNYKG", _service.LastFalseAttemptRequest!.FitbitUserId);
        Assert.Equal(timestamp, _service.LastFalseAttemptRequest.EcgStartTime);
    }

    [Fact]
    public async Task UserJourney_CollectTrainVerifyAndLogs_WorksEndToEnd()
    {
        _service.Reset();

        var collectRequest = new HttpRequestMessage(HttpMethod.Post, "/api/ecg-auth/collect-session")
        {
            Content = JsonContent.Create(new SessionCaptureRequest { Notes = "journey" })
        };
        collectRequest.Headers.Add("X-Test-AccessToken", "token");
        var collectResponse = await _client.SendAsync(collectRequest);
        collectResponse.EnsureSuccessStatusCode();

        var trainResponse = await _client.PostAsync("/api/ecg-auth/train?maxPairsPerUser=500", null);
        trainResponse.EnsureSuccessStatusCode();

        var verifyRequest = new HttpRequestMessage(HttpMethod.Post, "/api/ecg-auth/verify?threshold=0.9");
        verifyRequest.Headers.Add("X-Test-AccessToken", "token");
        var verifyResponse = await _client.SendAsync(verifyRequest);
        verifyResponse.EnsureSuccessStatusCode();

        var logsResponse = await _client.GetAsync("/api/ecg-auth/logs");
        logsResponse.EnsureSuccessStatusCode();

        Assert.Equal("journey", _service.LastCaptureRequest?.Notes);
        var verifyPayload = await verifyResponse.Content.ReadAsStringAsync();
        Assert.Contains("authenticated", verifyPayload);
    }
}
