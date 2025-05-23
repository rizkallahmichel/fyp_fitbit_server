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
        var clientId = _configuration.GetValue<string>("Fitbit:ClientId");
        var redirectUri = _configuration.GetValue<string>("Fitbit:RedirectUri");

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
        var clientId = _configuration.GetValue<string>("Fitbit:ClientId");
        var code = _configuration.GetValue<string>("Fitbit:Code");
        var codeVerifier = _configuration.GetValue<string>("Fitbit:CodeVerifier");
        var redirectUri = _configuration.GetValue<string>("Fitbit:RedirectUri");

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
