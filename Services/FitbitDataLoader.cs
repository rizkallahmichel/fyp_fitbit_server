using System.Net.Http.Headers;
using System.Text.Json;

public class FitbitDataLoader : IHostedService
{
    private readonly IServiceProvider _serviceProvider;

    public FitbitDataLoader(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var firebaseService = scope.ServiceProvider.GetRequiredService<FirebaseService>();

        var accessToken = "eyJhbGciOiJIUzI1NiJ9.eyJhdWQiOiIyM1E4Tk4iLCJzdWIiOiJCVE5ZS0ciLCJpc3MiOiJGaXRiaXQiLCJ0eXAiOiJhY2Nlc3NfdG9rZW4iLCJzY29wZXMiOiJyc29jIHJlY2cgcnNldCByaXJuIHJveHkgcnBybyBybnV0IHJzbGUgcmNmIHJhY3QgcmxvYyBycmVzIHJ3ZWkgcmhyIHJ0ZW0iLCJleHAiOjE3NDgwNjQ0OTYsImlhdCI6MTc0ODAzNTY5Nn0.OuVWnjz9EkqrDQ1tCtn_yv9TT-CyKkwsiBze2sBn0dk"; 

        var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        string today = DateTime.UtcNow.ToString("yyyy-MM-dd");

        async Task<object?> FetchPrimitive(string url, Func<JsonElement, object?> extract)
        {
            try
            {
                var res = await httpClient.GetAsync(url);
                var content = await res.Content.ReadAsStringAsync();

                if (!res.IsSuccessStatusCode)
                {
                    Console.WriteLine($"Failed to fetch from {url}, Status: {res.StatusCode}");
                    return null;
                }

                var doc = JsonDocument.Parse(content);
                return extract(doc.RootElement);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching {url}: {ex.Message}");
                return null;
            }
        }

        var data = new Dictionary<string, object?>();

        data["steps"] = await FetchPrimitive(
            $"https://api.fitbit.com/1/user/-/activities/steps/date/{today}/1d.json",
            root =>
            {
                var stepsArray = root.GetProperty("activities-steps");
                return stepsArray.GetArrayLength() > 0 ? int.Parse(stepsArray[0].GetProperty("value").GetString() ?? "0") : null;
            });

        data["sleepScore"] = await FetchPrimitive(
            $"https://api.fitbit.com/1.2/user/-/sleep/date/{today}.json",
            root =>
            {
                var sleepArray = root.GetProperty("sleep");
                return sleepArray.GetArrayLength() > 0 ? sleepArray[0].GetProperty("efficiency").GetInt32() : null;
            });

        data["hrv"] = await FetchPrimitive(
            $"https://api.fitbit.com/1/user/-/hrv/date/{today}.json",
            root =>
            {
                var hrvArray = root.GetProperty("hrv");
                return hrvArray.GetArrayLength() > 0 ? hrvArray[0].GetProperty("value").GetProperty("dailyRmssd").GetDouble() : null;
            });

        data["rhr"] = await FetchPrimitive(
            $"https://api.fitbit.com/1/user/-/activities/heart/date/{today}/1d.json",
            root =>
            {
                var hrArray = root.GetProperty("activities-heart");
                return hrArray.GetArrayLength() > 0 && hrArray[0].GetProperty("value").TryGetProperty("restingHeartRate", out var rhr)
                    ? rhr.GetInt32()
                    : null;
            });

        data["skinTemperature"] = await FetchPrimitive(
            $"https://api.fitbit.com/1/user/-/temp/skin/date/{today}.json",
            root =>
            {
                var tempArray = root.GetProperty("tempSkin");
                return tempArray.GetArrayLength() > 0
                    ? tempArray[0].GetProperty("value").GetProperty("nightlyRelative").GetDouble()
                    : null;
            });

        data["heartrate"] = await FetchPrimitive(
            $"https://api.fitbit.com/1/user/-/activities/heart/date/{today}/1d/1sec.json",
            root =>
            {
                var intraday = root.GetProperty("activities-heart-intraday").GetProperty("dataset");
                return intraday.GetArrayLength() > 1 ? intraday[1].GetProperty("value").GetInt32() : null;
            });

        data["breathingRate"] = await FetchPrimitive(
            $"https://api.fitbit.com/1/user/-/br/date/{today}/{today}.json",
            root =>
            {
                var brArray = root.GetProperty("br");
                return brArray.GetArrayLength() > 0
                    ? brArray[0].GetProperty("value").GetProperty("breathingRate").GetDouble()
                    : null;
            });

        // Remove nulls
        var cleanData = data.Where(kvp => kvp.Value != null)
                            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value!);

        await firebaseService.SaveOrUpdateFitbitDataAsync(today, cleanData);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
