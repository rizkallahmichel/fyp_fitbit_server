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
    public async Task<IActionResult> CollectSession(CancellationToken ct)
    {
        var accessToken = HttpContext.Items["AccessToken"] as string;
        if (string.IsNullOrWhiteSpace(accessToken))
            return Unauthorized("No access token found. FitbitAuthMiddleware must run before this endpoint.");

        var record = await _authService.CollectSessionAsync(accessToken, ct);
        return Ok(record);
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

        var result = await _authService.VerifyAsync(accessToken, threshold, ct);
        return Ok(result);
    }
}
