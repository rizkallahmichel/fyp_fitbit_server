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
    public async Task ContinuousVerify_UsesRequestPayload()
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

        response.EnsureSuccessStatusCode();
        Assert.NotNull(_service.LastContinuousRequest);
        Assert.Equal(20, _service.LastContinuousRequest!.WindowMinutes);
        Assert.Equal(10, _service.LastContinuousRequest.StrideMinutes);
        Assert.Equal(0.8, _service.LastContinuousRequest.Threshold);
    }
}
