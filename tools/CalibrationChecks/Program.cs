using PairUp.App.Audio;
using NAudio.CoreAudioApi;
using PairUp.App.Services;
using System.Runtime.InteropServices;

const int rate = 48000;
const long epoch = 1000000000000;
var passed = 0;
var failed = 0;
void Check(string name, Action action)
{
    try { action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}
void Require(bool value, string message) { if (!value) throw new Exception(message); }

List<CapturePacket> Packets(double probeMs = 700, double gain = .1, int badPacket = -1,
    int missingPacket = -1, int invalidPacket = -1, double clockPpm = 0,
    int sampleRate = rate, long startQpc = epoch)
{
    var framesPerPacket = sampleRate / 100;
    var full = new float[sampleRate * 6];
    var probe = AcousticDelayEstimator.CreateProbe(sampleRate, (float)gain);
    Array.Copy(probe, 0, full, (int)Math.Round(probeMs * sampleRate / 1000), probe.Length);
    var packets = new List<CapturePacket>();
    for (var p = 0; p < 600; p++)
    {
        if (p == missingPacket) continue;
        // Contiguous device frames with sub-millisecond packet timestamp wobble;
        // an optional late 2ms outlier is outside the 700ms probe window.
        var jitterMs = .18 * Math.Sin(p * 1.9) + (p == badPacket ? 2 : 0);
        var time = startQpc + (long)Math.Round(p * 100000.0 * (1 + clockPpm / 1000000) + jitterMs * 10000);
        var flags = p == invalidPacket ? AudioClientBufferFlags.TimestampError : (AudioClientBufferFlags)0;
        packets.Add(new(time, full.AsSpan(p * framesPerPacket, framesPerPacket).ToArray(), p == invalidPacket,
            p * (long)framesPerPacket, flags, startQpc + p * 100000L + 100000));
    }
    return packets;
}
CapturedAudio Render(List<CapturePacket> packets, int sampleRate = rate, long startQpc = epoch)
{
    var samples = new float[sampleRate * 6];
    var coverage = new byte[samples.Length];
    var info = CaptureTimeline.Render(packets, sampleRate, startQpc, samples, coverage, out var raw);
    return new(samples, coverage, sampleRate, startQpc) { TimelineRepair = info, RawEvidence = raw };
}

Check("clean packet jitter preserves real 150ms acoustic offset", () =>
{
    var result = AcousticProbeAnalysis.Analyze(Render(Packets()), Render(Packets(850)));
    Require(Math.Abs(result.ArrivalMs - 150) < .25, "incorrect absolute timing " + result.ArrivalMs);
});
Check("a late timestamp outlier must not invalidate an earlier locally trustworthy probe", () =>
{
    var reference = Render(Packets(badPacket: 450));
    Require(reference.HasContinuousProbe(700), reference.DescribeProbeCoverage(700));
    var result = AcousticProbeAnalysis.Analyze(reference, Render(Packets(850)));
    Require(Math.Abs(result.ArrivalMs - 150) < .25, "unrelated late outlier biased the measured offset");
});
Check("missing probe samples still reject", () =>
    Require(!Render(Packets(missingPacket: 75)).HasContinuousProbe(700), "real sample gap was filled"));
Check("invalid driver timing inside probe still rejects", () =>
    Require(!Render(Packets(invalidPacket: 75)).HasContinuousProbe(700), "invalid timestamp was ignored"));
Check("a timestamp outlier inside the probe is not silently repaired", () =>
    Require(!Render(Packets(badPacket: 75)).HasContinuousProbe(700), "bad local anchor was accepted"));
Check("quiet directional microphone remains eligible for bounded weak-signal handling", () =>
{
    try { AcousticProbeAnalysis.Analyze(Render(Packets()), Render(Packets(850, .00001))); }
    catch (AcousticWeakSignalException) { return; }
    throw new Exception("very quiet microphone did not return the weak-signal classification");
});
Check("an audible directional microphone is recognized", () =>
{
    var result = AcousticProbeAnalysis.Analyze(Render(Packets()), Render(Packets(850, .005)));
    Require(Math.Abs(result.ArrivalMs - 150) < .25, "audible microphone did not retain delay");
});
foreach (var bad in new[] { 0, 300, 599 })
    Check($"outlier at packet {bad} does not bias a separate trusted probe", () =>
    {
        var result = AcousticProbeAnalysis.Analyze(Render(Packets(badPacket: bad)), Render(Packets(850)));
        Require(Math.Abs(result.ArrivalMs - 150) < .25, "bad anchor biased absolute timing");
    });
foreach (var shift in new[] { -3.0, 3.0 })
    Check($"a {shift}ms timestamp step crossing the probe remains rejected", () =>
    {
        var packets = Packets();
        for (var i = 80; i < packets.Count; i++) packets[i] = packets[i] with { Start = packets[i].Start + (long)(shift * 10000) };
        Require(!Render(packets).HasContinuousProbe(700), "clock step was smoothed into a valid probe");
    });
Check("probe crossing an independent fit seam remains rejected", () =>
{
    var packets = Packets(2900, badPacket: 450);
    packets[150] = packets[150] with { Start = packets[150].Start + 20000 };
    Require(!Render(packets).HasContinuousProbe(2900), "independent clock fit seam was hidden");
});
Check("raw clipping across artificial splits keeps the real device run identity", () =>
{
    var packets = Packets(badPacket: 450);
    packets[150] = packets[150] with { Start = packets[150].Start + 20000 };
    Array.Fill(packets[299].Samples, 1f, 360, 120);
    Array.Fill(packets[300].Samples, -1f, 0, 120);
    var audio = Render(packets);
    Require(audio.RawEvidence!.HasClipping(139200, 148800, rate), "splitting cleared the clipping window");
});
Check("different microphone sample rate and capture origin preserve absolute offset", () =>
{
    var micStart = epoch + 200000;
    var result = AcousticProbeAnalysis.Analyze(Render(Packets(badPacket: 450)),
        Render(Packets(850, sampleRate: 44100, startQpc: micStart), 44100, micStart));
    Require(Math.Abs(result.ArrivalMs - 170) < .3, "sample rate or start time was normalized away: " + result.ArrivalMs);
});
Check("rejected fit diagnostics include the actual nonzero reason", () =>
{
    var info = Render(Packets(badPacket: 450)).TimelineRepair!;
    Require(info.Rejections.Contains("时间锚残差超过0.75毫秒") && !info.Rejections.Contains("残差 0.000 毫秒"), "rejection cause lost");
});
Check("clipboard write-then-flush exception with exact content is not reported as failure", () =>
{
    var clipboard = "";
    var text = new string('声', 10000) + "\r\nEND";
    var result = DiagnosticClipboard.CopyAsync(text, value => { clipboard = value; throw new ExternalException("flush busy"); },
        () => clipboard, () => Task.CompletedTask).GetAwaiter().GetResult();
    Require(result.Status == DiagnosticCopyStatus.Verified && result.Characters == text.Length, "complete copy was reported as failure");
});
Check("truncated clipboard contents must not be reported as complete", () =>
{
    var clipboard = "";
    var result = DiagnosticClipboard.CopyAsync("complete log", value => clipboard = value[..3], () => clipboard,
        () => Task.CompletedTask).GetAwaiter().GetResult();
    Require(result.Status != DiagnosticCopyStatus.Verified, "truncated copy reported complete");
});
Check("temporary clipboard lock retries without hardware", () =>
{
    var clipboard = "old"; var calls = 0;
    var result = DiagnosticClipboard.CopyAsync("log", value => { if (++calls == 1) throw new ExternalException(); clipboard = value; },
        () => clipboard, () => Task.CompletedTask).GetAwaiter().GetResult();
    Require(result.Status == DiagnosticCopyStatus.Verified && calls == 2, "transient lock was not retried");
});
Check("clipboard stays uncertain when readback is unavailable, with bounded retries", () =>
{
    var writes = 0; var reads = 0;
    var result = DiagnosticClipboard.CopyAsync("log", _ => writes++, () => { reads++; throw new ExternalException(); },
        () => Task.CompletedTask).GetAwaiter().GetResult();
    Require(result.Status == DiagnosticCopyStatus.Unconfirmed && writes == 1 && reads == 3, "unverified success or repeated overwrite");
});
Check("persistent clipboard write contention ends after three attempts", () =>
{
    var writes = 0;
    var result = DiagnosticClipboard.CopyAsync("log", _ => { writes++; throw new ExternalException(); }, () => "other",
        () => Task.CompletedTask).GetAwaiter().GetResult();
    Require(result.Status != DiagnosticCopyStatus.Verified && writes == 3, "unbounded retries or false success");
});
Check("Bluetooth startup restoring the old 41.6% must settle at the requested 49% before measurement", () =>
{
    var actual = .416; var negotiated = false; var writes = 0;
    CalibrationStartup.PrepareAsync(() => negotiated = false, () => { actual = .493; writes++; },
        () => Require(Math.Abs(actual - .493) < .005, "startup reset the requested volume to " + actual),
        CancellationToken.None, (_, _) => { if (!negotiated) { actual = .416; negotiated = true; } return Task.CompletedTask; })
        .GetAwaiter().GetResult();
    Require(writes == 1, "normal startup should only apply the requested level once");
});
Check("late lower-baseline rollback is retried once at the same level while silent", () =>
{
    var reads = 0; var writes = 0; var retries = 0;
    CalibrationStartup.PrepareAsync(() => { }, () => writes++,
        () => { if (++reads == 2) throw new CalibrationStartupRollbackException("old level"); }, CancellationToken.None,
        (_, _) => Task.CompletedTask, () => { retries++; writes++; }).GetAwaiter().GetResult();
    Require(retries == 1 && writes == 2 && reads == 6, "must restart the full stability window exactly once");
});
Check("persistent rollback is bounded, with no audible measurement", () =>
{
    var retries = 0; var armed = false;
    try
    {
        CalibrationStartup.PrepareAsync(() => { }, () => { }, () => throw new CalibrationStartupRollbackException("old level"),
            CancellationToken.None, (_, _) => Task.CompletedTask, () => retries++).GetAwaiter().GetResult();
        armed = true;
    }
    catch (CalibrationStartupRollbackException) { }
    Require(retries == 1 && !armed, "unbounded retries or started an unstable measurement");
});
Check("mute or other changes cannot trigger a volume rewrite", () =>
{
    var retries = 0; var rejected = false;
    try { CalibrationStartup.PrepareAsync(() => { }, () => { }, () => throw new InvalidOperationException("muted"),
        CancellationToken.None, (_, _) => Task.CompletedTask, () => retries++).GetAwaiter().GetResult(); }
    catch (InvalidOperationException) { rejected = true; }
    Require(rejected && retries == 0, "external change was overwritten");
});
Check("only an exact lower baseline with agreeing controls qualifies for startup retry", () =>
{
    Require(CalibrationVolumeScope.CanRetryBaseline(.416f, .493f, .416f, .416f, false, false), "logged rollback not recognized");
    Require(!CalibrationVolumeScope.CanRetryBaseline(.6f, .493f, .6f, .6f, false, false), "raised level allowed");
    Require(!CalibrationVolumeScope.CanRetryBaseline(.416f, .493f, .3f, .3f, false, false), "arbitrary lowered level allowed");
    Require(!CalibrationVolumeScope.CanRetryBaseline(.416f, .493f, .416f, .416f, true, false), "mute allowed");
    Require(!CalibrationVolumeScope.CanRetryBaseline(.416f, .493f, .416f, .416f, false, true), "retained mute allowed");
    Require(!CalibrationVolumeScope.CanRetryBaseline(.416f, .493f, .416f, .493f, false, false), "disagreeing handles allowed");
    Require(!CalibrationVolumeScope.CanRetryBaseline(.416f, .493f, float.NaN, .416f, false, false), "invalid reading allowed");
});
Check("cancelled silent warmup never writes level or arms audio", () =>
{
    using var cancel = new CancellationTokenSource(); var writes = 0; var cancelled = false;
    try { CalibrationStartup.PrepareAsync(() => { }, () => writes++, () => { }, cancel.Token,
        (_, _) => { cancel.Cancel(); return Task.CompletedTask; }).GetAwaiter().GetResult(); }
    catch (OperationCanceledException) { cancelled = true; }
    Require(cancelled && writes == 0, "cancellation applied volume");
});
Check("preview emits silence until explicitly armed and then finishes its complete chime", () =>
{
    var chime = new DevicePreview.Chime(rate, 2); var buffer = new float[960];
    for (var i = 0; i < 200; i++) { Array.Fill(buffer, 1f); Require(chime.Read(buffer, 0, buffer.Length) == buffer.Length && buffer.All(x => x == 0), "warmup emitted sound"); }
    chime.Arm(); var total = 0; var heard = false;
    for (var i = 0; i < 300; i++)
    {
        var read = chime.Read(buffer, 0, buffer.Length); if (read == 0) break;
        total += read; heard |= buffer.Take(read).Any(x => Math.Abs(x) > .01);
    }
    Require(heard && total == (int)(rate * 1.8) * 2, "warmup consumed or truncated the chime");
});
Check("endpoint rollback combined with a changed app mixer cannot overwrite the mixer", () =>
{
    var writes = 0; var checkedEndpoints = false; var rejected = false;
    try { CalibrationStartup.RetryLevel(() => checkedEndpoints = true,
        () => throw new InvalidOperationException("app mixer muted or lowered"), () => writes++, CancellationToken.None); }
    catch (InvalidOperationException) { rejected = true; }
    Require(checkedEndpoints && rejected && writes == 0, "session change was overwritten by startup retry");
});
Check("cancellation during verification prevents the rollback write", () =>
{
    using var cancel = new CancellationTokenSource(); var writes = 0; var cancelled = false;
    try { CalibrationStartup.PrepareAsync(() => { }, () => { }, () => { cancel.Cancel(); throw new CalibrationStartupRollbackException("old"); },
        cancel.Token, (_, _) => Task.CompletedTask, () => writes++).GetAwaiter().GetResult(); }
    catch (OperationCanceledException) { cancelled = true; }
    Require(cancelled && writes == 0, "cancelled verification still rewrote level");
});
Check("cancellation during retry validation prevents applying the level", () =>
{
    using var cancel = new CancellationTokenSource(); var writes = 0; var cancelled = false;
    try { CalibrationStartup.RetryLevel(() => { }, cancel.Cancel, () => writes++, cancel.Token); }
    catch (OperationCanceledException) { cancelled = true; }
    Require(cancelled && writes == 0, "cancelled validation still rewrote level");
});
Console.WriteLine($"Results: {passed} passed, {failed} failed. Synthetic data only; no hardware, clipboard or desktop opened.");
return failed == 0 ? 0 : 1;
