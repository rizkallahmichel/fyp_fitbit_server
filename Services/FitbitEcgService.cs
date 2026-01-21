using System.Net.Http.Headers;
using System.Text.Json;
using FitServer.Models;

namespace FitServer.Services;

public interface IFitbitEcgService
{
    Task<EcgReading?> GetLatestEcgAsync(string accessToken, DateOnly? afterDate = null, CancellationToken ct = default);
    Task<double?> GetDailyHrvAsync(string accessToken, DateOnly date, CancellationToken ct = default);
    Task<string> GetFitbitUserIdAsync(string accessToken, CancellationToken ct = default);
}

public sealed class FitbitEcgService : IFitbitEcgService
{
    private readonly IHttpClientFactory _httpClientFactory;

    public FitbitEcgService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<EcgReading?> GetLatestEcgAsync(string accessToken, DateOnly? afterDate = null, CancellationToken ct = default)
    {
        var date = (afterDate ?? DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(-1))).ToString("yyyy-MM-dd");
        var url = $"https://api.fitbit.com/1/user/-/ecg/list.json?afterDate={date}&sort=desc&limit=1&offset=0";
        var client = CreateClient(accessToken);

        using var response = await client.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Fitbit ECG fetch failed ({(int)response.StatusCode}): {body}");
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        var parsed = JsonSerializer.Deserialize<EcgLogListResponse>(json);
        if (parsed?.EcgReadings != null && parsed.EcgReadings.Count > 0)
            return parsed.EcgReadings[0];

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        foreach (var propName in new[] { "ecgReadings", "ecg", "readings" })
        {
            if (root.TryGetProperty(propName, out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                var first = arr.EnumerateArray().FirstOrDefault();
                if (first.ValueKind == JsonValueKind.Object)
                    return JsonSerializer.Deserialize<EcgReading>(first.GetRawText());
            }
        }

        return null;
    }

    public async Task<double?> GetDailyHrvAsync(string accessToken, DateOnly date, CancellationToken ct = default)
    {
        var url = $"https://api.fitbit.com/1/user/-/hrv/date/{date:yyyy-MM-dd}.json";
        var client = CreateClient(accessToken);

        using var response = await client.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
            return null;

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("hrv", out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
            return null;

        var first = arr[0];
        if (!first.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object)
            return null;

        return value.TryGetProperty("dailyRmssd", out var rmssd) && rmssd.ValueKind == JsonValueKind.Number
            ? rmssd.GetDouble()
            : null;
    }

    public async Task<string> GetFitbitUserIdAsync(string accessToken, CancellationToken ct = default)
    {
        var client = CreateClient(accessToken);
        using var response = await client.GetAsync("https://api.fitbit.com/1/user/-/profile.json", ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var profile = JsonSerializer.Deserialize<FitbitProfileResponse>(json);
        var id = profile?.User?.EncodedId;
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException("Unable to resolve Fitbit encodedId.");

        return id!;
    }

    private HttpClient CreateClient(string accessToken)
    {
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }
}
