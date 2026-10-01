// ParityDump — emit the map feature vector for one .osu file (or a folder) as JSON.
//
// Mirrors the app's production path exactly:
//   v1: OsuParser.ReadFile -> ExtractRawHitObjects -> FeatureExtractor.SplitIntoSections
//       -> FeatureExtractor.AggregateMapFeatures
//   v2: OsuParser.ReadFile -> ExtractRawHitObjects / GetDifficulty / GetTimingPoints
//       -> FeatureExtractorV2.ExtractDouble
// (see OsuScoutNew/Services/OsuLibraryService.cs). Its Python counterpart is
// parity_dump.py in the training repo; compare the two dumps with compare_parity.py.
//
// v2 is dumped at full double precision (the app casts to float only for the ONNX
// input), so parity can be checked far tighter than v1's float32 path allows.
//
// Usage:
//   dotnet run --project parity/ParityDump -- [--feature-version 2] <path-to.osu> [out.json]
//   dotnet run --project parity/ParityDump -- --feature-version 2 --dir <folder> <out.json>

using System.Text.Json;
using OsuScout;

int featureVersion = 1;
var rest = new List<string>(args);
if (rest.Count >= 2 && rest[0] == "--feature-version")
{
    if (!int.TryParse(rest[1], out featureVersion) || featureVersion is < 1 or > 2)
    {
        Console.Error.WriteLine("--feature-version must be 1 or 2");
        return 2;
    }
    rest.RemoveRange(0, 2);
}

if (rest.Count >= 3 && rest[0] == "--dir")
{
    // Folder mode: { "<file name>": [features...] } for every .osu in the folder,
    // null for a map too short to measure.
    var result = new SortedDictionary<string, double[]>(StringComparer.Ordinal);
    foreach (var path in Directory.GetFiles(rest[1], "*.osu").OrderBy(p => p, StringComparer.Ordinal))
        result[Path.GetFileName(path)] = Dump(path, featureVersion);
    File.WriteAllText(rest[2], JsonSerializer.Serialize(new { source = "csharp", feature_version = featureVersion, maps = result }));
    Console.WriteLine($"wrote {result.Count} maps to {rest[2]}");
    return 0;
}

if (rest.Count < 1)
{
    Console.Error.WriteLine("usage: ParityDump [--feature-version 2] <path-to.osu> [out.json]");
    Console.Error.WriteLine("       ParityDump --feature-version 2 --dir <folder> <out.json>");
    return 2;
}

string osuPath = rest[0];
double[] vector = Dump(osuPath, featureVersion);

if (vector == null)
{
    Console.Error.WriteLine("ERROR: no sections/features produced (map too short or unparseable)");
    return 1;
}

object payload = featureVersion == 2
    ? new { source = "csharp", file = osuPath, length = vector.Length, features = vector,
            feature_version = 2, feature_names = FeatureExtractorV2.FeatureNames }
    : new { source = "csharp", file = osuPath, length = vector.Length, features = vector.Select(v => (float)v).ToArray() };

// WriteIndented for readability; System.Text.Json emits invariant-culture,
// round-trippable numbers by default.
string text = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });

if (rest.Count >= 2)
{
    File.WriteAllText(rest[1], text);
}

Console.WriteLine(text);
return 0;

static double[] Dump(string osuPath, int featureVersion)
{
    var parser = new OsuParser(osuPath);
    parser.ReadFile();
    var hitObjects = parser.ExtractRawHitObjects();

    if (featureVersion == 2)
        return FeatureExtractorV2.ExtractDouble(hitObjects, parser.GetDifficulty(), parser.GetTimingPoints());

    var sections = FeatureExtractor.SplitIntoSections(hitObjects);
    float[] v1 = FeatureExtractor.AggregateMapFeatures(sections);
    return v1?.Select(v => (double)v).ToArray();
}
