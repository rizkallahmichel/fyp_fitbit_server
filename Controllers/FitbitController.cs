using Google.Cloud.Firestore;
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


        [HttpGet("all-data")]
        public async Task<IActionResult> GetAllFitbitData()
        {
            var db = FirestoreDb.Create("fyp-assistant-7a216");
            var snapshot = await db.Collection("fitbit_data").GetSnapshotAsync();

            var allData = new List<Dictionary<string, object>>();

            foreach (var doc in snapshot.Documents)
            {
                var data = doc.ToDictionary();
                data["date"] = doc.Id; 
                allData.Add(data);
            }

            return Ok(allData);
        }

    }
}