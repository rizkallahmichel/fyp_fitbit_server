using System;
using System.IO;
using Google.Apis.Auth.OAuth2;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace FitServer.Services;

internal static class GoogleCredentialResolver
{
    public static GoogleCredential Resolve(IConfiguration configuration, IWebHostEnvironment environment)
    {
        var inlineJson = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS_JSON");
        if (!string.IsNullOrWhiteSpace(inlineJson))
        {
            return GoogleCredential.FromJson(inlineJson);
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
                : Path.Combine(environment.ContentRootPath, configuredPath);

            if (File.Exists(absolutePath))
            {
                return GoogleCredential.FromFile(absolutePath);
            }
        }

        var envPath = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
        {
            return GoogleCredential.FromFile(envPath);
        }

        throw new InvalidOperationException("Google credentials are not configured. Set GOOGLE_APPLICATION_CREDENTIALS or GOOGLE_APPLICATION_CREDENTIALS_JSON.");
    }
}
