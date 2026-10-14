using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace PairUp.App.Audio;

/// <summary>Initialize only; callers retain control of volume checks and Play.</summary>
internal static class SharedAudioOutput
{
    public static WasapiOut Open(MMDevice device, IWaveProvider provider, int latencyMs,
        out bool compatibilityMode, CancellationToken cancellationToken = default)
    {
        compatibilityMode = false;
        try { return Initialize(device, provider, true, latencyMs, cancellationToken); }
        catch (Exception first) when (unchecked((uint)first.HResult) == 0x8889000F)
        {
            // A failed IAudioClient must be released before retrying. Do not restart
            // playback or change roles/volume. Polling avoids the event callback path;
            // 100ms gives a reconnecting endpoint more buffering headroom.
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var output = Initialize(device, provider, false, Math.Max(100, latencyMs), cancellationToken);
                compatibilityMode = true;
                return output;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception second)
            {
                throw new InvalidOperationException(
                    $"常规播放连接失败（0x{first.HResult:X8}）；兼容连接也失败（0x{second.HResult:X8}）。未开始播放。\n" +
                    "首次原因：" + first.Message + "\n兼容连接原因：" + second.Message, second);
            }
        }
    }

    private static WasapiOut Initialize(MMDevice device, IWaveProvider provider, bool eventSync,
        int latencyMs, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WasapiOut? output = null;
        try
        {
            output = new WasapiOut(device, AudioClientShareMode.Shared, eventSync, latencyMs);
            output.Init(provider);
            cancellationToken.ThrowIfCancellationRequested();
            return output;
        }
        catch
        {
            try { output?.Dispose(); } catch { /* Preserve the initialization failure. */ }
            throw;
        }
    }
}
