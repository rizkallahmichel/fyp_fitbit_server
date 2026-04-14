using System.Net;
using System.Net.Http;
using FitServer.Services;

namespace FitServer.Tests;

public sealed class FitbitEcgServiceTests
{
    [Fact]
    public async Task GetCurrentUserAsync_ReturnsEncodedIdAndDisplayName()
    {
        var service = new FitbitEcgService(new StubHttpClientFactory("""
        {
          "user": {
            "encodedId": "BTNYKG",
            "displayName": "Michel"
          }
        }
        """));

        var profile = await service.GetCurrentUserAsync("token");

        Assert.Equal("BTNYKG", profile.FitbitUserId);
        Assert.Equal("Michel", profile.DisplayName);
    }

    [Fact]
    public async Task GetFitbitUserIdAsync_ReusesProfileLookup()
    {
        var service = new FitbitEcgService(new StubHttpClientFactory("""
        {
          "user": {
            "encodedId": "BTNYKG",
            "displayName": "Michel"
          }
        }
        """));

        var fitbitUserId = await service.GetFitbitUserIdAsync("token");

        Assert.Equal("BTNYKG", fitbitUserId);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly string _responseBody;

        public StubHttpClientFactory(string responseBody)
        {
            _responseBody = responseBody;
        }

        public HttpClient CreateClient(string name)
        {
            return new HttpClient(new StubHttpMessageHandler(_responseBody))
            {
                BaseAddress = new Uri("https://api.fitbit.com")
            };
        }
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _responseBody;

        public StubHttpMessageHandler(string responseBody)
        {
            _responseBody = responseBody;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseBody)
            });
        }
    }
}
