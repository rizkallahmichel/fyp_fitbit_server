# FitServer – Fitbit ECG Authentication Service

## Overview
FitServer is an ASP.NET Core 9.0 backend that ingests Fitbit ECG/HRV sessions, trains adaptive ML models, and exposes endpoints for enrollment, verification, and continuous monitoring. The service integrates with Firestore for persistence, Firebase Admin for device telemetry, and background workers for automated retraining.

## Architecture Highlights
- **Controllers**: `EcgAuthController` exposes session collection, training, verification, and continuous verification endpoints.
- **Data diagnostics**: `GET /api/ecg-auth/data-overview` summarizes the Firestore collections that actually feed the ECG UI (`ecg_sessions`, `ecg_auth_logs`, `ecg_confidence`, `ecg_model_state`, `fitbit_data`).
- **Services**: Signal processing (`EcgFeatureExtractor`, `EcgEmbeddingService`), augmentation (`EcgAugmentationService`), ML training (`EcgMlTrainer`), and confidence modeling.
- **Background jobs**: `FitbitDataLoader` and `AdaptiveModelSupervisor`.
- **Middleware**: `FitbitAuthMiddleware` refreshes Fitbit OAuth tokens; tests can toggle it via `Fitbit:DisableAuthMiddleware`.
- **Data stores**: Firestore collections (`ecg_sessions`, `ecg_auth_logs`, `ecg_confidence`, `ecg_model_state`) plus local model artifacts (`ecg_auth_model.zip`).
- **External datasets**: Fitbit sessions are now complemented by curated samples imported from the public ECG-ID database, increasing class diversity for model training/validation.
- **ECG-ID benchmark endpoint**: call `POST /api/ecg-auth/benchmark-ecg-id` to reproduce the Safie et al. (2024) 60/40 split on the ECG-ID subset only and capture Accuracy/AUC/F1 deltas without touching Fitbit data.
- **Secure enrollment by default**: `/api/ecg-auth/verify` no longer auto-enrolls captured waveforms by default, preventing accidental dataset poisoning from false accepts.
- **False-attempt feedback loop**: `POST /api/ecg-auth/report-false-attempt` tags a known impostor sample and requests retraining so the next model treats it as a hard negative.

## Prerequisites
| Requirement | Purpose |
|-------------|---------|
| .NET SDK 9.0+ | Build and run the API |
| Firebase service account JSON | Firestore access (`fyp-assistant-*.json`) |
| Fitbit OAuth client credentials | Collect ECG data via Fitbit APIs |
| Google Cloud project | Stores Firestore datasets |

## Configuration
Configure credentials via environment variables or `appsettings.*.json`:

```bash
set GOOGLE_APPLICATION_CREDENTIALS=path\to\service-account.json
set Fitbit:ClientId=***
set Fitbit:ClientSecret=***
set Fitbit:RedirectUri=https://your-app/callback
# Optional test shortcut
set Fitbit:DisableAuthMiddleware=true
```

`docs/ECG_AUTH.md` walks through the collection → training → verification workflow.

## Build & Run
```bash
# Restore and build
dotnet build

# Run the API (launches Swagger at http://localhost:5104 by default)
dotnet run --project FitServer.csproj
```

While developing automated tests, you can bypass Fitbit OAuth by setting `Fitbit:DisableAuthMiddleware=true` and sending an `X-Test-AccessToken` header (used by the integration test harness).

## Useful Diagnostic Endpoint

When the UI looks empty and you need to know whether Firestore actually contains ECG data, call:

```bash
GET /api/ecg-auth/data-overview
```

The response tells you:
- how many documents exist in each relevant Firestore collection
- which Fitbit users are present in `ecg_sessions`
- which recent ECG sessions and verification logs exist
- whether `ecg_model_state` has already been trained

This is the fastest way to explain an empty participant dropdown or missing verification history in the UI.

## Automated Tests
| Command | Description |
|---------|-------------|
| `dotnet test Tests/FitServer.Tests/FitServer.Tests.csproj` | Runs unit and controller integration suites (26 tests) with coverlet coverage reports in `TestResults/coverage/coverage.cobertura.xml`. |
| `dotnet test` | Executes every test project in the solution (currently only `FitServer.Tests`). |

Test highlights:
- **Unit tests**: Signal augmentation (`EcgAugmentationServiceTests`), embedding fallbacks, model quality rules, waveform compression, etc. (new cases verify behavior on augmented ECG-ID samples).
- **Integration tests**: `EcgAuthControllerTests` bootstraps the entire host with `WebApplicationFactory`, exercising authorization errors, happy paths, and continuous verification payloads.

## Documentation & References
- `docs/ECG_AUTH.md`: End-to-end workflow (credentials, collection, training, verification).
- `docs/EER.md`: Mermaid EER view of the current Firestore persistence model.
- `Services/*.cs`: Implementation details with inline comments for ML pipeline components.
- `README.md` (this file): Quick start, run instructions, and test coverage.


python script to show result and score:
python tools/plot_train_metrics.py --scores bin\Debug\net9.0\reports\train_scores.csv --model-zip ecg_auth_model.zip --output-dir docs\metrics

python script to regenerate the EER diagram:
python tools/generate_eer.py --mmd-output docs/eer/firestore_eer.mmd --md-output docs/EER.md
