using System;
using System.Collections.Generic;
using System.Threading;
using FitServer.Services;
using Google.Cloud.Firestore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace FitServer.Tests.Infrastructure;

public sealed class TestApplicationFactory : WebApplicationFactory<Program>
{
    public FakeEcgAuthService EcgAuthService { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(config =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Fitbit:DisableAuthMiddleware"] = "true",
                ["AdaptiveModel:Enabled"] = "false"
            });
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IEcgAuthService>();
            services.RemoveAll<IEcgModelStateRepository>();
            services.RemoveAll<IEcgMlTrainer>();
            services.RemoveAll<FirestoreDb>();
            services.RemoveAll<global::FirebaseService>();

            services.AddSingleton<IEcgAuthService>(EcgAuthService);
            services.AddSingleton<IEcgModelStateRepository, InMemoryModelStateRepository>();
            services.AddSingleton<IEcgMlTrainer, NoopTrainer>();
        });
    }

    private sealed class InMemoryModelStateRepository : IEcgModelStateRepository
    {
        private readonly object _gate = new();
        private ModelStateSnapshot _state = new(null, 0, 0, false, null, null, null, null);
        private int _sessionCount;

        public Task<ModelStateSnapshot> GetAsync(CancellationToken ct = default)
        {
            lock (_gate)
            {
                return Task.FromResult(_state);
            }
        }

        public Task<int> GetSessionCountAsync(CancellationToken ct = default)
        {
            lock (_gate)
            {
                return Task.FromResult(_sessionCount);
            }
        }

        public Task IncrementSessionCountAsync(int delta, CancellationToken ct = default)
        {
            lock (_gate)
            {
                _sessionCount += delta;
                return Task.CompletedTask;
            }
        }

        public Task MarkModelTrainedAsync(ModelTrainingResult result, int sessionCount, CancellationToken ct = default)
        {
            lock (_gate)
            {
                _state = new ModelStateSnapshot(
                    DateTimeOffset.UtcNow,
                    sessionCount,
                    sessionCount,
                    false,
                    null,
                    result.Accuracy,
                    result.AreaUnderRocCurve,
                    result.F1Score);
                _sessionCount = sessionCount;
                return Task.CompletedTask;
            }
        }

        public Task<bool> TryRequestRetrainAsync(string reason, CancellationToken ct = default) => Task.FromResult(false);
    }

    private sealed class NoopTrainer : IEcgMlTrainer
    {
        public Task<ModelTrainingResult> TrainAndSaveAsync(string modelPath, int maxPairsPerUser, CancellationToken ct = default)
        {
            var result = new ModelTrainingResult(modelPath, $"{modelPath}_correction", 0.9, 0.9, 0.9, 20, 100);
            return Task.FromResult(result);
        }
    }
}
