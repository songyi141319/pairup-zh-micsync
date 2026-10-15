using System.Text.Json;
using PairUp.App.Audio;

// Pure numeric bridge to the unchanged production estimator. No audio device APIs.
if (args.Length != 2 || args[0] is not ("probe" or "evaluate"))
    throw new ArgumentException("Usage: Baseline <probe|evaluate> <synthetic-fixture-directory>");
var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[1]));
Directory.CreateDirectory(root);
if (args[0] == "probe")
{
    using var writer = new BinaryWriter(File.Create(Path.Combine(root, "legacy-probe.bin")));
    foreach (var sample in AcousticDelayEstimator.CreateProbe(16000)) writer.Write(sample);
    return;
}
var cases = JsonSerializer.Deserialize<List<Case>>(File.ReadAllText(Path.Combine(root, "cases.json")))!;
var results = new List<object>();
foreach (var item in cases)
{
    var file = Path.GetFullPath(Path.Combine(root, item.File));
    if (!file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fixture outside root");
    var bytes = File.ReadAllBytes(file);
    if (bytes.Length == 0 || bytes.Length % 4 != 0) throw new InvalidDataException("Expected nonempty float32 fixture");
    var samples = new float[bytes.Length / 4];
    Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
    var match = AcousticDelayEstimator.Find(samples, 16000, 100, 1600);
    results.Add(new { item.Id, match.OffsetMs, match.IsReliable, match.Confidence, match.Reason });
}
File.WriteAllText(Path.Combine(root, "baseline.json"), JsonSerializer.Serialize(results));
Console.WriteLine($"Evaluated {results.Count} synthetic recordings using unchanged production estimator; no hardware.");
record Case(string Id, string File);
