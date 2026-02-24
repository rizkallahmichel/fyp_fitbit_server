using FitServer.Services;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Firestore;
using Google.Cloud.Firestore.V1;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Primitives;
using Microsoft.OpenApi.Models;

namespace FitServer
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.
            builder.Services.AddControllers();
            // Add Swagger
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new OpenApiInfo
                {
                    Title = "Fitbit API",
                    Version = "v1"
                });
            });
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddSingleton<FirebaseService>();
            builder.Services.AddDistributedMemoryCache();
            builder.Services.AddSession(options =>
            {
                options.IdleTimeout = TimeSpan.FromMinutes(30);
                options.Cookie.HttpOnly = true;
                options.Cookie.IsEssential = true;
            });
            // Add HttpClientFactory
            builder.Services.AddHttpClient();

            if (builder.Configuration.GetValue("Fitbit:EnableDataLoader", false))
            {
            builder.Services.AddHostedService<FitbitDataLoader>();
        }

        builder.Services.Configure<AdaptiveModelOptions>(builder.Configuration.GetSection("AdaptiveModel"));
        builder.Services.AddSingleton(provider =>
        {
            var configuration = provider.GetRequiredService<IConfiguration>();
            var environment = provider.GetRequiredService<IWebHostEnvironment>();
            return GoogleCredentialResolver.Resolve(configuration, environment);
        });

        builder.Services.AddSingleton(provider =>
        {
            var configuration = provider.GetRequiredService<IConfiguration>();
            var credential = provider.GetRequiredService<GoogleCredential>();
            var projectId = configuration["Google:ProjectId"] ?? "fyp-assistant-7a216";
            var scopedCredential = credential.IsCreateScopedRequired
                ? credential.CreateScoped(FirestoreClient.DefaultScopes)
                : credential;

            return new FirestoreDbBuilder
            {
                ProjectId = projectId,
                Credential = scopedCredential
            }.Build();
        });

        builder.Services.AddSingleton<IFitbitEcgService, FitbitEcgService>();
        builder.Services.AddSingleton<IEcgFeatureExtractor, EcgFeatureExtractor>();
        builder.Services.AddSingleton<IEcgMlTrainer, EcgMlTrainer>();
        builder.Services.AddSingleton<IEcgAugmentationService, EcgAugmentationService>();
        builder.Services.AddSingleton<IEcgEmbeddingService, EcgEmbeddingService>();
        builder.Services.AddSingleton<IEcgModelStateRepository, EcgModelStateRepository>();
        builder.Services.AddSingleton<IConfidenceModelingService, ConfidenceModelingService>();
        builder.Services.AddSingleton<IEcgAuthService, EcgAuthService>();
        builder.Services.AddHostedService<AdaptiveModelSupervisor>();

        // Add Session support
        builder.Services.AddDistributedMemoryCache();
            builder.Services.AddSession(options =>
            {
                options.Cookie.HttpOnly = true;
                options.Cookie.IsEssential = true;
            });

            builder.Services.AddCors(options =>
            {
                options.AddPolicy("AllowAll", policy =>
                {
                    policy.AllowAnyOrigin()
                          .AllowAnyHeader()
                          .AllowAnyMethod();
                });
            });

            var app = builder.Build();

            app.UseSwagger();
            app.UseSwaggerUI(c =>
            {
                c.SwaggerEndpoint("/swagger/v1/swagger.json", "Fitbit API V1");
                c.RoutePrefix = string.Empty;
            });

            app.UseCors("AllowAll");

            app.UseSession();
            app.UseHttpsRedirection();

            app.UseSession();

            app.UseAuthorization();

            var disableFitbitMiddleware = builder.Configuration.GetValue("Fitbit:DisableAuthMiddleware", false);
            if (disableFitbitMiddleware)
            {
                app.Use(async (context, next) =>
                {
                    if (!context.Items.ContainsKey("AccessToken") &&
                        context.Request.Headers.TryGetValue("X-Test-AccessToken", out var headerValue) &&
                        !StringValues.IsNullOrEmpty(headerValue))
                    {
                        context.Items["AccessToken"] = headerValue.ToString();
                    }

                    await next();
                });
            }
            else
            {
                app.UseMiddleware<FitbitAuthMiddleware>();
            }

            app.MapControllers();

            app.Run();
        }
    }
}
