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
            0x88890008 when operation == "读取应用音量" => "Windows 在读取这个音频入口的应用音量时返回格式不兼容。尚未开始试听，也未设置试听音量；这不能证明测试音的格式有误。若名称含 Hands-Free，这是通话入口，听音乐请优先选择同名设备的扬声器或 Stereo 入口。",
            0x88890008 => "Windows 返回音频格式不兼容。若设备名称含 Hands-Free，这是通话入口，听音乐请优先选择同名设备的扬声器或 Stereo 入口；这不代表只能使用一个蓝牙音箱。",
            0x8889000A => "这台设备可能正被其他程序独占使用。请结束占用后重试。",
            _ => native.Message
        };
        return $"“{device}”{operation}失败：{explanation}\n错误代码：0x{native.HResult:X8}";
    }
}
