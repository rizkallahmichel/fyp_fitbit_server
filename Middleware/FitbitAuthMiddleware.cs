using Google.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using System.Net.Http.Headers;
using System.Text.Json;

public class FitbitAuthMiddleware
{
    private readonly RequestDelegate _next;
    private static string? _accessToken;
    private static string? _refreshToken;
    private readonly IConfiguration _configuration;

    public FitbitAuthMiddleware(RequestDelegate next, IConfiguration configuration)
    {
        _next = next;
        _configuration = configuration;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (string.IsNullOrEmpty(_accessToken) || !await IsTokenValid(_accessToken))
        {
            if (!string.IsNullOrEmpty(_refreshToken))
            {
                var refreshed = await TryRefreshTokenAsync(context);
                if (!refreshed)
                {
                    await RequestNewTokenAsync(context);
                }
            }
            else
            {
                await RequestNewTokenAsync(context);
            }
        }

        context.Items["AccessToken"] = _accessToken;
        await _next(context);
    }

    private async Task<bool> IsTokenValid(string token)
    {
        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await httpClient.GetAsync("https://api.fitbit.com/1/user/-/profile.json");
        return response.IsSuccessStatusCode;
    }

    private async Task<bool> TryRefreshTokenAsync(HttpContext context)
    {
        var clientId = _configuration.GetValue<string>("Fitbit:ClientId");
        var clientSecret = _configuration.GetValue<string>("Fitbit:ClientSecret");

        var authHeader = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authHeader);

        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            { "grant_type", "refresh_token" },
            { "refresh_token", _refreshToken ?? string.Empty }
        });

        var response = await client.PostAsync("https://api.fitbit.com/oauth2/token", content);

        if (!response.IsSuccessStatusCode)
            return false;

        var responseData = await response.Content.ReadFromJsonAsync<JsonElement>();
        _accessToken = responseData.GetProperty("access_token").GetString();
        _refreshToken = responseData.GetProperty("refresh_token").GetString();
        context.Items["AccessToken"] = _accessToken;
        context.Items["RefreshToken"] = _refreshToken;
        return true;
    }

    private async Task RequestNewTokenAsync(HttpContext context)
    {
        var clientId = _configuration.GetValue<string>("Fitbit:ClientId");
        var clientSecret = _configuration.GetValue<string>("Fitbit:ClientSecret");
        var code = _configuration.GetValue<string>("Fitbit:Code");
        var codeVerifier = _configuration.GetValue<string>("Fitbit:CodeVerifier");
        var redirectUri = _configuration.GetValue<string>("Fitbit:RedirectUri");

        var authHeader = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authHeader);

        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            { "client_id", clientId },
            { "grant_type", "authorization_code" },
            { "redirect_uri", redirectUri },
            { "code", code },
            { "code_verifier", codeVerifier }
        });

        var response = await client.PostAsync("https://api.fitbit.com/oauth2/token", content);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new Exception($"Unable to request new Fitbit token: {error}");
        }

        var responseData = await response.Content.ReadFromJsonAsync<JsonElement>();
        _accessToken = responseData.GetProperty("access_token").GetString();
        _refreshToken = responseData.GetProperty("refresh_token").GetString();
        context.Items["AccessToken"] = _accessToken;
        context.Items["RefreshToken"] = _refreshToken;
    }
}
