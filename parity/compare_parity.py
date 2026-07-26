"""
compare_parity.py -assert the Python and C# feature extractors agree.

Loads two JSON dumps (one from the training repo's parity_dump.py, one from
parity/ParityDump) and compares them element-by-element within a tolerance that
absorbs float64-vs-float32 rounding while still catching real logic drift.

On mismatch it prints WHICH feature diverged (index, aggregate, per-section slot)
so you know exactly where the two implementations disagree.

Usage:
    python compare_parity.py <python.json> <csharp.json> [atol] [rtol]

Exit code 0 = PASS, 1 = FAIL, 2 = bad usage.
"""

import sys
import json

# Per-section feature names, index-aligned with the 29-element list in both
# extract_meaningful_features (Python) and ExtractSectionFeatures (C#).
SECTION_FEATURES = [
    "burst_count", "stream_count", "max_continuous_stream", "total_stream_notes",
    "rhythm_change_ratio", "global_rhythm_variance",
    "max_stream_spacing_variance", "buzz_slider_count",
    "finger_control_score", "avg_rhythm_instability", "avg_spacing_instability",
    "slider_disruption_rate",
    "num_objects", "objects_per_sec",
    "mean_distance", "std_distance", "percentile95_distance",
    "mean_time_gap", "std_time_gap", "slider_ratio",
    "mean_angle", "std_angle",
    "sharp_angle_ratio", "square_angle_ratio", "wide_angle_ratio",
    "linear_angle_ratio", "vertical_jump_ratio", "perfect_overlap_ratio",
    "true_linear_sequence_ratio",
]
AGGREGATES = ["max", "mean", "std"]
FLAGS = ["has_peak_stream", "has_peak_jump", "is_hybrid"]


def label(idx: int, n_section: int) -> str:
    """Human-readable name for a final-vector index."""
    flags_start = n_section * len(AGGREGATES)
    if idx >= flags_start:
        f = idx - flags_start
        return FLAGS[f] if f < len(FLAGS) else f"flag[{f}]"
    agg = AGGREGATES[idx // n_section]
    slot = idx % n_section
    name = SECTION_FEATURES[slot] if slot < len(SECTION_FEATURES) else f"feat[{slot}]"
    return f"{agg}({name})"


def load(path: str):
    with open(path, encoding="utf-8") as f:
        return json.load(f)["features"]


def main():
    if len(sys.argv) < 3:
        print("usage: python compare_parity.py <python.json> <csharp.json> [atol] [rtol]",
              file=sys.stderr)
        raise SystemExit(2)

    py = load(sys.argv[1])
    cs = load(sys.argv[2])
    atol = float(sys.argv[3]) if len(sys.argv) > 3 else 1e-2
    rtol = float(sys.argv[4]) if len(sys.argv) > 4 else 1e-3

    if len(py) != len(cs):
        print(f"RESULT: FAIL -length mismatch: python={len(py)} csharp={len(cs)}")
        print("  (the two extractors produce different-size vectors -check FEATURE_COUNT "
              "in neural_model.py vs featureCount in FeatureExtractor.cs)")
        raise SystemExit(1)

    n = len(py)
    # Infer per-section feature count from the total: n = k*3 + 3  ->  k = (n-3)/3
    n_section = (n - 3) // 3 if (n - 3) % 3 == 0 else len(SECTION_FEATURES)

    rows = []
    fails = 0
    for i in range(n):
        diff = abs(py[i] - cs[i])
        tol = atol + rtol * abs(cs[i])
        ok = diff <= tol
        if not ok:
            fails += 1
        rows.append((diff, i, py[i], cs[i], ok))

    rows.sort(reverse=True)
    print(f"features: {n}  (per-section={n_section})   atol={atol}  rtol={rtol}")
    print(f"{'idx':>4}  {'feature':<28} {'python':>15} {'csharp':>15} {'abs_diff':>11}  status")
    print("-" * 86)
    for diff, i, x, y, ok in rows[:12]:
        print(f"{i:>4}  {label(i, n_section):<28} {x:>15.6g} {y:>15.6g} {diff:>11.3g}  "
              f"{'ok' if ok else 'FAIL'}")

    if fails:
        print(f"\nRESULT: FAIL -{fails} of {n} features exceed tolerance (worst shown above)")
        raise SystemExit(1)

    print(f"\nRESULT: PASS -all {n} features agree within tolerance")


if __name__ == "__main__":
    main()
