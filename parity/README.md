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

## When to run

Any time you change the feature math — **before** you retrain/export/ship. Add a
feature (see the training repo's README), update both extractors, then run this to
confirm they still agree. Test a few maps of different styles (stream map, jump map,
tech map), not just one.
