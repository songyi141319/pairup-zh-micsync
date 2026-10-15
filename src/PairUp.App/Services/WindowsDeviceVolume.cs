using NAudio.CoreAudioApi;

namespace PairUp.App.Services;

public sealed record DeviceVolumeState(double Percent, bool Muted);

/// <summary>Windows endpoint volume is the single source of truth, not an extra software gain.</summary>
public static class WindowsDeviceVolume
{
    // Identifies this process's ordinary endpoint writes in calibration notifications.
    internal static readonly Guid WriteContext = Guid.NewGuid();
    public static Dictionary<string, DeviceVolumeState> Read(IEnumerable<string> deviceIds)
    {
        var result = new Dictionary<string, DeviceVolumeState>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var id in deviceIds.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    using var device = enumerator.GetDevice(id);
                    if (device.State != DeviceState.Active) continue;
                    var volume = device.AudioEndpointVolume;
                    result[id] = new(volume.MasterVolumeLevelScalar * 100.0, volume.Mute);
                }
                catch { /* A disappearing device is unknown, not a zero-volume device. */ }
            }
        }
        catch { /* The audio service may be restarting. Preserve the last displayed values. */ }
        return result;
    }

    public static bool TrySet(string deviceId, double percent, out string? error)
    {
        error = null;
        if (!double.IsFinite(percent))
        {
            error = "音量必须是有效数字。";
            return false;
        }
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDevice(deviceId);
            if (device.State != DeviceState.Active) throw new InvalidOperationException("设备未连接");
            var volume = device.AudioEndpointVolume;
            var value = (float)(Math.Clamp(percent, 0, 100) / 100);
            if (Math.Abs(volume.MasterVolumeLevelScalar - value) < 0.0001f) return true;
            var wasMuted = volume.Mute;
            volume.NotificationGuid = WriteContext;
            try { volume.MasterVolumeLevelScalar = value; }
            finally
            {
                // Changing the percentage must not implicitly enable a muted speaker.
                if (volume.Mute != wasMuted) volume.Mute = wasMuted;
            }
            var actual = volume.MasterVolumeLevelScalar;
            if (!float.IsFinite(actual) || Math.Abs(actual - value) > 0.02f || volume.Mute != wasMuted)
                throw new InvalidOperationException($"请求 {value * 100:0.0}%，Windows 实际返回 {actual * 100:0.0}%；设备未确认音量或静音状态。");
            return true;
        }
        catch (Exception ex)
        {
            error = "无法同步设备的 Windows 音量：" + ex.Message;
            return false;
        }
    }
}
