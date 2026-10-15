using System.Reflection;
using System.Runtime.CompilerServices;
using NAudio.Wave;
using PairUp.App.Audio;
using PairUp.App.Services;

var failed = 0;
var passed = 0;
void Check(string name, Action check)
{
    try { check(); Console.WriteLine("PASS: " + name); passed++; }
    catch (Exception ex) { Console.WriteLine("FAIL: " + name + " — " + ex); failed++; }
}
void Require(bool condition, string detail) { if (!condition) throw new Exception(detail); }

static (OutputChannel Channel, ChannelAudioBuffer Buffer) MakeChannel(int channels = 1)
{
    var buffer = new ChannelAudioBuffer(WaveFormat.CreateIeeeFloatWaveFormat(48000, channels));
    // Exercise the real output queue and engine policy without opening any physical device.
    var channel = (OutputChannel)RuntimeHelpers.GetUninitializedObject(typeof(OutputChannel));
    // A queue fixture represents a running renderer. Never construct, start or dispose a
    // native output here: only the managed PlaybackState getter is used by engine policy.
    var output = (WasapiOut)RuntimeHelpers.GetUninitializedObject(typeof(WasapiOut));
    typeof(WasapiOut).GetField("playbackState", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(output, PlaybackState.Playing);
    typeof(OutputChannel).GetField("_output", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(channel, output);
    typeof(OutputChannel).GetField("_buffer", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(channel, buffer);
    typeof(OutputChannel).GetField("_volumeProvider", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(channel,
        new NAudio.Wave.SampleProviders.VolumeSampleProvider(buffer.Samples));
    return (channel, buffer);
}
static byte[] Bytes(float[] samples)
{
    var bytes = new byte[samples.Length * sizeof(float)];
    Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
    return bytes;
}
static float[] Recording(int rate, double delayMs, float gain = 1, double noise = 0.001, double echoMs = -1)
{
    var samples = new float[(int)(rate * 4.7)];
    var random = new Random(31415);
    for (var i = 0; i < samples.Length; i++) samples[i] = (float)((random.NextDouble() * 2 - 1) * noise);
    var probe = AcousticDelayEstimator.CreateProbe(rate, 0.1f * gain);
    AddProbe(samples, probe, rate, delayMs);
    if (echoMs >= 0) AddProbe(samples, probe.Select(x => x * 0.3f).ToArray(), rate, delayMs + echoMs);
    return samples;
}
static void AddProbe(float[] recording, float[] probe, int rate, double delayMs)
{
    var start = (int)Math.Round(delayMs * rate / 1000);
    for (var i = 0; i < probe.Length && i + start < recording.Length; i++) recording[i + start] += probe[i];
}

Check("3000 ms survives the former 1500 ms queue limit", () =>
{
    var (channel, core) = MakeChannel();
    channel.SetDelay(3000);
    var heard = -1;
    for (var block = 0; block < 320; block++)
    {
        var input = new float[480]; if (block == 0) input[0] = 1;
        var bytes = Bytes(input); channel.Feed(bytes, bytes.Length);
        core.Samples.Read(input, 0, input.Length);
        for (var i = 0; i < input.Length; i++) if (input[i] > 0.5) heard = block * 480 + i;
    }
    Require(heard == 144000, $"expected 144000 frames; heard {heard}");
});
Check("clock drift correction preserves 1000 ms after 12 seconds", () =>
{
    var slow = MakeChannel(); var fast = MakeChannel(); slow.Channel.SetDelay(1000);
    var engine = new AudioEngine();
    var channels = (Dictionary<string, OutputChannel>)typeof(AudioEngine).GetField("_channels", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(engine)!;
    channels.Add("slow", slow.Channel); channels.Add("fast", fast.Channel);
    var rebalance = typeof(AudioEngine).GetMethod("RebalanceChannels", BindingFlags.NonPublic | BindingFlags.Instance)!;
    slow.Buffer.Nudge(40);
    var heard = -1;
    for (var block = 0; block < 1200; block++)
    {
        var input = new float[480]; if (block == 800) input[0] = 1;
        var bytes = Bytes(input); slow.Channel.Feed(bytes, bytes.Length); fast.Channel.Feed(bytes, bytes.Length);
        if (block % 40 == 0) rebalance.Invoke(engine, null);
        slow.Buffer.Samples.Read(input, 0, input.Length);
        for (var i = 0; i < input.Length; i++) if (input[i] > 0.5) heard = block * 480 + i;
        fast.Buffer.Samples.Read(input, 0, input.Length);
    }
    Require(Math.Abs(heard - 432000) <= 480, $"expected 9 second arrival; heard frame {heard}");
    Require(slow.Buffer.DelayMs == 1000 && slow.Buffer.BufferedDuration.TotalMilliseconds <= 6, "manual delay was erased or real backlog not corrected");
});
Check("stereo delay preserves both channels", () =>
{
    var (channel, core) = MakeChannel(2); channel.SetDelay(25);
    var input = new float[4800]; input[0] = 0.75f; input[1] = -0.6f;
    var bytes = Bytes(input); channel.Feed(bytes, bytes.Length); core.Samples.Read(input, 0, input.Length);
    Require(input[2400] == 0.75f && input[2401] == -0.6f && input.Take(2400).All(x => x == 0), "stereo onset or channel order incorrect");
});
Check("delay bounds reject negative and nonfinite settings", () =>
{
    var (channel, core) = MakeChannel(); channel.SetDelay(7000); Require(core.DelayMs == 3000, "upper bound failed");
    channel.SetDelay(double.NaN); Require(core.DelayMs == 0, "NaN bound failed");
    channel.SetDelay(-10); Require(core.DelayMs == 0, "negative bound failed");
});
Check("calibration clears stale music in the delay history", () =>
{
    var (channel, core) = MakeChannel(); channel.SetDelay(1000);
    var prior = Enumerable.Repeat(0.75f, 4800).ToArray(); var bytes = Bytes(prior);
    channel.Feed(bytes, bytes.Length); core.Samples.Read(prior, 0, prior.Length); channel.ClearForCalibration();
    var silence = new float[96000]; core.Samples.Read(silence, 0, silence.Length);
    Require(silence.All(x => x == 0), "stale music leaked into calibration");
});
foreach (var rate in new[] { 16000, 44100, 48000 })
    Check($"noisy acoustic probe resolves 2750 ms at {rate} Hz", () =>
    {
        var match = AcousticDelayEstimator.Find(Recording(rate, 2750), rate);
        Require(match.IsReliable && Math.Abs(match.OffsetMs - 2750) < 0.3, $"{match.OffsetMs:0.000} ms, {match.Reason}");
    });
Check("microphone polarity does not alter acoustic delay", () =>
{
    var match = AcousticDelayEstimator.Find(Recording(48000, 950, -0.5f), 48000);
    Require(match.IsReliable && Math.Abs(match.OffsetMs - 950) < 0.3, match.Reason);
});
Check("a quieter echo does not replace the direct arrival", () =>
{
    var match = AcousticDelayEstimator.Find(Recording(48000, 1230, echoMs: 70), 48000);
    Require(match.IsReliable && Math.Abs(match.OffsetMs - 1230) < 1, $"{match.OffsetMs} ms, {match.Reason}");
});
Check("silence and random noise cannot auto apply a delay", () =>
{
    Require(!AcousticDelayEstimator.Find(new float[80000], 16000).IsReliable, "silence accepted");
    var noise = Recording(16000, 1000, 0, noise: 0.05);
    Require(!AcousticDelayEstimator.Find(noise, 16000).IsReliable, "random noise accepted");
});
Check("multiple equally loud probes are rejected", () =>
{
    var recording = Recording(16000, 400, noise: 0);
    AddProbe(recording, AcousticDelayEstimator.CreateProbe(16000), 16000, 2500);
    Require(!AcousticDelayEstimator.Find(recording, 16000).IsReliable, "ambiguous arrival accepted");
});
Check("clipped microphone audio is rejected", () =>
{
    var recording = Recording(16000, 1100, 30).Select(x => Math.Clamp(x, -1, 1)).ToArray();
    Require(!AcousticDelayEstimator.Find(recording, 16000).IsReliable, "clipped recording accepted");
});
Check("nearby strong echoes are rejected instead of choosing an uncertain peak", () =>
{
    foreach (var echoMs in new[] { 5, 10, 20 })
    {
        var recording = Recording(16000, 1000, noise: 0);
        AddProbe(recording, AcousticDelayEstimator.CreateProbe(16000), 16000, 1000 + echoMs);
        Require(!AcousticDelayEstimator.Find(recording, 16000).IsReliable, $"equal echo at {echoMs} ms accepted");
    }
});
Check("packet timestamps reject a missing part of the probe", () =>
{
    var coverage = Enumerable.Repeat((byte)1, 80000).ToArray();
    var capture = new CapturedAudio(new float[80000], coverage, 16000, 20000000);
    Require(capture.HasContinuousProbe(1000), "complete recording rejected");
    Array.Fill(coverage, (byte)0, 16500, 300);
    Require(!capture.HasContinuousProbe(1000), "capture gap accepted");
    Require(capture.AbsoluteMilliseconds(1000) == 3000, "QPC units incorrect");
});
Check("24 bit signed PCM decodes correctly", () =>
{
    var decoded = TimestampedAudioCapture.DecodeMono(new byte[] { 0, 0, 128, 255, 255, 127 }, new WaveFormat(48000, 24, 1));
    Require(decoded[0] == -1 && decoded[1] > 0.99999, "24 bit decoding incorrect");
});
Check("source referenced delays include compensation above one second", () =>
{
    var plan = AcousticAlignment.Plan("joy", new[] {
        new AcousticMeasurement("joy", "Joy", 2850, 0.8, 2), new AcousticMeasurement("aux", "AUX", 50, 0.9, 1),
        new AcousticMeasurement("cm", "CM510", 2500, 0.9, 4) });
    Require(plan.CanApply && plan.DelaysMs["aux"] == 2800 && plan.DelaysMs["cm"] == 350 && !plan.DelaysMs.ContainsKey("joy"), "delay plan incorrect");
});
Check("a slower routed speaker cannot produce a partial or negative plan", () =>
{
    var plan = AcousticAlignment.Plan("joy", new[] {
        new AcousticMeasurement("joy", "Joy", 100, 0.9, 1), new AcousticMeasurement("aux", "AUX", 20, 0.9, 1),
        new AcousticMeasurement("cm", "CM510", 107, 0.9, 1) });
    Require(!plan.CanApply && plan.DelaysMs.Count == 0, "negative or partial plan applied");
});
Check("unstable, duplicate, out of range and NaN measurements are rejected", () =>
{
    var source = new AcousticMeasurement("joy", "Joy", 3900, 0.9, 1);
    foreach (var pair in new[] {
        new[] { source, new AcousticMeasurement("aux", "AUX", 100, 0.9, 1) },
        new[] { source with { ArrivalMs = 1000 }, new AcousticMeasurement("aux", "AUX", 100, 0.9, 25) },
        new[] { source, source },
        new[] { source with { ArrivalMs = 1000 }, new AcousticMeasurement("aux", "AUX", 100, double.NaN, 1) },
        new[] { source with { ArrivalMs = 1000 }, new AcousticMeasurement("aux", "AUX", 100, 0.9, double.NaN) } })
    {
        var plan = AcousticAlignment.Plan("joy", pair);
        Require(!plan.CanApply && plan.DelaysMs.Count == 0, "invalid measurement produced a plan");
    }
});
Check("settings round trip retains 3000 ms and a failed save retains the old file", () =>
{
    var directory = Path.Combine(Path.GetTempPath(), "PairUpSyncCheck-" + Guid.NewGuid().ToString("N"));
    var path = Path.Combine(directory, "settings.json"); var store = new SettingsStore(path);
    try
    {
        store.Save(22, new[] { new DeviceSettings("AUX", true, 60, 3000) });
        var loaded = store.Load(); Require(loaded.MasterVolume == 22 && loaded.Devices["aux"].LatencyMs == 3000, "round trip lost settings");
        var rejected = false;
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            try { store.Save(5, new[] { new DeviceSettings("aux", true, 30, 10) }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { rejected = true; }
        loaded = store.Load();
        Require(rejected && loaded.MasterVolume == 22 && loaded.Devices["aux"].LatencyMs == 3000, "failed save changed prior settings");
    }
    finally { if (File.Exists(path)) File.Delete(path); if (Directory.Exists(directory)) Directory.Delete(directory); }
});
Check("fine delay adjustment supports 0.1 ms without floating point accumulation", () =>
{
    var device = new AudioDeviceInfo { Id = "aux", Name = "AUX", Kind = DeviceKind.Wired, State = NAudio.CoreAudioApi.DeviceState.Active,
        IsConnected = true, LatencyMs = 187 };
    for (var i = 0; i < 10; i++) device.AdjustDelay(0.1);
    Require(device.LatencyMs == 188, "ten tenth-ms steps did not total one ms");
    for (var i = 0; i < 10; i++) device.AdjustDelay(-0.1);
    Require(device.LatencyMs == 187, "reverse steps drifted");
    device.AdjustDelay(1); Require(device.LatencyMs == 188, "one ms step failed");
    device.LatencyMs = 3000; device.AdjustDelay(0.1); Require(device.LatencyMs == 3000, "upper bound failed");
    device.LatencyMs = 0; device.AdjustDelay(-1); Require(device.LatencyMs == 0, "lower bound failed");
    device.LatencyMs = 187; device.AdjustDelay(double.NaN); Require(device.LatencyMs == 187, "invalid step altered delay");
});
Check("tenth-ms fine adjustment reaches the actual audio delay line", () =>
{
    var device = new AudioDeviceInfo { Id = "aux", Name = "AUX", Kind = DeviceKind.Wired, State = NAudio.CoreAudioApi.DeviceState.Active,
        IsConnected = true, LatencyMs = 187 };
    device.AdjustDelay(0.1);
    var (channel, core) = MakeChannel(2); channel.SetDelay(device.LatencyMs);
    var input = new float[20000]; input[0] = 1; input[1] = -1;
    var bytes = Bytes(input); channel.Feed(bytes, bytes.Length); core.Samples.Read(input, 0, input.Length);
    var frame = (int)Math.Round(187.1 * 48000 / 1000);
    Require(input[frame * 2] == 1 && input[frame * 2 + 1] == -1, "fine setting was lost before audio output");
    Require(Math.Abs(core.DelayMs - 187.1) <= 1000.0 / 48000, "audio delay precision exceeds one sample frame");
});
Check("out of band room noise does not hide a clear probe or become one", () =>
{
    const int rate = 48000;
    var input = Recording(rate, 1871.4, noise: 0.001);
    for (var i = 0; i < input.Length; i++) input[i] += (float)(0.3 * Math.Sin(2 * Math.PI * 100 * i / rate));
    var match = AcousticDelayEstimator.Find(input, rate, 500, 3500);
    Require(match.IsReliable && Math.Abs(match.OffsetMs - 1871.4) < 0.15, $"low frequency noise hid the probe: {match.Confidence:F3}, {match.Reason}");
    var noise = new float[input.Length];
    for (var i = 0; i < noise.Length; i++) noise[i] = (float)(0.3 * Math.Sin(2 * Math.PI * 100 * i / rate));
    Require(!AcousticDelayEstimator.Find(noise, rate, 500, 3500).IsReliable, "room hum was misidentified as a probe");
});
Check("automatic alignment preserves tenth-ms compensation", () =>
{
    var plan = AcousticAlignment.Plan("joy", new[] {
        new AcousticMeasurement("joy", "Joy", 400.2, 0.9, 1), new AcousticMeasurement("aux", "AUX", 213.1, 0.9, 1) });
    Require(plan.CanApply && Math.Abs(plan.DelaysMs["aux"] - 187.1) < 0.001, "automatic result was rounded to whole milliseconds");
});
Check("room hum cannot disguise a probe below the minimum amplitude", () =>
{
    const int rate = 48000;
    var input = Recording(rate, 1400, gain: 0.001f, noise: 0);
    for (var i = 0; i < input.Length; i++) input[i] += (float)(0.3 * Math.Sin(2 * Math.PI * 100 * i / rate));
    var match = AcousticDelayEstimator.Find(input, rate, 500, 3500);
    Require(!match.IsReliable, "strong out of band noise made a nearly inaudible probe reliable");
});
Check("clipping between resampling frames is still rejected", () =>
{
    const int rate = 48000;
    var input = Recording(rate, 1500, noise: 0);
    var first = (int)(1.5 * rate); var count = (int)(rate * AcousticDelayEstimator.ProbeDurationSeconds);
    for (var i = first; i < first + count; i++) if (i % 3 == 1) input[i] = i % 2 == 0 ? 1 : -1;
    Require(!AcousticDelayEstimator.Find(input, rate, 500, 3500).IsReliable, "resampling concealed clipping in the original recording");
});
foreach (var channelCount in new[] { 1, 2 })
Check($"{channelCount} slower-clock output queues cannot accumulate 250 ms together", () =>
{
    var engine = new AudioEngine();
    var dictionary = (Dictionary<string, OutputChannel>)typeof(AudioEngine).GetField("_channels", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(engine)!;
    var outputs = Enumerable.Range(0, channelCount).Select(_ => MakeChannel()).ToArray();
    for (var i = 0; i < outputs.Length; i++) { outputs[i].Channel.SetDelay(187.1); dictionary.Add(i.ToString(), outputs[i].Channel); }
    var rebalance = typeof(AudioEngine).GetMethod("RebalanceChannels", BindingFlags.NonPublic | BindingFlags.Instance)!;
    var input = Bytes(new float[480]); var pulled = new float[479];
    for (var block = 0; block < 12000; block++)
    {
        foreach (var item in outputs) { item.Channel.Feed(input, input.Length); item.Buffer.Samples.Read(pulled, 0, pulled.Length); }
        if (block % 40 == 0) rebalance.Invoke(engine, null);
    }
    Require(outputs.All(x => x.Buffer.BufferedDuration.TotalMilliseconds < 70), $"common backlog reached {outputs.Max(x => x.Buffer.BufferedDuration.TotalMilliseconds):F1} ms");
    Require(outputs.All(x => Math.Abs(x.Buffer.DelayMs - 187.1) <= 1000.0 / 48000), "queue correction changed the user's delay");
});
Check("capture handler is wired exactly once before any device starts", () =>
{
    var engine = new AudioEngine();
    var capture = (AudioCaptureEngine)typeof(AudioEngine).GetField("_capture", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(engine)!;
    var callbacks = (Delegate?)typeof(AudioCaptureEngine).GetField("DataAvailable", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(capture);
    Require(callbacks?.GetInvocationList().Length == 1, "capture handler is not wired once at construction");
});
Check("time separated output probe is distinguished from the still audible source", () =>
{
    const int rate = 16000;
    var reference = new CapturedAudio(Recording(rate, 700), Enumerable.Repeat((byte)1, 75200).ToArray(), rate, 20000000);
    var received = Recording(rate, 1100, noise: 0.001);
    AddProbe(received, AcousticDelayEstimator.CreateProbe(rate), rate, 2900);
    var microphone = new CapturedAudio(received, Enumerable.Repeat((byte)1, received.Length).ToArray(), rate, 20000000);
    var timing = AcousticProbeAnalysis.Analyze(reference, microphone, 2000);
    Require(Math.Abs(timing.ArrivalMs - 200) < 0.15, $"known separation was not removed: {timing.ArrivalMs:F3}");
});
Check("normal 20 ms feeding and 80 ms playback cannot be mistaken for accumulated delay", () =>
{
    var engine = new AudioEngine();
    var dictionary = (Dictionary<string, OutputChannel>)typeof(AudioEngine).GetField("_channels", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(engine)!;
    var (channel, core) = MakeChannel(); dictionary.Add("batch", channel);
    var rebalance = typeof(AudioEngine).GetMethod("RebalanceChannels", BindingFlags.NonPublic | BindingFlags.Instance)!;
    var input = Bytes(Enumerable.Repeat(0.5f, 960).ToArray()); var pulled = new float[3840];
    for (var block = 1; block <= 2000; block++)
    {
        channel.Feed(input, input.Length);
        // Every status timer fires at the fullest point, immediately before the large read.
        if (block % 20 == 0) rebalance.Invoke(engine, null);
        if (block % 4 == 0)
        {
            core.Samples.Read(pulled, 0, pulled.Length);
            Require(pulled.All(x => x == 0.5f), "normal batching lost samples during queue correction");
        }
    }
    Require(core.BufferedDuration == TimeSpan.Zero, "correctly paced playback accumulated backlog");
});
Check("different probe separations reject a silent target's late source echo", () =>
{
    const int rate = 16000;
    var refSamples = Recording(rate, 700);
    var reference = new CapturedAudio(refSamples, Enumerable.Repeat((byte)1, refSamples.Length).ToArray(), rate, 20000000);
    var arrivals = new List<double>();
    for (var trial = 0; trial < 3; trial++)
    {
        var received = Recording(rate, 1000, noise: 0.001);
        AddProbe(received, AcousticDelayEstimator.CreateProbe(rate, 0.03f), rate, 2900);
        var microphone = new CapturedAudio(received, Enumerable.Repeat((byte)1, received.Length).ToArray(), rate, 20000000);
        arrivals.Add(AcousticProbeAnalysis.Analyze(reference, microphone, 2000 + trial * 100).ArrivalMs);
    }
    var spread = arrivals.Max() - arrivals.Min();
    var plan = AcousticAlignment.Plan("joy", new[] {
        new AcousticMeasurement("joy", "Joy", 300, 0.9, 1),
        new AcousticMeasurement("aux", "AUX", arrivals[1], 0.9, spread) });
    Require(spread > 190 && !plan.CanApply && plan.DelaysMs.Count == 0, "a late source echo produced a false alignment plan");
});
Check("a real target remains stable after three different known separations", () =>
{
    const int rate = 16000;
    var refSamples = Recording(rate, 700);
    var reference = new CapturedAudio(refSamples, Enumerable.Repeat((byte)1, refSamples.Length).ToArray(), rate, 20000000);
    var arrivals = new List<double>();
    for (var trial = 0; trial < 3; trial++)
    {
        var separation = 2000 + trial * 100;
        var received = Recording(rate, 1100, noise: 0.001);
        AddProbe(received, AcousticDelayEstimator.CreateProbe(rate), rate, 900 + separation);
        var microphone = new CapturedAudio(received, Enumerable.Repeat((byte)1, received.Length).ToArray(), rate, 20000000);
        arrivals.Add(AcousticProbeAnalysis.Analyze(reference, microphone, separation).ArrivalMs);
    }
    Require(arrivals.Max() - arrivals.Min() < 0.2 && arrivals.All(x => Math.Abs(x - 200) < 0.15), "a real output was changed by the artificial test delay");
});
Check("capture restart rejects changed sample rate, channel count or encoding", () =>
{
    var original = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    Require(AudioCaptureEngine.SameCaptureFormat(original, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)), "unchanged source format rejected");
    foreach (var changed in new[] {
        WaveFormat.CreateIeeeFloatWaveFormat(44100, 2), WaveFormat.CreateIeeeFloatWaveFormat(48000, 1),
        new WaveFormat(48000, 32, 2), new WaveFormat(48000, 24, 2) })
        Require(!AudioCaptureEngine.SameCaptureFormat(original, changed), "incompatible capture bytes could enter the old output chain");
});
Check("an asynchronous source failure silences outputs and discards old delayed sound", () =>
{
    var engine = new AudioEngine();
    var dictionary = (Dictionary<string, OutputChannel>)typeof(AudioEngine).GetField("_channels", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(engine)!;
    var (channel, core) = MakeChannel(); dictionary.Add("aux", channel);
    channel.SetDelay(215); channel.Volume = 0.7f;
    var input = Bytes(Enumerable.Repeat(0.5f, 960).ToArray()); channel.Feed(input, input.Length);
    var pulled = new float[960]; core.Samples.Read(pulled, 0, pulled.Length);
    channel.Feed(input, input.Length);
    var capture = (AudioCaptureEngine)typeof(AudioEngine).GetField("_capture", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(engine)!;
    var failedEvent = (Action<string>?)typeof(AudioCaptureEngine).GetField("CaptureFailed", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(capture);
    failedEvent!("disconnected during playback");
    Require(channel.Volume == 0 && core.BufferedDuration == TimeSpan.Zero && engine.CaptureError!.Contains("disconnected"), "source failure was not surfaced and silenced");
    pulled = new float[14400]; core.Samples.Read(pulled, 0, pulled.Length);
    Require(pulled.All(x => x == 0) && core.DelayMs == 215, "failure retained old sound or erased the user's manual setting");
});
Console.WriteLine($"Results: {passed} passed, {failed} failed. Physical audio devices were not opened.");
return failed == 0 ? 0 : 1;
