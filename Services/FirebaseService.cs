using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Firestore;
using Microsoft.Extensions.Configuration;

public class FirebaseService
{
    private readonly FirestoreDb _db;

    public FirebaseService(IConfiguration configuration, IWebHostEnvironment env)
    {
        var credential = ResolveCredential(configuration, env);
        var projectId = configuration["Google:ProjectId"] ?? "fyp-assistant-7a216";

        if (FirebaseApp.DefaultInstance == null)
        {
            FirebaseApp.Create(new AppOptions
            {
                Credential = credential,
                ProjectId = projectId
            });
        }

        _db = FirestoreDb.Create(projectId);
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

    private static GoogleCredential ResolveCredential(IConfiguration configuration, IWebHostEnvironment env)
    {
        var inlineJson = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS_JSON");
        if (!string.IsNullOrWhiteSpace(inlineJson))
        {
            return GoogleCredential.FromJson(inlineJson);
        }

        var envPath = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
        {
            return GoogleCredential.FromFile(envPath);
        }

        var configuredJson = configuration["Google:CredentialsJson"];
        if (!string.IsNullOrWhiteSpace(configuredJson))
        {
            return GoogleCredential.FromJson(configuredJson);
        }

        var configuredPath = configuration["Google:CredentialsPath"];
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var absolutePath = Path.IsPathRooted(configuredPath)
                ? configuredPath
                : Path.Combine(env.ContentRootPath, configuredPath);

            if (File.Exists(absolutePath))
            {
                return GoogleCredential.FromFile(absolutePath);
            }
        }

        throw new InvalidOperationException("Google credentials are not configured. Set GOOGLE_APPLICATION_CREDENTIALS or GOOGLE_APPLICATION_CREDENTIALS_JSON.");
    }
}
