using Google.Cloud.Firestore;
using Microsoft.Extensions.Options;

namespace FitServer.Services;

public interface IConfidenceModelingService
{
    Task<ConfidenceSnapshot> AppendAsync(string userId, double score, double threshold, bool passes, CancellationToken ct = default);
    Task<ConfidenceSnapshot?> GetAsync(string userId, CancellationToken ct = default);
}

public sealed record ConfidenceSnapshot(
    string UserId,
    int SampleCount,
    double RollingMean,
    double RollingStdDev,
    double ExponentialMovingAverage,
    double Drift,
    double ConfidenceLevel,
    int ConsecutivePasses,
    int ConsecutiveFailures,
    DateTimeOffset UpdatedAtUtc);

[FirestoreData]
internal sealed class ConfidenceDocument
{
    [FirestoreProperty("sampleCount")]
    public int SampleCount { get; set; }

    [FirestoreProperty("mean")]
    public double Mean { get; set; }

    [FirestoreProperty("m2")]
    public double M2 { get; set; }

    [FirestoreProperty("ema")]
    public double Ema { get; set; }

    [FirestoreProperty("confidence")]
    public double Confidence { get; set; }

    [FirestoreProperty("drift")]
    public double Drift { get; set; }

    [FirestoreProperty("lastThreshold")]
    public double LastThreshold { get; set; }

    [FirestoreProperty("consecutivePasses")]
    public int ConsecutivePasses { get; set; }

    [FirestoreProperty("consecutiveFailures")]
    public int ConsecutiveFailures { get; set; }

    [FirestoreProperty("updatedAtUtc")]
    public Timestamp? UpdatedAtUtc { get; set; }
}

public sealed class ConfidenceModelingService : IConfidenceModelingService
{
    private const string CollectionName = "ecg_confidence";
    private readonly FirestoreDb _db;
    private readonly IEcgModelStateRepository _modelStateRepository;
    private readonly IOptionsMonitor<AdaptiveModelOptions> _optionsMonitor;

    public ConfidenceModelingService(
        FirestoreDb db,
        IEcgModelStateRepository modelStateRepository,
        IOptionsMonitor<AdaptiveModelOptions> optionsMonitor)
    {
        _db = db;
        _modelStateRepository = modelStateRepository;
        _optionsMonitor = optionsMonitor;
    }

    public async Task<ConfidenceSnapshot> AppendAsync(string userId, double score, double threshold, bool passes, CancellationToken ct = default)
    {
        var docRef = _db.Collection(CollectionName).Document(userId);
        ConfidenceSnapshot? snapshot = null;
        var options = _optionsMonitor.CurrentValue;
        await _db.RunTransactionAsync(async transaction =>
        {
            var current = await GetDocumentAsync(transaction, docRef, ct);
            var updated = UpdateDocument(current, score, threshold, passes, options.ConfidenceEmaAlpha);
            transaction.Set(docRef, updated, SetOptions.MergeAll);
            snapshot = MapSnapshot(userId, updated);
            return 0;
        }, cancellationToken: ct);

        if (options.Enabled)
        {
            if (snapshot!.Drift >= options.ConfidenceDriftTrigger ||
                snapshot.ConfidenceLevel <= options.ConfidenceFloor ||
                snapshot.ConsecutiveFailures >= 3)
            {
                await _modelStateRepository.TryRequestRetrainAsync($"Confidence drift detected for {userId}", ct);
            }
        }

        return snapshot!;
    }

    public async Task<ConfidenceSnapshot?> GetAsync(string userId, CancellationToken ct = default)
    {
        var doc = await _db.Collection(CollectionName).Document(userId).GetSnapshotAsync(ct);
        if (!doc.Exists)
            return null;

        var data = doc.ConvertTo<ConfidenceDocument>();
        return MapSnapshot(userId, data);
    }

    private static ConfidenceDocument UpdateDocument(ConfidenceDocument? existing, double score, double threshold, bool passes, double alpha)
    {
        var doc = existing ?? new ConfidenceDocument();
        alpha = Math.Clamp(alpha, 0.05, 0.8);

        doc.SampleCount++;
        var delta = score - doc.Mean;
        doc.Mean += delta / doc.SampleCount;
        var delta2 = score - doc.Mean;
        doc.M2 += delta * delta2;

        if (doc.SampleCount == 1)
            doc.Ema = score;
        else
            doc.Ema = (1 - alpha) * doc.Ema + alpha * score;

        doc.LastThreshold = threshold;

        var drift = Math.Max(0, threshold - doc.Ema);
        var normalizedDrift = threshold <= 0 ? 0 : Math.Clamp(drift / threshold, 0, 1);
        var confidence = 1 - normalizedDrift;
        if (!passes)
            confidence *= 0.5;

        doc.Drift = normalizedDrift;
        doc.Confidence = confidence;
        if (passes)
        {
            doc.ConsecutivePasses++;
            doc.ConsecutiveFailures = 0;
        }
        else
        {
            doc.ConsecutiveFailures++;
            doc.ConsecutivePasses = 0;
        }

        doc.UpdatedAtUtc = Timestamp.FromDateTime(DateTime.UtcNow);
        return doc;
    }

    private ConfidenceSnapshot MapSnapshot(string userId, ConfidenceDocument doc)
    {
        var variance = doc.SampleCount > 1 ? doc.M2 / (doc.SampleCount - 1) : 0d;
        var stdDev = variance > 0 ? Math.Sqrt(variance) : 0d;
        return new ConfidenceSnapshot(
            userId,
            doc.SampleCount,
            doc.Mean,
            stdDev,
            doc.Ema,
            doc.Drift,
            doc.Confidence,
            doc.ConsecutivePasses,
            doc.ConsecutiveFailures,
            doc.UpdatedAtUtc?.ToDateTime() ?? DateTime.UtcNow);
    }

    private static async Task<ConfidenceDocument?> GetDocumentAsync(Transaction transaction, DocumentReference docRef, CancellationToken ct)
    {
        var snapshot = await transaction.GetSnapshotAsync(docRef, ct);
        return snapshot.Exists ? snapshot.ConvertTo<ConfidenceDocument>() : null;
    }
}
