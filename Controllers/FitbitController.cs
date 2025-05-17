using Microsoft.AspNetCore.Mvc;
using System.Net.Http.Headers;
using System.Text.Json;

namespace FitServer.Controllers
{
    [ApiController]
    [Route("api/fitbit")]
    public class FitbitController : ControllerBase
    {
        private async Task<IActionResult> FetchFitbitData(string endpoint)
        {
            var accessToken = HttpContext.Items["AccessToken"] as string;
            if (string.IsNullOrEmpty(accessToken))
                return Unauthorized("No access token available.");

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var response = await httpClient.GetAsync(endpoint);
            if (!response.IsSuccessStatusCode)
                return StatusCode((int)response.StatusCode, await response.Content.ReadAsStringAsync());

            var data = await response.Content.ReadAsStringAsync();
            return Ok(data);
        }


        [HttpGet("heart-rate/intraday")]
        public async Task<IActionResult> GetIntradayHeartRate()
        {
            var accessToken = HttpContext.Items["AccessToken"] as string;
            if (string.IsNullOrEmpty(accessToken))
                return Unauthorized("No access token available.");

            string today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            string url = $"https://api.fitbit.com/1/user/-/activities/heart/date/{today}/1d/1sec.json";

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var response = await httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return StatusCode((int)response.StatusCode, await response.Content.ReadAsStringAsync());

            var json = await response.Content.ReadAsStringAsync();

            List<object> fiveValues;

            using (var document = JsonDocument.Parse(json))
            {
                var root = document.RootElement;
                if (!root.TryGetProperty("activities-heart-intraday", out var intraday) ||
                    !intraday.TryGetProperty("dataset", out var dataset))
                {
                    return NotFound("Intraday heart rate dataset not found.");
                }

                fiveValues = dataset.EnumerateArray().Take(5).Select(item => new
                {
                    time = item.GetProperty("time").GetString(),
                    value = item.GetProperty("value").GetInt32()
                }).Cast<object>().ToList();
            }

            return Ok(fiveValues);
        }



        [HttpPost("heart-rate")]
        public async Task<IActionResult> GetHeartRate()
        {
            return await FetchFitbitData("https://api.fitbit.com/1/user/-/activities/heart/date/today/1d.json");
        }

        [HttpGet("sleep")]
        public async Task<IActionResult> GetSleep()
        {
            var accessToken = HttpContext.Items["AccessToken"] as string;
            if (string.IsNullOrEmpty(accessToken))
                return Unauthorized("No access token available.");

            // Get today's date in UTC (Fitbit uses UTC-based logs)
            string formattedDate = DateTime.UtcNow.ToString("yyyy-MM-dd");

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var response = await httpClient.GetAsync($"https://api.fitbit.com/1.2/user/-/sleep/date/{formattedDate}.json");

            if (!response.IsSuccessStatusCode)
                return StatusCode((int)response.StatusCode, await response.Content.ReadAsStringAsync());

            var data = await response.Content.ReadAsStringAsync();
            return Ok(data);
        }


        // [HttpGet("stress-management")]
        // public async Task<IActionResult> GetStressManagement()
        // {
        //     var accessToken = HttpContext.Items["AccessToken"] as string;
        //     if (string.IsNullOrEmpty(accessToken))
        //         return Unauthorized("No access token available.");

        //     using var httpClient = new HttpClient();
        //     httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        //     var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        //     var response = await httpClient.GetAsync($"https://api.fitbit.com/1/user/-/stressManagement/date/{today}.json");

        //     if (!response.IsSuccessStatusCode)
        //         return StatusCode((int)response.StatusCode, await response.Content.ReadAsStringAsync());

        //     var data = await response.Content.ReadAsStringAsync();
        //     return Ok(data);
        // }

        [HttpGet("hrv")]
        public async Task<IActionResult> GetHRV()
        {
            var accessToken = HttpContext.Items["AccessToken"] as string;
            if (string.IsNullOrEmpty(accessToken))
                return Unauthorized("No access token available.");

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var response = await httpClient.GetAsync($"https://api.fitbit.com/1/user/-/hrv/date/{today}.json");

            if (!response.IsSuccessStatusCode)
                return StatusCode((int)response.StatusCode, await response.Content.ReadAsStringAsync());

            var data = await response.Content.ReadAsStringAsync();
            return Ok(data);
        }

        /*[HttpGet("eda")]
        public async Task<IActionResult> GetEDA()
        {
            var accessToken = HttpContext.Items["AccessToken"] as string;
            if (string.IsNullOrEmpty(accessToken))
                return Unauthorized("No access token available.");

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var url = $"https://api.fitbit.com/1/user/-/eda/date/{today}.json";
            var response = await httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
                return StatusCode((int)response.StatusCode, await response.Content.ReadAsStringAsync());

            var data = await response.Content.ReadAsStringAsync();
            return Ok(data);
        }*/


        /*[HttpPost("hrv")]
        public async Task<IActionResult> GetHRV()
        {
            return await FetchFitbitData("https://api.fitbit.com/1/user/-/hrv/date/today.json");
        }
*/
        [HttpPost("resting-heart-rate")]
        public async Task<IActionResult> GetRestingHeartRate()
        {
            return await FetchFitbitData("https://api.fitbit.com/1/user/-/activities/heart/date/today/1d.json");
        }

        /*[HttpGet("mindfulness")]
        public async Task<IActionResult> GetMindfulness()
        {
            var accessToken = HttpContext.Items["AccessToken"] as string;
            if (string.IsNullOrEmpty(accessToken))
                return Unauthorized("No access token available.");

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var url = $"https://api.fitbit.com/1/user/-/mindfulness/date/{today}.json";
            var response = await httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
                return StatusCode((int)response.StatusCode, await response.Content.ReadAsStringAsync());

            var data = await response.Content.ReadAsStringAsync();
            return Ok(data);
        }*/


        [HttpGet("activity")]
        public async Task<IActionResult> GetActivity()
        {
            var accessToken = HttpContext.Items["AccessToken"] as string;
            if (string.IsNullOrEmpty(accessToken))
                return Unauthorized("No access token available.");

            var today = DateTime.UtcNow.ToString("yyyy-MM-dd"); // Correct format
            var url = $"https://api.fitbit.com/1/user/-/activities/date/{today}.json";

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var response = await httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
                return StatusCode((int)response.StatusCode, await response.Content.ReadAsStringAsync());

            var data = await response.Content.ReadAsStringAsync();
            return Ok(data);
        }


        [HttpPost("breathing-rate")]
        public async Task<IActionResult> GetBreathingRate()
        {
            var accessToken = HttpContext.Items["AccessToken"] as string;
            if (string.IsNullOrEmpty(accessToken))
                return Unauthorized("No access token available.");

            // Adjust these dates as needed (must be within the range Fitbit has data for)
            string today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            string url = $"https://api.fitbit.com/1/user/-/br/date/{today}/{today}.json";

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var response = await httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return StatusCode((int)response.StatusCode, await response.Content.ReadAsStringAsync());

            var json = await response.Content.ReadAsStringAsync();

            // Optional: check if array is empty
            var document = JsonDocument.Parse(json);
            var brArray = document.RootElement.GetProperty("br");
            if (brArray.GetArrayLength() == 0)
            {
                return Ok("No breathing rate data available for the selected date range.");
            }

            return Ok(json);
        }

        [HttpGet("temperature")]
        public async Task<IActionResult> GetTemperature()
        {
            var accessToken = HttpContext.Items["AccessToken"] as string;
            if (string.IsNullOrEmpty(accessToken))
                return Unauthorized("No access token available.");

            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var url = $"https://api.fitbit.com/1/user/-/temp/skin/date/{today}.json";

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var response = await httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return StatusCode((int)response.StatusCode, await response.Content.ReadAsStringAsync());

            var json = await response.Content.ReadAsStringAsync();

            // Optional: handle case where temperature data may be missing
            var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("tempSkin", out var tempArray) && tempArray.GetArrayLength() == 0)
            {
                return Ok("No temperature data available for the selected date.");
            }

            return Ok(json);
        }

    }
}