using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Firestore;
using Microsoft.Extensions.Configuration;

public class FirebaseService
{
    private readonly FirestoreDb _db;

    public FirebaseService(FirestoreDb db, GoogleCredential credential, IConfiguration configuration)
    {
        _db = db;
        var projectId = configuration["Google:ProjectId"] ?? "fyp-assistant-7a216";

        if (FirebaseApp.DefaultInstance == null)
        {
            FirebaseApp.Create(new AppOptions
            {
                Credential = credential,
                ProjectId = projectId
            });
        }
    }

    public async Task SaveOrUpdateFitbitDataAsync(string date, Dictionary<string, object> data)
    {
        var docRef = _db.Collection("fitbit_data").Document(date);
        var docSnapshot = await docRef.GetSnapshotAsync();

        if (docSnapshot.Exists)
        {
            await docRef.UpdateAsync(data);
        }
        else
        {
            data["date"] = date;
            await docRef.SetAsync(data);
        }
    }

}
