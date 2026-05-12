using System.Net;
using System.Net.Http;
using System.Text;
using FitServer.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace FitServer.Tests.Controllers;

public class FitbitControllerTests
{
    [Fact]
    public async Task GetHRV_ReturnsUnauthorized_WhenAccessTokenMissing()
    {
        var controller = CreateController(_ => new HttpResponseMessage(HttpStatusCode.OK));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        var response = await controller.GetHRV();

        Assert.IsType<UnauthorizedObjectResult>(response);
    }

    [Fact]
    public async Task GetAllFitbitData_ReturnsSummary_WhenTokenProvided()
    {
        var controller = CreateController(request =>
        {
            var body = request.RequestUri!.AbsoluteUri.Contains("/introspect", StringComparison.OrdinalIgnoreCase)
                ? "{\"scope\":\"profile heartrate electrocardiogram activity sleep respiratory_rate temperature\"}"
                : "{\"ok\":true}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        });

        var httpContext = new DefaultHttpContext();
        httpContext.Items["AccessToken"] = "token";
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };

        var result = await controller.GetAllFitbitData();

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
        Assert.Contains("fitbit-live-summary", ok.Value!.ToString());
    }

    private static FitbitController CreateController(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var factory = new FakeHttpClientFactory(responder);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Fitbit:ClientId"] = "id",
                ["Fitbit:ClientSecret"] = "secret"
            })
            .Build();

        return new FitbitController(factory, config);
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public FakeHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        public HttpClient CreateClient(string name)
        {
            return new HttpClient(new FakeHttpMessageHandler(_responder));
        }
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_responder(request));
        }
    }
}
