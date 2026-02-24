# Cahier de charge – Plateforme ECG Fitbit  
_Version : 24 février 2026_

## 1. Contexte & objectifs
- Solution biométrique exploitant les signaux ECG provenant des montres Fitbit (Charge 6, Sense, etc.).
- Deux projets principaux : **FitServer** (backend ASP.NET Core 9.0) et **UI** (console opérateur React/Vite).
- Objectif : collecter, entraîner et valider un modèle d’authentification ECG incluant désormais un volume étendu de données issues du **référentiel public ECG-ID** en complément des lectures Fitbit, afin d’augmenter la diversité des classes et la robustesse du modèle.

## 2. Périmètre fonctionnel
1. **Collecte**  
   - Endpoint `/api/ecg-auth/collect-session` (appelé via l’UI Enrollment Wizard).  
   - Minimum 10–20 sessions par participant, capture de métadonnées/tags/notes, vérification de la qualité du signal.  
   - Import périodique des nouveaux échantillons ECG-ID pour enrichir la base.
2. **Entraînement**  
   - `/api/ecg-auth/train` (manuel) ou déclenchements automatiques via `AdaptiveModelSupervisor`.  
   - Stockage des métriques (accuracy, AUC, F1), génération du modèle `ecg_auth_model.zip`.  
   - Historisation des jeux d’entraînement (Fitbit + ECG-ID) dans Firestore.
3. **Vérification ponctuelle**  
   - `/api/ecg-auth/verify` avec seuil configurable, retour des scores et des comparaisons baseline.  
   - UI Verification Panel : slider de seuil, mode imposteur, logs de tentatives.
4. **Vérification continue**  
   - `/api/ecg-auth/continuous-verify` pour fenêtres glissantes, agrégation des scores, génération de logs Firestore (`ecg_auth_logs`).  
   - UI Continuous Monitor : Recharts, log des échantillons, KPIs rolling.
5. **Analytics & supervision**  
   - Tab Analytics (FAR/FRR estimés, alias participants).  
   - Confidence modeling (`ecg_confidence`) et déclenchement automatique d’entraînement en cas de dérive.

## 3. Architecture & composants
- **Backend**  
  - `Program.cs` installe Swagger, CORS, DI, session et middleware `FitbitAuthMiddleware`.  
  - Services principaux : `EcgFeatureExtractor`, `EcgEmbeddingService`, `EcgAugmentationService`, `EcgMlTrainer`, `ConfidenceModelingService`, `FitbitEcgService`, `FitbitDataLoader`, `AdaptiveModelSupervisor`.  
  - Persistances : Firestore (`ecg_sessions`, `ecg_auth_logs`, `ecg_confidence`, `ecg_model_state`) + fichiers modèles (ZIP).  
  - Tests d’intégration via `TestApplicationFactory` (WebApplicationFactory custom) et fakes (`FakeEcgAuthService`).  
  - Les nouveaux échantillons ECG-ID sont chargés via scripts offline et suivent le même pipeline (extraction features + stockage Firestore).
- **Frontend (UI)**  
  - React 19, Vite, TanStack Query, Axios client (`src/api/client.ts`).  
  - Onglets : Participants, Enrollment, Verification, Continuous, Analytics.  
  - Stockage local des alias (`useLocalStorage`), visualisations Recharts.  
  - Tests Vitest + Playwright.

## 4. Exigences techniques
| Domaine | Attentes |
|---------|----------|
| **Sécurité** | Secrets Fitbit/Firebase hors repo (`GOOGLE_APPLICATION_CREDENTIALS`, `Fitbit:ClientId`, `Fitbit:ClientSecret`). Support user-secrets ou vault. |
| **Performance** | Temps de collecte <5 s, vérification temps réel. Superviseur adaptatif configurable (`AdaptiveModelOptions`). |
| **Qualité des données** | Filtrage qualité via `EcgQualityRules`, rejet signaux faibles. Tracabilité des provenances (Fitbit vs ECG-ID). |
| **Scalabilité** | Hébergement cloud (App Service/Cloud Run), Firestore managé. CORS restreint en production. |
| **Conformité** | Anonymisation/pseudonymisation pour export (datasets Fitbit & ECG-ID). Retention paramétrable. |

## 5. UX / UI
- Wizard d’enrôlement (timer 30 s, reset auto des métadonnées).  
- Panneau de vérification : slider seuil 0.5→0.95, notes, label genuine/impostor, sweep visuel, table comparaisons.  
- Continuous Monitor : courbes score vs temps, log formaté (heure, pass/fail), KPIs pass rate.  
- Analytics : FAR/FRR, attempt log, alias editing.  
- Accessibilité : labels, navigation clavier, messages d’erreur clairs.

## 6. Stratégie de tests
- **Backend** (`dotnet test Tests/FitServer.Tests/FitServer.Tests.csproj`)  
  - 26 tests unitaires/intégration (couverture ≥15 %).  
  - Unittests notables : `EcgAugmentationServiceTests`, `EcgEmbeddingServiceTests`, `EcgQualityRulesTests`, `WaveformCompressorTests`, `ConfidenceModelingServiceTests`. Ces tests incluent des scénarios basés sur les signaux augmentés provenant de l’ECG-ID pour valider la robustesse du pipeline.  
  - Tests d’intégration : `EcgAuthControllerTests` (collect, verify, continuous verify, erreurs d’authentification).
- **Frontend**  
  - `npm run test:unit` (Vitest + Testing Library).  
  - `npm run test:e2e` (Playwright) pour les parcours critiques (navigation Participants/Enrollment, interactions panel).
- **CI/CD**  
  - Publier les rapports (cobertura, Playwright) dans les artefacts.  
  - Couvrir 100 % des endpoints critiques avant mise en prod.

## 7. Livrables
- `README.md` backend (setup, run, tests) + `UI/README.md`.  
- `docs/ECG_AUTH.md` (workflow).  
- Ce cahier de charge (`docs/CAHIER_DE_CHARGE.md`).  
- Rapports de tests automatisés.  
- Scripts d’import ECG-ID documentés.

## 8. Planning prévisionnel
| Jalons | Livrables |
|--------|-----------|
| **M1 – Setup & Data** | Secrets sécurisés, pipeline collecte Fitbit, import ECG-ID, cahier signé. |
| **M2 – Tech Review** | Audit DI/middleware, alignement API/UI, base Terraform. |
| **M3 – QA & Tests** | Couverture backend/unitaire, Vitest & Playwright en CI. |
| **M4 – Doc & transfert** | Runbooks, politiques secrets/données, formation équipes. |

## 9. Risques & atténuation
- **Disponibilité Fitbit API** : mettre en cache, anticiper quotas, fallback ECG-ID.  
- **Sécurité des données sensibles** : chiffrement repos/transport, rotation clés, audits.  
- **Dérive du modèle** : monitorer `ConfidenceModelingService`, déclencher retrain auto.  
- **Dette technique tests** : surveiller taux de couverture, ajouter tests pour chaque service/middleware.

Ce cahier de charge doit être revu et approuvé par PO, Architecture, Sécurité et Équipe R&D avant toute mise en production. Toute évolution majeure doit faire l’objet d’un addendum versionné.
