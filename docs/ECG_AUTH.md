# ECG Authentication Workflow

This repo now exposes a complete Fitbit ECG -> ML authentication loop. Use the
endpoints below after configuring your Fitbit + Firebase credentials.

## 1. Configure Firebase credentials

Set one of these before running the API (use user-secrets for local dev):

```
GOOGLE_APPLICATION_CREDENTIALS=path\to\service-account.json
GOOGLE_APPLICATION_CREDENTIALS_JSON=<inline JSON string>
```

Optionally you can set `Google:CredentialsPath` or `Google:CredentialsJson`
through `appsettings.{Environment}.json` / user-secrets.

## 2. Collect ECG/HRV sessions

1. User initiates an ECG reading on the Charge 6 (30 s spot-check).
2. Call the endpoint (after Fitbit OAuth):
   ```
   POST /api/ecg-auth/collect-session
   ```
3. Repeat until you have **10-20 sessions per user** (more is better) and at
   least **two different users** to provide negative examples.

## 3. Train the ML model

Run:
```
POST /api/ecg-auth/train?maxPairsPerUser=500
```
The response contains accuracy, AUC, and F1. The trained file is saved to
`ecg_auth_model.zip`; redeploy this with your API.

## 4. Verify / tune threshold

1. Capture a fresh ECG reading.
2. Call:
   ```
   POST /api/ecg-auth/verify?threshold=0.9
   ```
   The payload returns the raw score plus `comparisonScores` for each stored
   enrollment session.
3. Evaluate FAR/FRR manually: run genuine attempts and impostor attempts, then
   adjust `threshold` to hit your target (e.g., operate near Equal Error Rate).

All verification attempts are logged to Firestore (`ecg_auth_logs`) so you can
compute FAR/FRR offline later.
