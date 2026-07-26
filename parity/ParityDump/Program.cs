// ParityDump — emit the aggregated map feature vector for one .osu file as JSON.
//
// Mirrors the app's production path exactly:
//   OsuParser.ReadFile -> ExtractRawHitObjects -> FeatureExtractor.SplitIntoSections
//   -> FeatureExtractor.AggregateMapFeatures
// (see OsuScoutNew/Services/OsuLibraryService.cs). Its Python counterpart is
// parity_dump.py in the training repo; compare the two dumps with compare_parity.py.
//
// Usage:
//   dotnet run --project parity/ParityDump -- <path-to.osu> [out.json]

using System.Text.Json;
using OsuScout;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: ParityDump <path-to.osu> [out.json]");
    return 2;
}

string osuPath = args[0];

var parser = new OsuParser(osuPath);
parser.ReadFile();
var hitObjects = parser.ExtractRawHitObjects();

var sections = FeatureExtractor.SplitIntoSections(hitObjects);
float[] vector = FeatureExtractor.AggregateMapFeatures(sections);

if (vector == null)
{
    Console.Error.WriteLine("ERROR: no sections/features produced (map too short or unparseable)");
    return 1;
}

var payload = new
{
    source = "csharp",
    file = osuPath,
    length = vector.Length,
    features = vector,
};

// WriteIndented for readability; System.Text.Json emits invariant-culture,
// round-trippable numbers by default.
string text = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });

if (args.Length >= 2)
{
    File.WriteAllText(args[1], text);
}

Console.WriteLine(text);
return 0;
