using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace PairUp.App.Services;

public sealed record WindowsRenderDefaults(string ConsoleId, string MultimediaId);

/// <summary>
/// Changes only the ordinary Windows playback roles, retaining Communications.
/// The COM GUIDs and vtable order are independently declared from the ABI documented at
/// https://github.com/tartakynov/audioswitch/blob/master/IPolicyConfig.h (EreTIk).
/// No upstream implementation is copied. IPolicyConfig is undocumented and can be unavailable;
/// callers must restore the saved roles and audio graph when any operation fails.
/// </summary>
public static class WindowsDefaultAudioDevice
{
    public static WindowsRenderDefaults Capture()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var console = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
        using var multimedia = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        return new(console.ID, multimedia.ID);
    }

    public static void Set(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) throw new ArgumentException("请选择有效的主音源。", nameof(deviceId));
        using (var enumerator = new MMDeviceEnumerator())
        using (var device = enumerator.GetDevice(deviceId))
            if (device.State != DeviceState.Active) throw new InvalidOperationException("所选主音源未连接。");
        WithPolicy(policy =>
        {
            SetRole(policy, deviceId, Role.Console);
            SetRole(policy, deviceId, Role.Multimedia);
        });
        var actual = Capture();
        if (!string.Equals(actual.ConsoleId, deviceId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(actual.MultimediaId, deviceId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Windows 尚未将所选设备设为主音源，已取消切换。");
    }

    public static void Restore(WindowsRenderDefaults defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        WithPolicy(policy =>
        {
            // Try both roles even when a disconnected old endpoint rejects the first one.
            var errors = new List<string>();
            foreach (var (id, role) in new[] { (defaults.ConsoleId, Role.Console), (defaults.MultimediaId, Role.Multimedia) })
                try { SetRole(policy, id, role); }
                catch (Exception ex) { errors.Add(ex.Message); }
            if (errors.Count > 0) throw new InvalidOperationException(string.Join("；", errors));
        });
        var actual = Capture();
        if (!string.Equals(actual.ConsoleId, defaults.ConsoleId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(actual.MultimediaId, defaults.MultimediaId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Windows 原默认播放设备未能完整恢复，请在声音设置中检查。");
    }

    private static void SetRole(IPolicyConfig policy, string id, Role role)
    {
        var result = policy.SetDefaultEndpoint(id, role);
        if (result < 0) throw new InvalidOperationException($"无法设置 Windows {role} 播放设备（0x{result:X8}）。", Marshal.GetExceptionForHR(result));
    }

    private static void WithPolicy(Action<IPolicyConfig> action)
    {
        object? instance = null;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9"), throwOnError: true)!;
            instance = Activator.CreateInstance(type) ?? throw new InvalidOperationException("Windows 播放设备控制不可用。");
            action((IPolicyConfig)instance);
        }
        finally
        {
            if (instance is not null && Marshal.IsComObject(instance)) Marshal.FinalReleaseComObject(instance);
        }
    }

    // Unused slots are required before SetDefaultEndpoint to retain the native vtable order.
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int useDefault, out IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, int useDefault, out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, ref long period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, Role role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);
    }
}
