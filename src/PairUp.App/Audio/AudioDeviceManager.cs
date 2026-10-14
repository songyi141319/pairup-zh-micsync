using NAudio.CoreAudioApi;

namespace PairUp.App.Audio;

public sealed record OutputDeviceSnapshot(IReadOnlyList<AudioDeviceInfo> Devices, IReadOnlyList<string> Warnings)
{
    public bool IsComplete => Warnings.Count == 0;
}

public sealed class AudioDeviceManager
{
    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices() => GetOutputSnapshot().Devices;

    public OutputDeviceSnapshot GetOutputSnapshot()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active | DeviceState.Unplugged);

        var result = new List<AudioDeviceInfo>();
        var warnings = new List<string>();
        // An endpoint can disappear between enumeration and reading its properties. Keep the
        // other endpoints, and let the caller retain its old rows for an incomplete snapshot.
        for (var index = 0; index < devices.Count; index++)
        {
            try
            {
                using var device = devices[index];
                var id = device.ID;
                var name = device.FriendlyName;
                var state = device.State;
                result.Add(new AudioDeviceInfo { Id = id, Name = name, Kind = ClassifyDevice(name), State = state });
            }
            catch (Exception ex) { warnings.Add("有设备暂时无法读取，已保留上次信息：" + ex.Message); }
        }

        return new(result, warnings);
    }

    public string GetDefaultDeviceId()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        return device.ID;
    }

    public string? TryGetDefaultDeviceId(out string? warning)
    {
        try { warning = null; return GetDefaultDeviceId(); }
        catch (Exception ex)
        {
            warning = "当前没有可用的系统主音源，请连接原设备或选择其他主音源。" + ex.Message;
            return null;
        }
    }

    private static DeviceKind ClassifyDevice(string deviceName)
    {
        var name = deviceName.ToLowerInvariant();

        if (name.Contains("bluetooth") || name.Contains("hands-free") || name.Contains("bt "))
            return DeviceKind.Bluetooth;

        if (name.Contains("speaker") || name.Contains("soundbar") || name.Contains("hdmi") || name.Contains("optical"))
            return DeviceKind.Speakers;

        if (name.Contains("headphone") || name.Contains("headset") || name.Contains("jack") || name.Contains("line out"))
            return DeviceKind.Wired;

        return DeviceKind.Other;
    }
}
