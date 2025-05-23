using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Firestore;

public class FirebaseService
{
    private readonly FirestoreDb _db;

    public FirebaseService(IWebHostEnvironment env)
    {
        var path = Path.Combine(env.ContentRootPath, "fyp-assistant-firebase.json");

        if (FirebaseApp.DefaultInstance == null)
        {
            FirebaseApp.Create(new AppOptions
            {
                Credential = GoogleCredential.FromFile(path)
            });
        }

        _db = FirestoreDb.Create("fyp-assistant-7a216");
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
