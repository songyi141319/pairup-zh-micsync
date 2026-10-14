using System.Reflection;
using System.Runtime.CompilerServices;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using PairUp.App.Audio;
using PairUp.App.Services;

var passed = 0;
var failed = 0;
void Check(string name, Action check)
{
    try { check(); Console.WriteLine("PASS: " + name); passed++; }
    catch (Exception ex)
    {
        while (ex is TargetInvocationException && ex.InnerException is not null) ex = ex.InnerException;
        Console.WriteLine("FAIL: " + name + " — " + ex); failed++;
    }
}
static void Require(bool condition, string detail) { if (!condition) throw new Exception(detail); }
static void Near(float actual, double expected, string detail, double tolerance = 0.00001) =>
    Require(Math.Abs(actual - expected) <= tolerance, $"{detail}: expected {expected}, got {actual}");
static Type AudioType(string name) => typeof(AudioEngine).Assembly.GetType("PairUp.App.Audio." + name)
    ?? throw new Exception("Missing production audio type: " + name);
static object Profile(string role = "FullRange", double low = 0, double high = 0, double trim = 0) =>
    Activator.CreateInstance(AudioType("SpatialProfile"), Enum.Parse(AudioType("SpatialRole"), role), low, high, trim)!;
static ISampleProvider Spatial(ISampleProvider source, object? profile = null) =>
    (ISampleProvider)Activator.CreateInstance(AudioType("SpatialSampleProvider"), source, profile)!;
static ISampleProvider Chain(ISampleProvider source, int channels = 2, int rate = 48000) =>
    (ISampleProvider)Activator.CreateInstance(AudioType("SpatialOutputSampleProvider"), source, channels, rate)!;
static void Set(object target, string property, object value) =>
    (target.GetType().GetProperty(property) ?? throw new Exception("Missing property: " + property)).SetValue(target, value);
static object? Get(object target, string property) =>
    (target.GetType().GetProperty(property) ?? throw new Exception("Missing property: " + property)).GetValue(target);
static void Call(object target, string method, params object[] arguments) =>
    (target.GetType().GetMethod(method) ?? throw new Exception("Missing method: " + method)).Invoke(target, arguments);
static void Throws<T>(Action action, string reason) where T : Exception
{
    try { action(); }
    catch (Exception ex)
    {
        while (ex is TargetInvocationException && ex.InnerException is not null) ex = ex.InnerException;
        if (ex is T) return;
        throw new Exception($"{reason}: expected {typeof(T).Name}, got {ex.GetType().Name}", ex);
    }
    throw new Exception(reason + ": no exception was raised");
}
static float[] Read(ISampleProvider source, int count)
{
    var result = new float[count];
    Require(source.Read(result, 0, count) == count, "unexpected output length");
    return result;
}
static float[] Tone(double frequency, int channels = 2, int frames = 48000)
{
    var data = new float[frames * channels];
    for (var frame = 0; frame < frames; frame++)
        for (var channel = 0; channel < channels; channel++)
            data[frame * channels + channel] = (float)(0.5 * Math.Sin(2 * Math.PI * frequency * frame / 48000));
    return data;
}
static double Rms(float[] samples, int skip = 4800) =>
    Math.Sqrt(samples.Skip(skip).Average(x => (double)x * x));
static string SettingsFolder()
{
    // The build output is inside this checkout's writable root; never use real user settings.
    var folder = Path.Combine(AppContext.BaseDirectory, "isolated-settings", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(folder);
    return folder;
}

Check("default bypass preserves every sample, channel and offset exactly", () =>
{
    var data = new float[] { 0.75f, -0.25f, 1.25f, -1.25f, -0.0f, 0.125f };
    var provider = Spatial(new MemorySource(data, 2));
    var target = Enumerable.Repeat(9f, 10).ToArray();
    Require(provider.Read(target, 2, data.Length) == data.Length, "length changed");
    Require(data.Select(BitConverter.SingleToInt32Bits).SequenceEqual(target.Skip(2).Take(data.Length).Select(BitConverter.SingleToInt32Bits)), "bypass changed audio bits");
    Require(target[0] == 9 && target[1] == 9 && target[8] == 9 && target[9] == 9, "offset bounds were overwritten");
});

foreach (var (role, expected) in new[] {
    ("FrontLeft", 0.8), ("FrontRight", -0.2), ("Center", 0.3), ("Bass", 0.3),
    ("SurroundLeft", 0.59), ("SurroundRight", -0.41) })
{
    Check(role + " uses the agreed bounded stereo matrix", () =>
    {
        var output = Read(Spatial(new MemorySource(new[] { 0.8f, -0.2f }, 2), Profile(role)), 2);
        Near(output[0], expected, "left output"); Near(output[1], expected, "right output");
    });
}

Check("all roles keep defined mono behavior", () =>
{
    foreach (var role in new[] { "FullRange", "FrontLeft", "FrontRight", "Center", "Bass", "SurroundLeft", "SurroundRight" })
    {
        var expected = role.StartsWith("Surround") ? 0.24 : 0.8;
        Near(Read(Spatial(new MemorySource(new[] { 0.8f }, 1), Profile(role)), 1)[0], expected, role);
    }
});

Check("left allocation happens before stereo to mono conversion", () =>
{
    var chain = Chain(new MemorySource(new[] { 0.8f, -0.2f }, 2), 1);
    Set(chain, "Spatial", Profile("FrontLeft"));
    Near(Read(chain, 1)[0], 0.8, "left information was lost before allocation");
});

Check("mono to stereo duplicates allocated content", () =>
{
    var chain = Chain(new MemorySource(new[] { 0.8f }, 1));
    Set(chain, "Spatial", Profile("SurroundLeft"));
    var output = Read(chain, 2); Near(output[0], 0.24, "left"); Near(output[1], 0.24, "right");
});

Check("low pass retains bass and attenuates high frequencies", () =>
{
    var low = Tone(60); var high = Tone(4000);
    var bass = Rms(Read(Spatial(new MemorySource(low, 2), Profile("Bass", high: 160)), low.Length));
    var treble = Rms(Read(Spatial(new MemorySource(high, 2), Profile("Bass", high: 160)), high.Length));
    Require(bass > 0.33 && bass < 0.36 && treble < 0.002, $"unexpected bass/treble RMS: {bass}, {treble}");
});

Check("high pass retains treble and attenuates low frequencies", () =>
{
    var low = Tone(60); var high = Tone(4000);
    var bass = Rms(Read(Spatial(new MemorySource(low, 2), Profile(low: 500)), low.Length));
    var treble = Rms(Read(Spatial(new MemorySource(high, 2), Profile(low: 500)), high.Length));
    Require(bass < 0.006 && treble > 0.34 && treble < 0.36, $"unexpected bass/treble RMS: {bass}, {treble}");
});

Check("band pass applies both cutoffs independently", () =>
{
    var measurements = new List<double>();
    foreach (var hz in new[] { 40, 1000, 10000 })
    {
        var samples = Tone(hz);
        measurements.Add(Rms(Read(Spatial(new MemorySource(samples, 2), Profile(low: 300, high: 3000)), samples.Length)));
    }
    Require(measurements[0] < 0.01 && measurements[1] > 0.33 && measurements[2] < 0.025, string.Join(", ", measurements));
});

Check("negative trim attenuates without changing timing", () =>
{
    var output = Read(Spatial(new MemorySource(new[] { 1f, -1f, 0f, 0f }, 2), Profile(trim: -6)), 4);
    Near(output[0], 0.5011872, "gain"); Near(output[1], -0.5011872, "gain");
    Require(output[2] == 0 && output[3] == 0, "trim added delay/history");
});

Check("every active matrix and filtered transient stays finite and bounded", () =>
{
    var samples = new float[20000];
    for (var i = 0; i < samples.Length; i++) samples[i] = (i / 13 % 2 == 0) ? 1 : -1;
    foreach (var role in new[] { "FrontLeft", "FrontRight", "Center", "Bass", "SurroundLeft", "SurroundRight" })
    {
        var output = Read(Spatial(new MemorySource(samples, 2), Profile(role, 20, 20000)), samples.Length);
        Require(output.All(x => float.IsFinite(x) && Math.Abs(x) <= 1), role + " exceeded full scale");
    }
});

Check("invalid profiles are rejected before replacing valid audio", () =>
{
    var provider = Spatial(new MemorySource(new[] { 0.8f, -0.2f }, 2), Profile("FrontLeft"));
    foreach (var profile in new[] {
        Profile(low: double.NaN), Profile(high: double.PositiveInfinity), Profile(trim: double.NegativeInfinity),
        Profile(low: 19), Profile(high: 20001), Profile(trim: 0.1), Profile(trim: -24.1),
        Profile(low: 500, high: 500), Profile(low: 1000, high: 100), Profile("99") })
        Throws<ArgumentException>(() => Set(provider, "Profile", profile), "invalid spatial profile accepted");
    Near(Read(provider, 2)[0], 0.8, "invalid setting replaced the previous profile");
});

Check("profile changes are applied at the next read boundary", () =>
{
    var source = new MemorySource(new[] { 0.8f, -0.2f, 0.8f, -0.2f }, 2);
    var provider = Spatial(source, Profile("FrontLeft"));
    source.OnRead = () => Set(provider, "Profile", Profile("FrontRight"));
    Near(Read(provider, 2)[0], 0.8, "mid-read profile change affected current block");
    source.OnRead = null;
    Near(Read(provider, 2)[0], -0.2, "new profile was not active at next block");
});

Check("calibration bypasses matrix, filters, trim and old EQ then restores them", () =>
{
    var source = new MemorySource(Tone(4000, frames: 48000 * 3), 2);
    var chain = Chain(source);
    Set(chain, "Spatial", Profile("Bass", high: 160, trim: -12));
    Set(chain, "BassGainDb", 12.0); Set(chain, "TrebleGainDb", -12.0); Set(chain, "MonoDownmix", true);
    var before = Rms(Read(chain, 96000));
    Set(chain, "CalibrationBypass", true);
    var during = Rms(Read(chain, 96000));
    Set(chain, "CalibrationBypass", false);
    var after = Rms(Read(chain, 96000));
    Require(before < 0.002 && during > 0.35 && during < 0.36 && after < 0.002,
        $"RMS before/during/after: {before}, {during}, {after}");
    Require((double)Get(Get(chain, "Spatial")!, "HighCutHz")! == 160 && (double)Get(chain, "TrebleGainDb")! == -12,
        "calibration overwrote stored settings");
});

Check("calibration bypass retains original stereo channels", () =>
{
    var chain = Chain(new MemorySource(new[] { 0.8f, -0.2f }, 2));
    Set(chain, "Spatial", Profile("FrontRight")); Set(chain, "MonoDownmix", true); Set(chain, "CalibrationBypass", true);
    var output = Read(chain, 2); Near(output[0], 0.8, "left"); Near(output[1], -0.2, "right");
});

Check("unsupported channel layouts fail explicitly", () =>
{
    Throws<NotSupportedException>(() => Spatial(new MemorySource(new float[6], 6)), "six-channel input");
    Throws<NotSupportedException>(() => Chain(new MemorySource(new float[2], 2), 6), "six-channel output");
});

Check("original sound matches the previous conversion, resampling and EQ chain", () =>
{
    foreach (var (sourceChannels, targetChannels, rate) in new[] { (1, 2, 48000), (2, 1, 48000), (2, 2, 44100), (2, 1, 44100) })
    {
        var input = Tone(1000, sourceChannels);
        ISampleProvider original = new MemorySource(input, sourceChannels);
        if (sourceChannels == 1 && targetChannels == 2) original = new MonoToStereoSampleProvider(original);
        if (sourceChannels == 2 && targetChannels == 1) original = new StereoToMonoSampleProvider(original);
        if (rate != 48000) original = new WdlResamplingSampleProvider(original, rate);
        original = new DeviceProcessingSampleProvider(original) { BassGainDb = 4, TrebleGainDb = -3, MonoDownmix = true };
        var updated = Chain(new MemorySource(input, sourceChannels), targetChannels, rate);
        Set(updated, "BassGainDb", 4.0); Set(updated, "TrebleGainDb", -3.0); Set(updated, "MonoDownmix", true);
        var expected = Read(original, rate / 2 * targetChannels);
        var actual = Read(updated, expected.Length);
        Require(expected.Select(BitConverter.SingleToInt32Bits).SequenceEqual(actual.Select(BitConverter.SingleToInt32Bits)),
            $"original audio changed: {sourceChannels}->{targetChannels}, {rate} Hz");
    }
});

Check("saved cutoffs remain finite on lower sample-rate mono sources", () =>
{
    var input = Enumerable.Range(0, 8000).Select(i => (float)(0.5 * Math.Sin(2 * Math.PI * 1000 * i / 8000))).ToArray();
    var result = Read(Spatial(new MemorySource(input, 1, 8000), Profile(low: 20, high: 20000)), input.Length);
    Require(result.All(x => float.IsFinite(x) && Math.Abs(x) <= 1) && Rms(result, 1000) > 0.3, "cutoff above Nyquist corrupted samples");
});

Check("legacy JSON remains original sound without altering saved fields", () =>
{
    var folder = SettingsFolder();
    try
    {
        var path = Path.Combine(folder, "settings.json"); var store = new SettingsStore(path);
        const string devices = "[{\"DeviceId\":\"saved\",\"IsConnected\":true,\"Volume\":37,\"LatencyMs\":188.1,\"IsFavorite\":true,\"BassBoost\":4,\"Treble\":-3,\"IsMono\":true}]";
        foreach (var json in new[] { devices, "{\"MasterVolume\":61,\"Devices\":" + devices + "}" })
        {
            File.WriteAllText(path, json); var loaded = store.Load(); var saved = loaded.Devices["saved"];
            Require(saved.Volume == 37 && saved.LatencyMs == 188.1 && saved.IsConnected && saved.IsFavorite && saved.BassBoost == 4 && saved.Treble == -3 && saved.IsMono,
                "legacy settings changed");
            Require(Get(saved, "Spatial") is null, "missing spatial field was not treated as original sound");
        }
    }
    finally { Directory.Delete(folder, true); }
});

Check("spatial settings survive save and load", () =>
{
    var folder = SettingsFolder();
    try
    {
        var store = new SettingsStore(Path.Combine(folder, "settings.json"));
        var saved = new DeviceSettings("saved", true, 37, 188.1);
        Set(saved, "Spatial", Profile("SurroundRight", 120, 8000, -4));
        store.Save(61, new[] { saved }); var loaded = store.Load();
        Require(loaded.MasterVolume == 61, "master volume changed");
        var spatial = Get(loaded.Devices["saved"], "Spatial")!;
        Require(Get(spatial, "Role")!.ToString() == "SurroundRight" && (double)Get(spatial, "LowCutHz")! == 120 &&
            (double)Get(spatial, "HighCutHz")! == 8000 && (double)Get(spatial, "TrimDb")! == -4, "spatial round trip failed");
    }
    finally { Directory.Delete(folder, true); }
});

Check("invalid saved spatial settings fall back to original sound and preserve device preferences", () =>
{
    var folder = SettingsFolder();
    try
    {
        var path = Path.Combine(folder, "settings.json");
        File.WriteAllText(path, "{\"MasterVolume\":61,\"Devices\":[{\"DeviceId\":\"saved\",\"IsConnected\":true,\"Volume\":37,\"LatencyMs\":188.1,\"IsFavorite\":true,\"Spatial\":{\"Role\":99,\"LowCutHz\":1000,\"HighCutHz\":20,\"TrimDb\":12}}]}");
        var loaded = new SettingsStore(path).Load(); var saved = loaded.Devices["saved"];
        Require(loaded.MasterVolume == 61 && saved.Volume == 37 && saved.LatencyMs == 188.1 && saved.IsFavorite && saved.IsConnected,
            "invalid spatial settings erased existing preferences");
        Require(Get(saved, "Spatial") is null, "invalid saved profile reached playback");
    }
    finally { Directory.Delete(folder, true); }
});

Check("saving an invalid spatial profile cannot replace an existing settings file", () =>
{
    var folder = SettingsFolder();
    try
    {
        var path = Path.Combine(folder, "settings.json"); var store = new SettingsStore(path);
        File.WriteAllText(path, "{\"MasterVolume\":61,\"Devices\":[]}");
        var invalid = new DeviceSettings("invalid", true, 75, 0); Set(invalid, "Spatial", Profile(trim: 12));
        Throws<ArgumentException>(() => store.Save(75, new[] { invalid }), "invalid profile persisted");
        Require(store.Load().MasterVolume == 61, "failed save overwrote the last valid file");
    }
    finally { Directory.Delete(folder, true); }
});

Check("engine rejects spatial edits during calibration before any device access", () =>
{
    using var engine = new AudioEngine();
    typeof(AudioEngine).GetField("_isCalibrating", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(engine, true);
    Throws<InvalidOperationException>(() => Call(engine, "SetSpatial", "output", Profile("Bass", high: 160)), "calibration edit");
});

Check("source device cannot receive spatial processing", () =>
{
    using var engine = new AudioEngine();
    var capture = typeof(AudioEngine).GetField("_capture", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(engine)!;
    capture.GetType().GetProperty("SourceDeviceId")!.SetValue(capture, "source");
    Throws<InvalidOperationException>(() => Call(engine, "SetSpatial", "SOURCE", Profile("Bass", high: 160)), "source edit");
});

Check("engine calibration scope restores effects and manual delay on disposal", () =>
{
    using var engine = new AudioEngine();
    var channels = (Dictionary<string, OutputChannel>)typeof(AudioEngine).GetField("_channels", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(engine)!;
    var capture = typeof(AudioEngine).GetField("_capture", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(engine)!;
    capture.GetType().GetProperty("SourceDeviceId")!.SetValue(capture, "source");
    capture.GetType().GetProperty("IsCapturing")!.SetValue(capture, true); // Skip all physical device startup.
    var queue = new ChannelAudioBuffer(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
    var processing = Chain(queue.Samples);
    Set(processing, "Spatial", Profile("Bass", high: 160)); Set(processing, "TrebleGainDb", -12.0);
    var channel = (OutputChannel)RuntimeHelpers.GetUninitializedObject(typeof(OutputChannel));
    typeof(OutputChannel).GetField("_buffer", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(channel, queue);
    typeof(OutputChannel).GetField("_processingProvider", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(channel, processing);
    typeof(OutputChannel).GetField("_volumeProvider", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(channel, new VolumeSampleProvider(processing));
    channel.SetDelay(188.1); channels.Add("output", channel);
    try
    {
        using (engine.BeginAcousticCalibration())
        {
            Require(channel.DelayMs == 0, "calibration retained the previous manual delay");
            var probe = Tone(4000); var bytes = new byte[probe.Length * sizeof(float)]; Buffer.BlockCopy(probe, 0, bytes, 0, bytes.Length);
            channel.Feed(bytes, bytes.Length);
            Require(Rms(Read(processing, probe.Length)) > 0.35, "engine did not bypass the low pass/EQ");
            Throws<InvalidOperationException>(() => engine.SetBassBoost("output", 1), "bass edit during calibration");
            Throws<InvalidOperationException>(() => engine.SetTreble("output", 1), "treble edit during calibration");
            Throws<InvalidOperationException>(() => engine.SetMono("output", true), "mono edit during calibration");
        }
        Require(Math.Abs(channel.DelayMs - 188.1) < 0.03 && !engine.IsCalibrating, "scope failed to restore timing/state");
        var music = Tone(4000); var musicBytes = new byte[music.Length * sizeof(float)]; Buffer.BlockCopy(music, 0, musicBytes, 0, musicBytes.Length);
        channel.Feed(musicBytes, musicBytes.Length);
        Require(Rms(Read(processing, music.Length)) < 0.002, "engine did not restore stored effects");
    }
    finally { channels.Clear(); } // Fake channels intentionally own no native renderer to dispose.
});

Console.WriteLine($"Results: {passed} passed, {failed} failed. Only generated in-memory signals and isolated temporary JSON were used; no physical device was opened.");
return failed == 0 ? 0 : 1;

sealed class MemorySource(float[] samples, int channels, int rate = 48000) : ISampleProvider
{
    private int _position;
    public Action? OnRead { get; set; }
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);
    public int Read(float[] buffer, int offset, int count)
    {
        OnRead?.Invoke();
        var read = Math.Min(count, samples.Length - _position);
        Array.Copy(samples, _position, buffer, offset, read); _position += read; return read;
    }
}
