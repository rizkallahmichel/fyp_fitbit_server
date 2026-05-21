using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Grpc.Core;

namespace FitServer.Services;

public sealed class AdaptiveModelSupervisor : BackgroundService
{
    private readonly IEcgMlTrainer _trainer;
    private readonly IEcgModelStateRepository _stateRepository;
    private readonly IWebHostEnvironment _environment;
    private readonly IOptionsMonitor<AdaptiveModelOptions> _options;
    private readonly ILogger<AdaptiveModelSupervisor> _logger;
    private string? _modelPath;

    public AdaptiveModelSupervisor(
        IEcgMlTrainer trainer,
        IEcgModelStateRepository stateRepository,
        IWebHostEnvironment environment,
        IOptionsMonitor<AdaptiveModelOptions> options,
        ILogger<AdaptiveModelSupervisor> logger)
    {
        _trainer = trainer;
        _stateRepository = stateRepository;
        _environment = environment;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var opts = _options.CurrentValue;
            if (!opts.Enabled)
            {
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                continue;
            }

            try
            {
                await EvaluateAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.PermissionDenied)
            {
                _logger.LogError(ex, "Adaptive model supervisor stopped: Firestore permission denied for configured service account.");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Adaptive model evaluation failed; background worker will retry on next interval.");
            }
            var delay = TimeSpan.FromMinutes(Math.Max(1, opts.CheckIntervalMinutes));
            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private async Task EvaluateAsync(CancellationToken ct)
    {
        var state = await _stateRepository.GetAsync(ct);
        var options = _options.CurrentValue;
        var totalSessions = state.SessionCount;
        if (totalSessions == 0)
        {
            await _stateRepository.IncrementSessionCountAsync(0, ct);
            state = await _stateRepository.GetAsync(ct);
            totalSessions = state.SessionCount;
        }

        var needsRetrain = state.RetrainPending;
        if (!needsRetrain)
        {
            var delta = totalSessions - state.SessionCountAtLastTrain;
            if (delta >= options.SessionDeltaThreshold)
                needsRetrain = true;
            else if (state.LastTrainedUtc is null)
                needsRetrain = true;
            else
            {
                var elapsed = DateTimeOffset.UtcNow - state.LastTrainedUtc.Value;
                if (elapsed.TotalMinutes >= options.MinimumMinutesBetweenRetrains)
                    needsRetrain = true;
            }
        }

        if (!needsRetrain)
            return;

        var modelPath = GetModelPath();
        var result = await _trainer.TrainAndSaveAsync(modelPath, options.MaxPairsPerUser, ct);
        var latestCount = await _stateRepository.GetSessionCountAsync(ct);
        await _stateRepository.MarkModelTrainedAsync(result, latestCount, ct);
    }

    private string GetModelPath()
    {
        if (_modelPath is not null)
            return _modelPath;

        _modelPath = Path.Combine(_environment.ContentRootPath, "ecg_auth_model.zip");
        return _modelPath;
    }
}
