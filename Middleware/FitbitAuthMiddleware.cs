using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using System.Net.Http.Headers;
using System.Text.Json;

public class FitbitAuthMiddleware
{
    private readonly RequestDelegate _next;
    private static string? _accessToken = "eyJhbGciOiJIUzI1NiJ9.eyJhdWQiOiIyM1E4Tk4iLCJzdWIiOiJCVE5ZS0ciLCJpc3MiOiJGaXRiaXQiLCJ0eXAiOiJhY2Nlc3NfdG9rZW4iLCJzY29wZXMiOiJyYWN0IHJyZXMgcm94eSByaHIgcnBybyByc2xlIHJ0ZW0iLCJleHAiOjE3NDc1Mjk5OTIsImlhdCI6MTc0NzUwMTE5Mn0.DRo__W5y68mCps0Mdnlk4ZnUvR8bY_k_huvJ8wMEAGQ";
    private static string? _refreshToken;

    public FitbitAuthMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (string.IsNullOrEmpty(_accessToken) || !await IsTokenValid(_accessToken))
        {
            if (!string.IsNullOrEmpty(_refreshToken))
            {
                var refreshed = await TryRefreshTokenAsync();
                if (!refreshed)
                {
                    await RequestNewTokenAsync(); // fallback to full flow
                }
            }
            else
            {
                await RequestNewTokenAsync(); // initial token request
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

    private async Task<bool> TryRefreshTokenAsync()
    {
        var clientId = "23Q8NN";
        var redirectUri = "http://localhost:8080/callback";

        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            { "grant_type", "refresh_token" },
            { "refresh_token", _refreshToken ?? string.Empty },
            { "client_id", clientId }
        });

        using var client = new HttpClient();
        var response = await client.PostAsync("https://api.fitbit.com/oauth2/token", content);

        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        var responseData = await response.Content.ReadFromJsonAsync<JsonElement>();
        _accessToken = responseData.GetProperty("access_token").GetString();
        _refreshToken = responseData.GetProperty("refresh_token").GetString();
        return true;
    }

    private async Task RequestNewTokenAsync()
    {
        var clientId = "23Q8NN";
        var code = "28c00fce3cade29233f8c231b18a2e8784b4fc8f"; 
        var codeVerifier = "81Oli40rwdNtdX6imBH80qtWVF1FHWSaiVJHz6g5O9A";
        var redirectUri = "http://localhost:8080/callback";

        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            { "client_id", clientId },
            { "grant_type", "authorization_code" },
            { "redirect_uri", redirectUri },
            { "code", code },
            { "code_verifier", codeVerifier }
        });

        using var client = new HttpClient();
        var response = await client.PostAsync("https://api.fitbit.com/oauth2/token", content);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new Exception($"Unable to request new Fitbit token: {error}");
        }

        var responseData = await response.Content.ReadFromJsonAsync<JsonElement>();
        _accessToken = responseData.GetProperty("access_token").GetString();
        _refreshToken = responseData.GetProperty("refresh_token").GetString();
    }
}
