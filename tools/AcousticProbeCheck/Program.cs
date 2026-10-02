using System.Reflection;
using System.Numerics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using PairUp.App.Audio;
using PairUp.App.Services;

// Hardware reproduction of the production reference/microphone probe. Metadata only.
// Machine-specific device identities must be supplied locally, never stored in the repository.
var sessionsOnly = args.Contains("--sessions-only");
var microphoneArgument = Array.IndexOf(args, "--microphone");
var microphoneId = microphoneArgument >= 0 && microphoneArgument + 1 < args.Length &&
    !args[microphoneArgument + 1].StartsWith("--", StringComparison.Ordinal)
    ? args[microphoneArgument + 1] : null;
if (args.Contains("--help") || (!sessionsOnly && string.IsNullOrWhiteSpace(microphoneId)))
{
    Console.WriteLine("用法：--microphone <本机麦克风设备标识> [--zero-source]；或 --sessions-only。");
    Console.WriteLine("默认不选择麦克风。声音测量请在用户确认方便后手动运行；日常校准使用主程序窗口。");
    return args.Contains("--help") ? 0 : 2;
}
using var enumerator = new MMDeviceEnumerator();
using var source = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
if (sessionsOnly)
{
    Console.WriteLine($"SOURCE: {source.FriendlyName}; mute={source.AudioEndpointVolume.Mute}; volume={source.AudioEndpointVolume.MasterVolumeLevelScalar:F3}");
    var liveSessions = source.AudioSessionManager.Sessions;
    for (var i = 0; i < liveSessions.Count; i++)
    {
        using var live = liveSessions[i];
        Console.WriteLine($"SESSION: pid={live.GetProcessID}; muted={live.SimpleAudioVolume.Mute}; volume={live.SimpleAudioVolume.Volume:F3}");
    }
    return 0;
}
var currentPairupPids = System.Diagnostics.Process.GetProcessesByName("PairUp.App").Select(p => p.Id).ToHashSet();
using var isolated = new ExistingPairupMuteScope(enumerator, currentPairupPids);
using var sourceQuiet = args.Contains("--zero-source") ? new SourceQuietScope(source.AudioEndpointVolume) : null;
var musicScopeType = typeof(AcousticCalibrator).GetNestedType("MusicMuteScope", BindingFlags.NonPublic)!;
using var musicMute = (IDisposable)musicScopeType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, new object[] { source.ID })!;
var sessions = source.AudioSessionManager.Sessions;
for (var i = 0; i < sessions.Count; i++)
{
    using var session = sessions[i];
    Console.WriteLine($"Session pid={session.GetProcessID}; muted={session.SimpleAudioVolume.Mute}");
}
using var reference = new TimestampedAudioCapture(source.ID, true);
using var microphone = new TimestampedAudioCapture(microphoneId!, false);
await reference.StartAsync(CancellationToken.None);
await microphone.StartAsync(CancellationToken.None);
await Task.Delay(400);
var format = source.AudioClient.MixFormat;
var signalType = typeof(AcousticCalibrator).GetNestedType("ProbeSampleProvider", BindingFlags.NonPublic)!;
var signal = (ISampleProvider)Activator.CreateInstance(signalType, format.SampleRate, format.Channels)!;
using var output = new WasapiOut(source, AudioClientShareMode.Shared, true, 10);
output.Init(signal);
var start = TimestampedAudioCapture.NowQpc;
output.Play();
try { await Task.Delay(4900); }
finally { output.Stop(); }
await Task.Delay(100);
var refAudio = reference.Snapshot(start, start + 49000000);
var micAudio = microphone.Snapshot(start, start + 49000000);
static ProbeMatch Report(string label, CapturedAudio audio, double min, double max)
{
    var match = AcousticDelayEstimator.Find(audio.Samples, audio.SampleRate, min, max);
    var first = Math.Max(0, (int)Math.Round(match.OffsetMs * audio.SampleRate / 1000));
    var count = (int)Math.Round(AcousticDelayEstimator.ProbeDurationSeconds * audio.SampleRate);
    var covered = audio.Coverage.Skip(first).Take(count).Count(x => x == 1);
    Console.WriteLine($"{label}: peak={audio.Samples.Max(x => Math.Abs(x)):F6}; confidence={match.Confidence:F5}; reliable={match.IsReliable}; offset_ms={match.OffsetMs:F3}; probe_coverage={covered / (double)count:P3}; continuous={audio.HasContinuousProbe(match.OffsetMs)}; reason={match.Reason}");
    return match;
}
var refMatch = Report("REFERENCE", refAudio, 200, 1500);
var micMatch = Report("MICROPHONE", micAudio, refMatch.OffsetMs - 100, refMatch.OffsetMs + 3900);
ReportPeaks("REFERENCE", refAudio, 200, 1500);
ReportPeaks("MICROPHONE", micAudio, refMatch.OffsetMs - 100, refMatch.OffsetMs + 3900);
var passed = refMatch.IsReliable && refAudio.HasContinuousProbe(refMatch.OffsetMs)
    && (args.Contains("--zero-source") || (micMatch.IsReliable && micAudio.HasContinuousProbe(micMatch.OffsetMs)));
Console.WriteLine(passed ? "PASS: source and microphone both verified" : "FAIL: calibration cannot reliably locate both probes");
return passed ? 0 : 1;

// Diagnostic metadata only: expose competing correlation peaks without retaining recordings.
static void ReportPeaks(string label, CapturedAudio audio, double minimumMs, double maximumMs)
{
    const int rate = AcousticDelayEstimator.AnalysisRate;
    var resampled = AcousticDelayEstimator.Resample(audio.Samples, audio.SampleRate, rate);
    var filter = typeof(AcousticDelayEstimator).GetMethod("RemoveOutOfBandNoise", BindingFlags.NonPublic | BindingFlags.Static)!;
    var input = (float[])filter.Invoke(null, new object[] { resampled })!;
    var probe = AcousticDelayEstimator.CreateProbe(rate);
    var first = Math.Max(0, (int)(minimumMs * rate / 1000));
    var last = Math.Min(input.Length - probe.Length, (int)(maximumMs * rate / 1000));
    var length = 1;
    while (length < input.Length + probe.Length - 1) length <<= 1;
    var signal = new Complex[length]; var template = new Complex[length];
    var mean = input.Average(x => (double)x);
    var energy = new double[input.Length + 1];
    for (var i = 0; i < input.Length; i++)
    {
        signal[i] = input[i] - mean;
        energy[i + 1] = energy[i] + signal[i].Real * signal[i].Real;
    }
    for (var i = 0; i < probe.Length; i++) template[i] = probe[probe.Length - 1 - i];
    var transform = typeof(AcousticDelayEstimator).GetMethod("Transform", BindingFlags.NonPublic | BindingFlags.Static)!;
    transform.Invoke(null, new object[] { signal, false });
    transform.Invoke(null, new object[] { template, false });
    for (var i = 0; i < length; i++) signal[i] *= template[i];
    transform.Invoke(null, new object[] { signal, true });
    var probeEnergy = probe.Sum(x => (double)x * x);
    var scores = new List<(int Index, double Score)>();
    for (var i = first; i <= last; i++)
    {
        var windowEnergy = energy[i + probe.Length] - energy[i];
        scores.Add((i, windowEnergy > 1e-9 ? Math.Min(1, Math.Abs(signal[i + probe.Length - 1].Real) / Math.Sqrt(probeEnergy * windowEnergy)) : 0));
    }
    var peaks = new List<(int Index, double Score)>();
    foreach (var candidate in scores.OrderByDescending(p => p.Score))
    {
        if (peaks.Any(p => Math.Abs(p.Index - candidate.Index) <= rate * 0.002)) continue;
        peaks.Add(candidate);
        if (peaks.Count == 6) break;
    }
    Console.WriteLine($"{label}_PEAKS: {string.Join("; ", peaks.Select(p => $"{p.Index * 1000.0 / rate:F3}ms={p.Score:F5}"))}");
    if (peaks.Count > 0)
    {
        var best = (int)Math.Round(peaks[0].Index * (double)audio.SampleRate / rate);
        var count = (int)Math.Round(AcousticDelayEstimator.ProbeDurationSeconds * audio.SampleRate);
        Console.WriteLine($"{label}_BEST_COVERAGE: valid={audio.Coverage.Skip(best).Take(count).Count(x => x == 1)}; missing={audio.Coverage.Skip(best).Take(count).Count(x => x == 0)}; invalid={audio.Coverage.Skip(best).Take(count).Count(x => x == 2)}");
    }
}

sealed class ExistingPairupMuteScope : IDisposable
{
    private readonly List<(AudioSessionControl Session, bool Mute)> _sessions = new();
    public ExistingPairupMuteScope(MMDeviceEnumerator enumerator, HashSet<int> pids)
    {
        try
        {
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                using (device)
                {
                    var sessions = device.AudioSessionManager.Sessions;
                    for (var i = 0; i < sessions.Count; i++)
                    {
                        var session = sessions[i];
                        if (!pids.Contains((int)session.GetProcessID)) { session.Dispose(); continue; }
                        _sessions.Add((session, session.SimpleAudioVolume.Mute));
                        session.SimpleAudioVolume.Mute = true;
                    }
                }
        }
        catch { Dispose(); throw; }
    }
    public void Dispose()
    {
        foreach (var (session, mute) in _sessions)
        {
            try { session.SimpleAudioVolume.Mute = mute; }
            catch { /* A session may expire while its app exits. */ }
            finally { session.Dispose(); }
        }
        _sessions.Clear();
    }
}

sealed class SourceQuietScope : IDisposable
{
    private readonly AudioEndpointVolume _endpoint;
    private readonly float _volume;
    private readonly bool _muted;
    public SourceQuietScope(AudioEndpointVolume endpoint)
    {
        _endpoint = endpoint; _volume = endpoint.MasterVolumeLevelScalar; _muted = endpoint.Mute;
        try { endpoint.Mute = false; endpoint.MasterVolumeLevelScalar = 0; }
        catch { Dispose(); throw; }
    }
    public void Dispose()
    {
        try { _endpoint.MasterVolumeLevelScalar = _volume; }
        finally { _endpoint.Mute = _muted; }
    }
}

