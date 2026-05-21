#!/usr/bin/env python3
"""
Plot ROC curve and score distribution for the Fitbit ECG authentication model.

Inputs:
  --scores     CSV/Parquet file (or a zip containing one) with at least
               the columns Label (0/1) and either Probability or Score.
               Score should be the raw LightGBM score saved from training.
  --model-zip  Path to ecg_auth_model*.zip so the Platt calibrator
               (A, B) parameters can be read when only Score is available.
"""

from __future__ import annotations

import argparse
import io
import re
import zipfile
from pathlib import Path
from typing import Iterable

import matplotlib.pyplot as plt
import numpy as np
import pandas as pd
import seaborn as sns
from sklearn.metrics import auc, roc_curve

CALIBRATOR_REGEX = re.compile(r"A=([-+0-9.eE]+).*B=([-+0-9.eE]+)", re.DOTALL)


def read_calibrator(zip_path: Path) -> tuple[float, float]:
    """Return (A, B) from the Platt calibrator embedded in the ML.NET zip."""
    with zipfile.ZipFile(zip_path) as zf:
        for name in zf.namelist():
            if name.endswith("Calibrator/Calibrator.txt"):
                text = zf.read(name).decode("utf-8", errors="ignore")
                match = CALIBRATOR_REGEX.search(text)
                if match:
                    return float(match.group(1)), float(match.group(2))
                break
    # Fallback to identity if no calibrator is present.
    return -1.0, 0.0


def load_frame(scores_path: Path) -> pd.DataFrame:
    """Load score data from CSV/Parquet or from a nested archive."""
    if scores_path.suffix.lower() == ".zip":
        with zipfile.ZipFile(scores_path) as zf:
            inner = _pick_inner_file(zf.namelist())
            with zf.open(inner) as fh:
                if inner.lower().endswith(".csv"):
                    return pd.read_csv(fh)
                buffer = io.BytesIO(fh.read())
                return pd.read_parquet(buffer)

    if scores_path.suffix.lower() == ".parquet":
        return pd.read_parquet(scores_path)

    return pd.read_csv(scores_path)


def _pick_inner_file(names: Iterable[str]) -> str:
    csvs = [name for name in names if name.lower().endswith(".csv")]
    if csvs:
        return csvs[0]
    parquet = [name for name in names if name.lower().endswith(".parquet")]
    if parquet:
        return parquet[0]
    raise FileNotFoundError(
        "Expected a CSV or Parquet file inside the archive with the scored pairs."
    )


def select_column(columns: list[str], candidates: Iterable[str]) -> str:
    lowered = {col.lower(): col for col in columns}
    for alias in candidates:
        if alias.lower() in lowered:
            return lowered[alias.lower()]
    raise KeyError(f"None of {candidates} found in columns: {columns}")


def platt_probability(scores: np.ndarray, a: float, b: float) -> np.ndarray:
    logits = a * scores + b
    logits = np.clip(logits, -700, 700)  # Guard for overflow in exp.
    return 1.0 / (1.0 + np.exp(logits))


def plot_roc(y_true: np.ndarray, probs: np.ndarray, out_path: Path) -> float:
    fpr, tpr, _ = roc_curve(y_true, probs)
    roc_auc = auc(fpr, tpr)
    plt.figure(figsize=(6, 6))
    plt.plot(fpr, tpr, label=f"AUC = {roc_auc:0.4f}")
    plt.plot([0, 1], [0, 1], "k--", label="Chance")
    plt.xlabel("False Positive Rate")
    plt.ylabel("True Positive Rate")
    plt.title("ROC Curve – ECG Auth Model")
    plt.legend(loc="lower right")
    plt.grid(True, linestyle="--", alpha=0.4)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    plt.tight_layout()
    plt.savefig(out_path, dpi=200)
    plt.close()
    return roc_auc


def compute_eer(y_true: np.ndarray, probs: np.ndarray) -> tuple[float, float]:
    fpr, tpr, thresholds = roc_curve(y_true, probs)
    fnr = 1.0 - tpr
    idx = int(np.nanargmin(np.abs(fpr - fnr)))
    eer = float((fpr[idx] + fnr[idx]) / 2.0)
    threshold = float(thresholds[idx])
    return eer, threshold


def plot_distribution(
    y_true: np.ndarray, probs: np.ndarray, out_path: Path, threshold: float
) -> None:
    df = pd.DataFrame({"probability": probs, "label": y_true})
    plt.figure(figsize=(7, 4))
    sns.histplot(
        df,
        x="probability",
        hue="label",
        bins=40,
        stat="density",
        common_norm=False,
        palette={0: "#d9534f", 1: "#5cb85c"},
    )
    plt.axvline(threshold, color="#007bff", linestyle="--", label=f"Threshold {threshold}")
    plt.xlabel("Calibrated Probability")
    plt.ylabel("Density")
    plt.title("Score Distribution (genuine vs impostor)")
    plt.legend()
    plt.tight_layout()
    out_path.parent.mkdir(parents=True, exist_ok=True)
    plt.savefig(out_path, dpi=200)
    plt.close()


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Plot ROC and score distributions for ecg_auth_model.zip outputs.",
    )
    parser.add_argument(
        "--scores",
        required=True,
        help="CSV/Parquet file (or zip containing one) with Label + Score/Probability columns.",
    )
    parser.add_argument(
        "--model-zip",
        default="ecg_auth_model.zip",
        help="Path to the ML.NET model zip (used to read the Platt calibrator).",
    )
    parser.add_argument(
        "--output-dir",
        default="docs/metrics",
        help="Directory where ROC and histogram plots will be written.",
    )
    parser.add_argument(
        "--threshold",
        type=float,
        default=0.85,
        help="Decision threshold to overlay on the score distribution plot.",
    )
    args = parser.parse_args()

    scores_path = Path(args.scores)
    model_zip = Path(args.model_zip)
    output_dir = Path(args.output_dir)

    frame = load_frame(scores_path)
    label_col = select_column(frame.columns.tolist(), ["Label", "label", "target", "y"])

    probability_col = next(
        (col for col in frame.columns if col.lower() in {"probability", "prob"}), None
    )
    if probability_col is None:
        score_col = select_column(frame.columns.tolist(), ["Score", "rawScore"])
        a, b = read_calibrator(model_zip)
        probs = platt_probability(frame[score_col].to_numpy(dtype=float), a, b)
    else:
        probs = frame[probability_col].to_numpy(dtype=float)

    labels = frame[label_col].astype(int).to_numpy()

    roc_path = output_dir / "roc_curve.png"
    hist_path = output_dir / "score_distribution.png"
    auc_value = plot_roc(labels, probs, roc_path)
    plot_distribution(labels, probs, hist_path, args.threshold)
    eer_value, eer_threshold = compute_eer(labels, probs)

    positives = labels.sum()
    negatives = len(labels) - positives
    threshold = args.threshold
    tpr, fpr = _threshold_stats(labels, probs, threshold)
    print(f"Samples: {len(labels)} (pos={positives}, neg={negatives})")
    print(f"ROC AUC: {auc_value:0.4f}")
    print(f"EER: {eer_value:0.4f} at threshold {eer_threshold:0.4f}")
    print(f"At threshold {threshold:.2f}: TPR={tpr:0.4f}  FPR={fpr:0.4f}")
    print(f"ROC plot   -> {roc_path}")
    print(f"Hist plot  -> {hist_path}")


def _threshold_stats(y_true: np.ndarray, probs: np.ndarray, threshold: float) -> tuple[float, float]:
    positives = y_true == 1
    negatives = ~positives
    tp = float(((probs >= threshold) & positives).sum())
    fn = float(((probs < threshold) & positives).sum())
    fp = float(((probs >= threshold) & negatives).sum())
    tn = float(((probs < threshold) & negatives).sum())
    tpr = tp / (tp + fn) if (tp + fn) > 0 else 0.0
    fpr = fp / (fp + tn) if (fp + tn) > 0 else 0.0
    return tpr, fpr


if __name__ == "__main__":
    main()
