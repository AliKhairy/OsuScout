# Feature parity harness

The map feature vector is computed **twice** — once in Python during training
(`neural_model.ImprovedBeatmapClassifier.extract_meaningful_features`) and once in
C# at prediction time (`OsuScoutNew/FeatureExtractor.cs`). The trained model only
works if those two implementations produce the **same numbers in the same order**.
If they drift, predictions silently go wrong — no crash, no error.

This harness proves the two sides agree by running the **same `.osu` file** through
both and comparing all features.

## Pieces

| File | Repo | Role |
| --- | --- | --- |
| `parity_dump.py` | training (`osu-beatmap-classifier`) | dumps the Python feature vector as JSON |
| `parity/ParityDump/` | app (this repo) | console app that compiles the **real** `FeatureExtractor.cs` + `OsuParser.cs` and dumps the C# vector as JSON |
| `parity/compare_parity.py` | app (this repo) | compares the two dumps, names any mismatch by feature |

`ParityDump` includes the production source directly (`<Compile Include>`), so it can
never test a stale copy.

## Run it

Pick any `.osu` file (e.g. one from the training repo's `downloads/`), then:

```bash
# 1. Python side (from the training repo, using its venv so tensorflow/sklearn resolve)
cd /path/to/osu-beatmap-classifier
venv/Scripts/python parity_dump.py downloads/downloaded_1000740.osu py.json

# 2. C# side (from this repo)
cd /path/to/OsuScoutNew
dotnet run --project parity/ParityDump -- "C:\path\to\downloaded_1000740.osu" cs.json

# 3. Compare
python parity/compare_parity.py py.json cs.json
```

`RESULT: PASS` means the extractors agree within tolerance. `RESULT: FAIL` prints the
top diverging features (index + name + both values) so you know exactly what to fix.

## Tolerance

Default `atol=0.01, rtol=0.001`. Python uses float64, C# `float` is float32, so exact
equality is impossible — expected differences are ~1e-5. The tolerance absorbs that
while still catching real logic drift (which shows up as large diffs). Override:
`python parity/compare_parity.py py.json cs.json <atol> <rtol>`.

## Golden fixtures and CI

`.github/workflows/ci.yml` runs this check on every push and PR, but it does **not**
run the Python side — installing TensorFlow on every commit would make CI slow enough
that people stop paying attention to it. Instead the Python vectors were computed once
and committed:

| File | What it is |
| --- | --- |
| `fixtures/{stream,jump,tech}.osu` | three maps chosen for contrasting styles (deathstream / cross-screen jumps / tech) |
| `fixtures/{stream,jump,tech}.python.json` | the golden Python feature vectors for those maps |

CI builds `ParityDump`, runs it on each fixture, and compares against the golden.

**The one rule: if you change the feature math in Python, you must regenerate the
goldens.** Otherwise CI keeps comparing C# against a stale snapshot and passes while the
two implementations have actually drifted. Regenerate from the training repo:

```bash
python make_goldens.py /path/to/OsuScoutNew/parity/fixtures
```

Regenerating is a deliberate, reviewable step - the diff on those JSON files is exactly
the change in feature behaviour, and it should be inspected, not rubber-stamped.

## v2 features

`FeatureExtractorV2.cs` is the C# port of the training repo's `features_v2.py`
(72 features). Which extractor the app runs is set by `feature_version` in
`model_config.json`, so v1 model files keep working unchanged.

```bash
# Python side (training repo)
venv/Scripts/python parity_dump.py --feature-version 2 downloads/downloaded_1000740.osu py.json
# C# side (this repo)
dotnet run --project parity/ParityDump -- --feature-version 2 "C:/path/to/downloaded_1000740.osu" cs.json
python parity/compare_parity.py py.json cs.json 1e-9 1e-9
```

v2 is computed in double precision on both sides, so it is held to 1e-9 rather
than v1's 1e-2: measured over 4961 maps, the worst difference is 9e-13. CI checks
`fixtures/{stream,jump,tech}.python.v2.json` the same way it checks v1. Regenerate
them from the training repo with:

```bash
python make_goldens.py /path/to/OsuScoutNew/parity/fixtures --feature-version 2
```

`--dir <folder> <out.json>` (with `--feature-version 2`) dumps every `.osu` in a
folder in one run, for checking parity over many maps at once.

## When to run

Any time you change the feature math — **before** you retrain/export/ship. Add a
feature (see the training repo's README), update both extractors, then run this to
confirm they still agree. Test a few maps of different styles (stream map, jump map,
tech map), not just one.
