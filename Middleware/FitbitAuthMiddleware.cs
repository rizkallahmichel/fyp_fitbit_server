using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using System.Net.Http.Headers;
using System.Text.Json;

public class FitbitAuthMiddleware
{
    private readonly RequestDelegate _next;
    private static string? _accessToken = "eyJhbGciOiJIUzI1NiJ9.eyJhdWQiOiIyM1E4Tk4iLCJzdWIiOiJCVE5ZS0ciLCJpc3MiOiJGaXRiaXQiLCJ0eXAiOiJhY2Nlc3NfdG9rZW4iLCJzY29wZXMiOiJyYWN0IHJyZXMgcm94eSByaHIgcnBybyBydGVtIHJzbGUiLCJleHAiOjE3NDY0NzYwNTYsImlhdCI6MTc0NjQ0NzI1Nn0.Rdn_T7f-F0tikRxV72qWTlY5pakIZFb_mDD__KKP20k";
    //private static string? _accessToken;
    private static string? _refreshToken;

    public FitbitAuthMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (string.IsNullOrEmpty(_accessToken) || !await IsTokenValid(_accessToken))
        {
            await RefreshTokenAsync();
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

    private async Task RefreshTokenAsync()
    {
        var clientId = "23Q8NN";
        var code = "aa83ee957e458f1a1fb2d30b5ea02e11295d2993";
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

        // DO NOT set the Authorization header for PKCE
        // client.DefaultRequestHeaders.Authorization = ...

        var response = await client.PostAsync("https://api.fitbit.com/oauth2/token", content);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new Exception($"Unable to refresh Fitbit token: {error}");
        }

        var responseData = await response.Content.ReadFromJsonAsync<JsonElement>();
        _accessToken = responseData.GetProperty("access_token").GetString();
        _refreshToken = responseData.GetProperty("refresh_token").GetString();
    }
}
