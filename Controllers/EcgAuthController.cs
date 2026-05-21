using FitServer.Models;
using FitServer.Services;
using Grpc.Core;
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
        catch (RpcException ex) when (IsGoogleCredentialFailure(ex))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                "Firestore authentication failed. Check Google service-account credentials (invalid JWT signature).");
        }
        catch (RpcException ex) when (IsFirestorePermissionDenied(ex))
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                "Firestore permission denied. Verify the active service account has Firestore access in the configured Google project.");
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
        catch (RpcException ex) when (IsGoogleCredentialFailure(ex))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                "Firestore authentication failed. Check Google service-account credentials (invalid JWT signature).");
        }
        catch (RpcException ex) when (IsFirestorePermissionDenied(ex))
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                "Firestore permission denied. Verify the active service account has Firestore access in the configured Google project.");
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
        await Task.CompletedTask;
        return StatusCode(StatusCodes.Status410Gone, "continuous-verify has been removed from the workflow. Use collect-session then verify.");
    }

    [HttpGet("sessions")]
    public async Task<IActionResult> GetSessions(CancellationToken ct)
    {
        try
        {
            var sessions = await _authService.GetSessionsAsync(ct);
            return Ok(sessions);
        }
        catch (RpcException ex) when (IsGoogleCredentialFailure(ex))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                "Firestore authentication failed. Check Google service-account credentials (invalid JWT signature).");
        }
        catch (RpcException ex) when (IsFirestorePermissionDenied(ex))
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                "Firestore permission denied. Verify the active service account has Firestore access in the configured Google project.");
        }
    }

    [HttpGet("logs")]
    public async Task<IActionResult> GetLogs([FromQuery] string? fitbitUserId = null, [FromQuery] int limit = 100, CancellationToken ct = default)
    {
        try
        {
            var logs = await _authService.GetVerificationLogsAsync(fitbitUserId, limit, ct);
            return Ok(logs);
        }
        catch (RpcException ex) when (IsGoogleCredentialFailure(ex))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                "Firestore authentication failed. Check Google service-account credentials (invalid JWT signature).");
        }
        catch (RpcException ex) when (IsFirestorePermissionDenied(ex))
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                "Firestore permission denied. Verify the active service account has Firestore access in the configured Google project.");
        }
    }

    [HttpGet("data-overview")]
    public async Task<IActionResult> GetDataOverview(CancellationToken ct)
    {
        try
        {
            var overview = await _authService.GetDataOverviewAsync(ct);
            return Ok(overview);
        }
        catch (RpcException ex) when (IsGoogleCredentialFailure(ex))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                "Firestore authentication failed. Check Google service-account credentials (invalid JWT signature).");
        }
        catch (RpcException ex) when (IsFirestorePermissionDenied(ex))
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                "Firestore permission denied. Verify the active service account has Firestore access in the configured Google project.");
        }
    }

    [HttpPost("benchmark-ecg-id")]
    public async Task<IActionResult> BenchmarkEcgId([FromBody] EcgBenchmarkRequest? request, CancellationToken ct = default)
    {
        try
        {
            var payload = request ?? new EcgBenchmarkRequest();
            var result = await _authService.BenchmarkEcgIdAsync(payload, ct);
            return Ok(result);
        }
        catch (RpcException ex) when (IsGoogleCredentialFailure(ex))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                "Firestore authentication failed. Check Google service-account credentials (invalid JWT signature).");
        }
        catch (RpcException ex) when (IsFirestorePermissionDenied(ex))
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                "Firestore permission denied. Verify the active service account has Firestore access in the configured Google project.");
        }
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
        catch (RpcException ex) when (IsGoogleCredentialFailure(ex))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                "Firestore authentication failed. Check Google service-account credentials (invalid JWT signature).");
        }
    }

    private static bool IsGoogleCredentialFailure(RpcException ex)
    {
        return ex.StatusCode == Grpc.Core.StatusCode.Internal &&
               ex.Status.Detail?.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool IsFirestorePermissionDenied(RpcException ex)
    {
        return ex.StatusCode == Grpc.Core.StatusCode.PermissionDenied ||
               ex.Status.Detail?.Contains("Missing or insufficient permissions", StringComparison.OrdinalIgnoreCase) == true;
    }
}
