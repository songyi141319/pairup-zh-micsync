using System.Reflection;
using NAudio.CoreAudioApi;
using PairUp.App.Audio;

// Read-only audio timing diagnostics. No output playback, microphone capture, or audio files.
using var enumerator = new MMDeviceEnumerator();
using var source = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
using var capture = new TimestampedAudioCapture(source.ID, true);
await capture.StartAsync(CancellationToken.None);
Console.WriteLine($"Reference observer ready: {source.FriendlyName}; {capture.SampleRate} Hz");
for (var i = 0; i < 15; i++)
{
    await Task.Delay(2000);
    var end = TimestampedAudioCapture.NowQpc;
    var audio = capture.Snapshot(end - 80000000, end);
    var match = AcousticDelayEstimator.Find(audio.Samples, audio.SampleRate, 0, 7600);
    Console.WriteLine($"[reference-{i}] peak={audio.Samples.Max(x => Math.Abs(x)):F6}; valid={audio.Coverage.Count(x => x == 1) / (double)audio.Coverage.Length:P2}; invalid={audio.Coverage.Count(x => x == 2) / (double)audio.Coverage.Length:P2}; confidence={match.Confidence:F4}; reliable={match.IsReliable}; offset={match.OffsetMs:F3}; continuous={audio.HasContinuousProbe(match.OffsetMs)}; reason={match.Reason}");
    var packets = (System.Collections.IEnumerable)typeof(TimestampedAudioCapture).GetField("_packets", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(capture)!;
    // Snapshot reflection while holding the same gate as the production capture worker.
    var gate = typeof(TimestampedAudioCapture).GetField("_gate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(capture)!;
    lock (gate)
    {
        var times = packets.Cast<object>().Select(p => (long)p.GetType().GetProperty("Start")!.GetValue(p)!).ToList();
        if (times.Count > 0) Console.WriteLine($"[timestamp-{i}] packets={times.Count}; newest_age_ms={(end - times[^1]) / 10000.0:F3}; oldest_age_ms={(end - times[0]) / 10000.0:F3}");
    }
}
