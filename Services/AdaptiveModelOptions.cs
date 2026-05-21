namespace FitServer.Services;

public sealed class AdaptiveModelOptions
{
    public bool Enabled { get; set; } = true;
    public int CheckIntervalMinutes { get; set; } = 15;
    public int SessionDeltaThreshold { get; set; } = 30;
    public int MinimumMinutesBetweenRetrains { get; set; } = 240;
    public int MaxPairsPerUser { get; set; } = 500;
    public double ConfidenceDriftTrigger { get; set; } = 0.2;
    public double ConfidenceFloor { get; set; } = 0.5;
    public double ConfidenceEmaAlpha { get; set; } = 0.2;
}
