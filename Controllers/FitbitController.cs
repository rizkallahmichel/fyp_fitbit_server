using Google.Cloud.Firestore;
using Grpc.Core;
using GrpcStatusCode = Grpc.Core.StatusCode;
using Microsoft.AspNetCore.Mvc;
using System.Net.Http.Headers;
using System.Text.Json;

namespace FitServer.Controllers
{
    [ApiController]
    [Route("api/fitbit")]
    public class FitbitController : ControllerBase
    {
        private readonly FirestoreDb _db;

        public FitbitController(FirestoreDb db)
        {
            _db = db;
        }

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
            try
            {
                var snapshot = await _db.Collection("fitbit_data").GetSnapshotAsync();

                var allData = new List<Dictionary<string, object>>();

                foreach (var doc in snapshot.Documents)
                {
                    var data = doc.ToDictionary();
                    data["date"] = doc.Id;
                    allData.Add(data);
                }

                return Ok(allData);
            }
            catch (RpcException ex) when (ex.StatusCode == GrpcStatusCode.PermissionDenied)
            {
                return base.StatusCode(StatusCodes.Status503ServiceUnavailable,
                    "Firestore permission denied. Grant the configured service account access to the fitbit_data collection.");
            }
            catch (Exception ex)
            {
                return base.StatusCode(StatusCodes.Status500InternalServerError,
                    $"Failed to load Fitbit data: {ex.Message}");
            }
        }

    }
}
