using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using System.Net.Http.Headers;
using System.Text.Json;

namespace FitServer.Controllers
{
    [ApiController]
    [Route("api/fitbit")]
    public class FitbitController : ControllerBase
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;

        public FitbitController(IHttpClientFactory httpClientFactory, IConfiguration configuration)
        {
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
        }

        [HttpGet("hrv")]
        public async Task<IActionResult> GetHRV()
        {
            var accessToken = HttpContext.Items["AccessToken"] as string;
            if (string.IsNullOrWhiteSpace(accessToken))
                return Unauthorized("No access token available.");

            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            using var response = await client.GetAsync($"https://api.fitbit.com/1/user/-/hrv/date/{today}.json");

            if (!response.IsSuccessStatusCode)
                return StatusCode((int)response.StatusCode, await response.Content.ReadAsStringAsync());

            var data = await response.Content.ReadAsStringAsync();
            return Ok(data);
        }

        [HttpGet("all-data")]
        public async Task<IActionResult> GetAllFitbitData()
        {
            var accessToken = HttpContext.Items["AccessToken"] as string;
            if (string.IsNullOrWhiteSpace(accessToken))
                return Unauthorized("No access token available.");

            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var afterDate = today.AddDays(-30);
            var tokenScopes = await TryGetTokenScopesAsync(accessToken);

            var sections = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["profile"] = await FetchSectionSummaryAsync(client, "profile", "https://api.fitbit.com/1/user/-/profile.json"),
                ["ecgList"] = await FetchSectionSummaryAsync(client, "ecgList", $"https://api.fitbit.com/1/user/-/ecg/list.json?afterDate={afterDate:yyyy-MM-dd}&sort=desc&limit=10&offset=0"),
                ["hrv"] = await FetchSectionSummaryAsync(client, "hrv", $"https://api.fitbit.com/1/user/-/hrv/date/{today:yyyy-MM-dd}.json"),
                ["heartDaily"] = await FetchSectionSummaryAsync(client, "heartDaily", $"https://api.fitbit.com/1/user/-/activities/heart/date/{today:yyyy-MM-dd}/1d.json"),
                ["heartIntraday"] = await FetchSectionSummaryAsync(client, "heartIntraday", $"https://api.fitbit.com/1/user/-/activities/heart/date/{today:yyyy-MM-dd}/1d/1sec.json"),
                ["steps"] = await FetchSectionSummaryAsync(client, "steps", $"https://api.fitbit.com/1/user/-/activities/steps/date/{today:yyyy-MM-dd}/1d.json"),
                ["sleep"] = await FetchSectionSummaryAsync(client, "sleep", $"https://api.fitbit.com/1.2/user/-/sleep/date/{today:yyyy-MM-dd}.json"),
                ["breathingRate"] = await FetchSectionSummaryAsync(client, "breathingRate", $"https://api.fitbit.com/1/user/-/br/date/{today:yyyy-MM-dd}/{today:yyyy-MM-dd}.json"),
                ["skinTemperature"] = await FetchSectionSummaryAsync(client, "skinTemperature", $"https://api.fitbit.com/1/user/-/temp/skin/date/{today:yyyy-MM-dd}.json")
            };
            AttachScopeHints(sections, tokenScopes);

            var totalSections = sections.Count;
            var okSections = sections.Values.Count(IsSectionOk);

            return Ok(new
            {
                dateUtc = DateTime.UtcNow,
                source = "fitbit-live-summary",
                auth = new
                {
                    scopes = tokenScopes
                },
                totals = new
                {
                    totalSections,
                    okSections,
                    failedSections = totalSections - okSections
                },
                sections
            });
        }

        private async Task<string[]> TryGetTokenScopesAsync(string accessToken)
        {
            try
            {
                var clientId = _configuration.GetValue<string>("Fitbit:ClientId");
                var clientSecret = _configuration.GetValue<string>("Fitbit:ClientSecret");
                if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
                    return Array.Empty<string>();

                var authHeader = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
                using var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authHeader);
                using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = accessToken });
                using var response = await client.PostAsync("https://api.fitbit.com/1.1/oauth2/introspect", content);
                if (!response.IsSuccessStatusCode)
                    return Array.Empty<string>();

                var payload = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(payload);
                if (!doc.RootElement.TryGetProperty("scope", out var scopeElement) || scopeElement.ValueKind != JsonValueKind.String)
                    return Array.Empty<string>();

                return NormalizeScopes(scopeElement.GetString());
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static string[] NormalizeScopes(string? rawScopes)
        {
            if (string.IsNullOrWhiteSpace(rawScopes))
                return Array.Empty<string>();

            // Fitbit introspection can return either space-separated scopes
            // or a map-like string: "{ELECTROCARDIOGRAM=READ, HEARTRATE=READ, PROFILE=READ}"
            var normalized = rawScopes.Trim().TrimStart('{').TrimEnd('}');
            var mapStyleParts = normalized.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var scopes = new List<string>();

            foreach (var part in mapStyleParts)
            {
                var token = part;
                var eqIdx = token.IndexOf('=');
                if (eqIdx > 0)
                    token = token.Substring(0, eqIdx);

                token = token.Trim().ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(token))
                    scopes.Add(token);
            }

            if (scopes.Count == 0)
            {
                scopes.AddRange(rawScopes
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(s => s.Trim().ToLowerInvariant()));
            }

            return scopes.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static void AttachScopeHints(Dictionary<string, object?> sections, IReadOnlyCollection<string> scopes)
        {
            var requiredScopes = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["profile"] = new[] { "profile" },
                ["ecgList"] = new[] { "electrocardiogram" },
                ["hrv"] = new[] { "heartrate" },
                ["heartDaily"] = new[] { "heartrate" },
                ["heartIntraday"] = new[] { "heartrate" },
                ["steps"] = new[] { "activity" },
                ["sleep"] = new[] { "sleep" },
                ["breathingRate"] = new[] { "respiratory_rate" },
                ["skinTemperature"] = new[] { "temperature" }
            };

            foreach (var key in sections.Keys.ToArray())
            {
                if (!requiredScopes.TryGetValue(key, out var required))
                    continue;

                var missing = required.Where(s => !scopes.Contains(s, StringComparer.OrdinalIgnoreCase)).ToArray();
                if (missing.Length == 0)
                    continue;

                sections[key] = new
                {
                    ok = false,
                    statusCode = 403,
                    error = $"Missing OAuth scope(s): {string.Join(", ", missing)}",
                    requiredScopes = required,
                    grantedScopes = scopes.ToArray()
                };
            }
        }

        private static async Task<object> FetchSectionSummaryAsync(HttpClient client, string sectionName, string url)
        {
            try
            {
                using var response = await client.GetAsync(url);
                var body = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    return new
                    {
                        ok = false,
                        statusCode = (int)response.StatusCode,
                        error = body
                    };
                }

                return new
                {
                    ok = true,
                    statusCode = (int)response.StatusCode,
                    summary = BuildSectionSummary(sectionName, body)
                };
            }
            catch (Exception ex)
            {
                return new
                {
                    ok = false,
                    statusCode = 0,
                    error = ex.Message
                };
            }
        }

        private static object BuildSectionSummary(string sectionName, string rawJson)
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            var rootKeys = root.ValueKind == JsonValueKind.Object
                ? root.EnumerateObject().Select(p => p.Name).ToArray()
                : Array.Empty<string>();

            object details = sectionName switch
            {
                "profile" => BuildProfileSummary(root),
                "ecgList" => BuildEcgSummary(root),
                "hrv" => BuildHrvSummary(root),
                "heartDaily" => BuildHeartDailySummary(root),
                "heartIntraday" => BuildHeartIntradaySummary(root),
                "steps" => BuildStepsSummary(root),
                "sleep" => BuildSleepSummary(root),
                "breathingRate" => BuildBreathingRateSummary(root),
                "skinTemperature" => BuildSkinTemperatureSummary(root),
                _ => new { note = "No section-specific summary rule." }
            };

            return new
            {
                rootKeys,
                details
            };
        }

        private static object BuildProfileSummary(JsonElement root)
        {
            if (!root.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.Object)
                return new { hasUser = false };

            return new
            {
                hasUser = true,
                encodedId = TryGetString(user, "encodedId"),
                displayName = TryGetString(user, "displayName"),
                age = TryGetInt(user, "age"),
                gender = TryGetString(user, "gender")
            };
        }

        private static object BuildEcgSummary(JsonElement root)
        {
            var readings = TryGetArray(root, "ecgReadings");
            var first = readings.FirstOrDefault();
            return new
            {
                readingCount = readings.Length,
                latestStartTime = first.ValueKind == JsonValueKind.Object ? TryGetString(first, "startTime") : null,
                latestAverageHeartRate = first.ValueKind == JsonValueKind.Object ? TryGetInt(first, "averageHeartRate") : null,
                latestResultClassification = first.ValueKind == JsonValueKind.Object ? TryGetString(first, "resultClassification") : null
            };
        }

        private static object BuildHrvSummary(JsonElement root)
        {
            var hrv = TryGetArray(root, "hrv");
            var first = hrv.FirstOrDefault();
            if (first.ValueKind != JsonValueKind.Object)
                return new { recordCount = hrv.Length };

            var value = first.TryGetProperty("value", out var rawValue) && rawValue.ValueKind == JsonValueKind.Object
                ? rawValue
                : default;

            return new
            {
                recordCount = hrv.Length,
                dateTime = TryGetString(first, "dateTime"),
                dailyRmssd = value.ValueKind == JsonValueKind.Object ? TryGetDouble(value, "dailyRmssd") : null,
                deepRmssd = value.ValueKind == JsonValueKind.Object ? TryGetDouble(value, "deepRmssd") : null
            };
        }

        private static object BuildHeartDailySummary(JsonElement root)
        {
            var daily = TryGetArray(root, "activities-heart");
            var first = daily.FirstOrDefault();
            if (first.ValueKind != JsonValueKind.Object)
                return new { recordCount = daily.Length };

            var value = first.TryGetProperty("value", out var rawValue) && rawValue.ValueKind == JsonValueKind.Object
                ? rawValue
                : default;

            return new
            {
                recordCount = daily.Length,
                dateTime = TryGetString(first, "dateTime"),
                restingHeartRate = value.ValueKind == JsonValueKind.Object ? TryGetInt(value, "restingHeartRate") : null
            };
        }

        private static object BuildHeartIntradaySummary(JsonElement root)
        {
            if (!root.TryGetProperty("activities-heart-intraday", out var intraday) || intraday.ValueKind != JsonValueKind.Object)
                return new { hasIntraday = false };

            var dataset = TryGetArray(intraday, "dataset");
            return new
            {
                hasIntraday = true,
                datasetInterval = TryGetInt(intraday, "datasetInterval"),
                datasetType = TryGetString(intraday, "datasetType"),
                sampleCount = dataset.Length,
                latestValue = dataset.Length > 0 ? TryGetInt(dataset[^1], "value") : null
            };
        }

        private static object BuildStepsSummary(JsonElement root)
        {
            var steps = TryGetArray(root, "activities-steps");
            var first = steps.FirstOrDefault();
            return new
            {
                recordCount = steps.Length,
                dateTime = first.ValueKind == JsonValueKind.Object ? TryGetString(first, "dateTime") : null,
                steps = first.ValueKind == JsonValueKind.Object ? TryGetInt(first, "value") : null
            };
        }

        private static object BuildSleepSummary(JsonElement root)
        {
            var sleep = TryGetArray(root, "sleep");
            var first = sleep.FirstOrDefault();
            return new
            {
                recordCount = sleep.Length,
                efficiency = first.ValueKind == JsonValueKind.Object ? TryGetInt(first, "efficiency") : null,
                minutesAsleep = first.ValueKind == JsonValueKind.Object ? TryGetInt(first, "minutesAsleep") : null,
                timeInBed = first.ValueKind == JsonValueKind.Object ? TryGetInt(first, "timeInBed") : null
            };
        }

        private static object BuildBreathingRateSummary(JsonElement root)
        {
            var br = TryGetArray(root, "br");
            var first = br.FirstOrDefault();
            if (first.ValueKind != JsonValueKind.Object)
                return new { recordCount = br.Length };

            var value = first.TryGetProperty("value", out var rawValue) && rawValue.ValueKind == JsonValueKind.Object
                ? rawValue
                : default;

            return new
            {
                recordCount = br.Length,
                dateTime = TryGetString(first, "dateTime"),
                breathingRate = value.ValueKind == JsonValueKind.Object ? TryGetDouble(value, "breathingRate") : null
            };
        }

        private static object BuildSkinTemperatureSummary(JsonElement root)
        {
            var temp = TryGetArray(root, "tempSkin");
            var first = temp.FirstOrDefault();
            if (first.ValueKind != JsonValueKind.Object)
                return new { recordCount = temp.Length };

            var value = first.TryGetProperty("value", out var rawValue) && rawValue.ValueKind == JsonValueKind.Object
                ? rawValue
                : default;

            return new
            {
                recordCount = temp.Length,
                dateTime = TryGetString(first, "dateTime"),
                nightlyRelative = value.ValueKind == JsonValueKind.Object ? TryGetDouble(value, "nightlyRelative") : null
            };
        }

        private static JsonElement[] TryGetArray(JsonElement root, string propertyName)
        {
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty(propertyName, out var value) &&
                value.ValueKind == JsonValueKind.Array)
            {
                return value.EnumerateArray().ToArray();
            }

            return Array.Empty<JsonElement>();
        }

        private static string? TryGetString(JsonElement element, string propertyName)
        {
            if (!TryGetProperty(element, propertyName, out var value))
                return null;
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            };
        }

        private static int? TryGetInt(JsonElement element, string propertyName)
        {
            if (!TryGetProperty(element, propertyName, out var value))
                return null;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
                return number;

            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed))
                return parsed;

            return null;
        }

        private static double? TryGetDouble(JsonElement element, string propertyName)
        {
            if (!TryGetProperty(element, propertyName, out var value))
                return null;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
                return number;

            if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), out var parsed))
                return parsed;

            return null;
        }

        private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
        {
            value = default;
            return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out value);
        }

        private static bool IsSectionOk(object? section)
        {
            if (section is null) return false;
            var prop = section.GetType().GetProperty("ok");
            return prop?.GetValue(section) is bool isOk && isOk;
        }
    }
}
