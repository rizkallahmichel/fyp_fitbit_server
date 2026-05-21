#!/usr/bin/env python3
"""
Generate a Mermaid EER diagram for the Firestore schema used by FitServer.

The output is conceptual rather than relational:
- Firestore collections/documents are modeled as entities.
- Embedded objects inside `ecg_sessions` are shown as child entities.
- `FITBIT_USER` is an external/conceptual entity resolved from Fitbit APIs.
"""

from __future__ import annotations

import argparse
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class Field:
    type_name: str
    name: str
    qualifier: str = ""


@dataclass(frozen=True)
class Entity:
    name: str
    fields: tuple[Field, ...]


@dataclass(frozen=True)
class Relationship:
    expression: str


ENTITIES: tuple[Entity, ...] = (
    Entity(
        "FITBIT_USER",
        (
            Field("string", "fitbitUserId", "PK"),
            Field("string", "displayName"),
            Field("string", "encodedId"),
        ),
    ),
    Entity(
        "ECG_SESSION",
        (
            Field("string", "documentId", "PK"),
            Field("string", "fitbitUserId", "FK"),
            Field("string", "dataSource"),
            Field("datetime", "sessionTimeUtc"),
            Field("datetime", "collectedAtUtc"),
            Field("datetime", "ecgStartTime"),
            Field("double", "hrvDailyRmssd"),
            Field("int", "samplingFrequencyHz"),
            Field("int", "scalingFactor"),
            Field("double", "signalQualityScore"),
            Field("double", "motionArtifactIndex"),
            Field("double", "baselineDriftRatio"),
            Field("string", "waveformBlob"),
            Field("int_array", "waveformPreview"),
            Field("string_array", "tags"),
            Field("string", "notes"),
            Field("float_array", "embeddingVector"),
        ),
    ),
    Entity(
        "ECG_FEATURES",
        (
            Field("double", "mean"),
            Field("double", "std"),
            Field("double", "rms"),
            Field("double", "min"),
            Field("double", "max"),
            Field("double", "skewness"),
            Field("double", "kurtosis"),
            Field("double", "estimatedBpm"),
            Field("double", "peakCount"),
            Field("double", "rrMeanMs"),
            Field("double", "rrStdMs"),
            Field("double", "qrsWidthMs"),
            Field("double", "lowFreqPowerRatio"),
            Field("double", "midFreqPowerRatio"),
            Field("double", "highFreqPowerRatio"),
            Field("double", "spectralCentroidHz"),
            Field("double", "spectralEntropy"),
            Field("double", "veryLowFreqPowerRatio"),
            Field("double", "signalQualityScore"),
            Field("double", "motionArtifactIndex"),
            Field("double", "baselineDriftRatio"),
            Field("float_array", "embeddingVector"),
        ),
    ),
    Entity(
        "SESSION_METADATA",
        (
            Field("string", "activityLabel"),
            Field("string", "stressLevel"),
            Field("string", "sensorPlacement"),
            Field("string", "deviceModel"),
        ),
    ),
    Entity(
        "ECG_AUTH_LOG",
        (
            Field("string", "documentId", "PK"),
            Field("string", "fitbitUserId", "FK"),
            Field("datetime", "attemptedAtUtc"),
            Field("double", "score"),
            Field("double", "threshold"),
            Field("double", "meanScore"),
            Field("int", "votesPassing"),
            Field("double", "consensusScore"),
            Field("double", "latestScore"),
            Field("bool", "latestPasses"),
            Field("bool", "authenticated"),
            Field("int", "comparisonCount"),
            Field("double", "confidenceLevel"),
            Field("double", "confidenceDrift"),
            Field("int", "confidenceSamples"),
        ),
    ),
    Entity(
        "ECG_CONFIDENCE",
        (
            Field("string", "fitbitUserId", "PK"),
            Field("int", "sampleCount"),
            Field("double", "mean"),
            Field("double", "m2"),
            Field("double", "ema"),
            Field("double", "confidence"),
            Field("double", "drift"),
            Field("double", "lastThreshold"),
            Field("int", "consecutivePasses"),
            Field("int", "consecutiveFailures"),
            Field("datetime", "updatedAtUtc"),
        ),
    ),
    Entity(
        "ECG_MODEL_STATE",
        (
            Field("string", "documentId", "PK"),
            Field("datetime", "lastTrainedUtc"),
            Field("int", "sessionCount"),
            Field("int", "sessionCountAtLastTrain"),
            Field("bool", "retrainPending"),
            Field("string", "retrainReason"),
            Field("datetime", "pendingSinceUtc"),
            Field("datetime", "updatedAtUtc"),
            Field("double", "lastAccuracy"),
            Field("double", "lastAreaUnderRocCurve"),
            Field("double", "lastF1Score"),
        ),
    ),
    Entity(
        "FITBIT_DAILY_DATA",
        (
            Field("date", "date", "PK"),
            Field("int", "steps"),
            Field("int", "sleepScore"),
            Field("double", "hrv"),
            Field("int", "rhr"),
            Field("double", "skinTemperature"),
            Field("int", "heartrate"),
            Field("double", "breathingRate"),
        ),
    ),
)


RELATIONSHIPS: tuple[Relationship, ...] = (
    Relationship("FITBIT_USER ||--o{ ECG_SESSION : records"),
    Relationship("FITBIT_USER ||--o{ ECG_AUTH_LOG : attempts"),
    Relationship("FITBIT_USER ||--|| ECG_CONFIDENCE : accumulates"),
    Relationship("ECG_SESSION ||--|| ECG_FEATURES : embeds"),
    Relationship("ECG_SESSION ||--o| SESSION_METADATA : annotates"),
)


def render_mermaid() -> str:
    lines = [
        "erDiagram",
        "    %% Generated by tools/generate_eer.py",
        "    %% FITBIT_USER is conceptual/external. Other entities map to Firestore collections or embedded documents.",
    ]

    for entity in ENTITIES:
        lines.append(f"    {entity.name} {{")
        for field in entity.fields:
            qualifier = f" {field.qualifier}" if field.qualifier else ""
            lines.append(f"        {field.type_name} {field.name}{qualifier}")
        lines.append("    }")
        lines.append("")

    for relationship in RELATIONSHIPS:
        lines.append(f"    {relationship.expression}")

    return "\n".join(lines).rstrip() + "\n"


def render_markdown(mermaid: str, mmd_path: Path, md_path: Path) -> str:
    return f"""# Firestore EER

This diagram documents the persisted Firestore model used by FitServer today.

- `FITBIT_USER` is a conceptual external entity resolved from the Fitbit profile API, not a Firestore collection.
- `ECG_FEATURES` and `SESSION_METADATA` are embedded objects inside each `ecg_sessions` document.
- `ECG_MODEL_STATE` is a singleton document stored as `ecg_model_state/current`.
- `FITBIT_DAILY_DATA` documents are keyed by date and currently store one daily snapshot per run.

Regenerate with:

```bash
python tools/generate_eer.py --mmd-output {mmd_path.as_posix()} --md-output {md_path.as_posix()}
```

```mermaid
{mermaid.rstrip()}
```
"""


def write_text(path: Path, content: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(content, encoding="utf-8")


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Generate a Mermaid EER diagram for the FitServer Firestore schema.",
    )
    parser.add_argument(
        "--mmd-output",
        default="docs/eer/firestore_eer.mmd",
        help="Path to the generated Mermaid source file.",
    )
    parser.add_argument(
        "--md-output",
        default="docs/EER.md",
        help="Path to the generated Markdown preview file.",
    )
    args = parser.parse_args()

    mmd_output = Path(args.mmd_output)
    md_output = Path(args.md_output)

    mermaid = render_mermaid()
    markdown = render_markdown(mermaid, mmd_output, md_output)

    write_text(mmd_output, mermaid)
    write_text(md_output, markdown)

    print(f"Mermaid EER written to {mmd_output}")
    print(f"Markdown preview written to {md_output}")


if __name__ == "__main__":
    main()
