using Google.Cloud.Firestore;

namespace FitServer.Services;

public interface IEcgModelStateRepository
{
    Task<ModelStateSnapshot> GetAsync(CancellationToken ct = default);
    Task IncrementSessionCountAsync(int delta, CancellationToken ct = default);
    Task MarkModelTrainedAsync(ModelTrainingResult result, int totalSessions, CancellationToken ct = default);
    Task<bool> TryRequestRetrainAsync(string reason, CancellationToken ct = default);
    Task<int> GetSessionCountAsync(CancellationToken ct = default);
}

public sealed record ModelStateSnapshot(
    DateTimeOffset? LastTrainedUtc,
    int SessionCount,
    int SessionCountAtLastTrain,
    bool RetrainPending,
    string? RetrainReason,
    double? LastAccuracy,
    double? LastAreaUnderRocCurve,
    double? LastF1Score);

public sealed class EcgModelStateRepository : IEcgModelStateRepository
{
    private const string CollectionName = "ecg_model_state";
    private const string DocumentId = "current";
    private readonly FirestoreDb _db;

    public EcgModelStateRepository(FirestoreDb db)
    {
        _db = db;
    }

    public async Task<ModelStateSnapshot> GetAsync(CancellationToken ct = default)
    {
        var doc = await GetDocumentAsync(ct);
        if (doc is null || !doc.Exists)
            return new ModelStateSnapshot(null, 0, 0, false, null, null, null, null);

        var data = doc.ToDictionary();
        return new ModelStateSnapshot(
            TryGetDateTime(data, "lastTrainedUtc"),
            TryGetInt(data, "sessionCount"),
            TryGetInt(data, "sessionCountAtLastTrain"),
            data.TryGetValue("retrainPending", out var pendingObj) && Convert.ToBoolean(pendingObj),
            data.TryGetValue("retrainReason", out var reasonObj) ? reasonObj?.ToString() : null,
            TryGetDouble(data, "lastAccuracy"),
            TryGetDouble(data, "lastAreaUnderRocCurve"),
            TryGetDouble(data, "lastF1Score"));
    }

    public async Task IncrementSessionCountAsync(int delta, CancellationToken ct = default)
    {
        if (delta == 0)
            return;

        var docRef = _db.Collection(CollectionName).Document(DocumentId);
        await _db.RunTransactionAsync(async transaction =>
        {
            var snapshot = await transaction.GetSnapshotAsync(docRef);
            var sessionCount = snapshot.TryGetValue("sessionCount", out int value) ? value : 0;
            sessionCount += delta;

            var updates = new Dictionary<string, object>
            {
                ["sessionCount"] = sessionCount,
                ["updatedAtUtc"] = Timestamp.FromDateTime(DateTime.UtcNow)
            };

            transaction.Set(docRef, updates, SetOptions.MergeAll);
            return sessionCount;
        }, cancellationToken: ct);
    }

    public async Task MarkModelTrainedAsync(ModelTrainingResult result, int totalSessions, CancellationToken ct = default)
    {
        var docRef = _db.Collection(CollectionName).Document(DocumentId);
        var payload = new Dictionary<string, object>
        {
            ["lastTrainedUtc"] = Timestamp.FromDateTime(DateTime.UtcNow),
            ["sessionCountAtLastTrain"] = totalSessions,
            ["sessionCount"] = totalSessions,
            ["retrainPending"] = false,
            ["retrainReason"] = string.Empty,
            ["lastAccuracy"] = result.Accuracy,
            ["lastAreaUnderRocCurve"] = result.AreaUnderRocCurve,
            ["lastF1Score"] = result.F1Score
        };

        await docRef.SetAsync(payload, SetOptions.MergeAll, ct);
    }

    public async Task<bool> TryRequestRetrainAsync(string reason, CancellationToken ct = default)
    {
        var docRef = _db.Collection(CollectionName).Document(DocumentId);
        var requested = false;
        await _db.RunTransactionAsync(async transaction =>
        {
            var snapshot = await transaction.GetSnapshotAsync(docRef);
            var alreadyPending = snapshot.TryGetValue("retrainPending", out bool pending) && pending;
            if (alreadyPending)
                return 0;

            requested = true;
            var updates = new Dictionary<string, object>
            {
                ["retrainPending"] = true,
                ["retrainReason"] = reason,
                ["pendingSinceUtc"] = Timestamp.FromDateTime(DateTime.UtcNow)
            };
            transaction.Set(docRef, updates, SetOptions.MergeAll);
            return 0;
        }, cancellationToken: ct);

        return requested;
    }

    public async Task<int> GetSessionCountAsync(CancellationToken ct = default)
    {
        var snapshot = await GetAsync(ct);
        return snapshot.SessionCount;
    }

    private async Task<DocumentSnapshot?> GetDocumentAsync(CancellationToken ct)
    {
        var docRef = _db.Collection(CollectionName).Document(DocumentId);
        var doc = await docRef.GetSnapshotAsync(ct);
        return doc;
    }

    private static DateTimeOffset? TryGetDateTime(IReadOnlyDictionary<string, object> data, string key)
    {
        if (!data.TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            Timestamp ts => ts.ToDateTime(),
            DateTime dt => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
            DateTimeOffset dto => dto,
            _ => null
        };
    }

    private static int TryGetInt(IReadOnlyDictionary<string, object> data, string key)
    {
        if (!data.TryGetValue(key, out var value) || value is null)
            return 0;
        return Convert.ToInt32(value);
    }

    private static double? TryGetDouble(IReadOnlyDictionary<string, object> data, string key)
    {
        if (!data.TryGetValue(key, out var value) || value is null)
            return null;
        return Convert.ToDouble(value);
    }
}
