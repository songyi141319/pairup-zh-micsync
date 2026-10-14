namespace PairUp.App.Services;

/// <summary>Keep native endpoint failures distinct from an inferred Bluetooth disconnection.</summary>
internal static class AudioFailure
{
    public static string Describe(Exception error, string device, string operation)
    {
        var causes = new List<Exception>();
        for (Exception? current = error; current is not null; current = current.InnerException)
            causes.Add(current);
        var native = causes.FirstOrDefault(item => ((uint)item.HResult & 0xFFFF0000u) == 0x88890000u)
            ?? causes.Last();
        var explanation = unchecked((uint)native.HResult) switch
        {
            0x8889000F => "Windows 暂时无法建立这台设备的音频连接。设备可能刚重新连接或音频端点正在变化；这不代表只能使用一个蓝牙音箱。请先刷新设备，再重试。",
            0x88890008 => "设备不支持当前播放格式。请检查 Windows 中该设备的声音格式。",
            0x8889000A => "这台设备可能正被其他程序独占使用。请结束占用后重试。",
            _ => native.Message
        };
        return $"“{device}”{operation}失败：{explanation}\n错误代码：0x{native.HResult:X8}";
    }
}
