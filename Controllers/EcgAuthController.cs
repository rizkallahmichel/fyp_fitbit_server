using FitServer.Models;
using FitServer.Services;
using Microsoft.AspNetCore.Mvc;

namespace FitServer.Controllers;

[ApiController]
[Route("api/ecg-auth")]
public sealed class EcgAuthController : ControllerBase
{
    private readonly IEcgAuthService _authService;

    public EcgAuthController(IEcgAuthService authService)
    {
        _authService = authService;
    }

    [HttpPost("collect-session")]
    public async Task<IActionResult> CollectSession([FromBody] SessionCaptureRequest? request, CancellationToken ct)
    {
        var accessToken = HttpContext.Items["AccessToken"] as string;
        if (string.IsNullOrWhiteSpace(accessToken))
            return Unauthorized("No access token found. FitbitAuthMiddleware must run before this endpoint.");

        try
        {
            var record = await _authService.CollectSessionAsync(accessToken, request, ct);
            return Ok(record);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpPost("train")]
    public async Task<IActionResult> Train([FromQuery] int maxPairsPerUser = 500, CancellationToken ct = default)
    {
        var result = await _authService.TrainModelAsync(maxPairsPerUser, ct);
        return Ok(result);
    }

    [HttpPost("verify")]
    public async Task<IActionResult> Verify([FromQuery] double threshold = 0, CancellationToken ct = default)
    {
        var accessToken = HttpContext.Items["AccessToken"] as string;
        if (string.IsNullOrWhiteSpace(accessToken))
            return Unauthorized("No access token found. FitbitAuthMiddleware must run before this endpoint.");

        try
        {
            var result = await _authService.VerifyAsync(accessToken, threshold, ct);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpGet("current-user")]
    public async Task<IActionResult> GetCurrentUser(CancellationToken ct = default)
    {
        var accessToken = HttpContext.Items["AccessToken"] as string;
        if (string.IsNullOrWhiteSpace(accessToken))
            return Unauthorized("No access token found. FitbitAuthMiddleware must run before this endpoint.");

        try
        {
            var user = await _authService.GetCurrentUserAsync(accessToken, ct);
            return Ok(user);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpPost("continuous-verify")]
    public async Task<IActionResult> ContinuousVerify([FromBody] ContinuousVerifyRequest? request, CancellationToken ct = default)
    {
        var accessToken = HttpContext.Items["AccessToken"] as string;
        if (string.IsNullOrWhiteSpace(accessToken))
            return Unauthorized("No access token found. FitbitAuthMiddleware must run before this endpoint.");

        try
        {
            var payload = request ?? new ContinuousVerifyRequest();
            var response = await _authService.VerifyContinuouslyAsync(accessToken, payload, ct);
            return Ok(response);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpGet("sessions")]
    public async Task<IActionResult> GetSessions(CancellationToken ct)
    {
        var sessions = await _authService.GetSessionsAsync(ct);
        return Ok(sessions);
    }

    [HttpGet("logs")]
    public async Task<IActionResult> GetLogs([FromQuery] string? fitbitUserId = null, [FromQuery] int limit = 100, CancellationToken ct = default)
    {
        var logs = await _authService.GetVerificationLogsAsync(fitbitUserId, limit, ct);
        return Ok(logs);
    }

    [HttpGet("data-overview")]
    public async Task<IActionResult> GetDataOverview(CancellationToken ct)
    {
        var overview = await _authService.GetDataOverviewAsync(ct);
        return Ok(overview);
    }

    [HttpPost("benchmark-ecg-id")]
    public async Task<IActionResult> BenchmarkEcgId([FromBody] EcgBenchmarkRequest? request, CancellationToken ct = default)
    {
        var payload = request ?? new EcgBenchmarkRequest();
        var result = await _authService.BenchmarkEcgIdAsync(payload, ct);
        return Ok(result);
    }

    [HttpPost("report-false-attempt")]
    public async Task<IActionResult> ReportFalseAttempt([FromBody] FalseAttemptReportRequest? request, CancellationToken ct = default)
    {
        try
        {
            var payload = request ?? new FalseAttemptReportRequest();
            var response = await _authService.ReportFalseAttemptAsync(payload, ct);
            return Ok(response);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }
}
