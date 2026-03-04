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
4. Successful verify calls now persist the captured waveform back into `ecg_sessions` with an `auto-verify` tag, so your Fitbit dataset grows even if you skip `/collect-session`.

All verification attempts are logged to Firestore (`ecg_auth_logs`) so you can
compute FAR/FRR offline later.

## 5. Benchmark ECG-ID protocol (Safie et al., 2024)

Use this endpoint when you want to replicate the Safie et al. split without involving Fitbit data:

```
POST /api/ecg-auth/benchmark-ecg-id
{
  "maxPairsPerUser": 600,
  "testFraction": 0.4
}
```

- Only sessions tagged with `dataSource = ecg-id` (or an `ecg-id` tag) are kept.
- Training runs the existing feature extraction + LightGBM stack but enforces the 60/40 train/test split from the paper.
- The response returns dataset stats (subjects/sessions) plus Accuracy/AUC/F1 so you can cite a like-for-like comparison in your thesis.
