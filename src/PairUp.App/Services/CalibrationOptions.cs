using System.IO;
using System.Text.Json;

namespace PairUp.App.Services;

public enum CalibrationVolumeMode { Automatic, Manual }

public sealed record CalibrationOptions(
    CalibrationVolumeMode Mode = CalibrationVolumeMode.Automatic,
    int ManualPercent = 20, int StartPercent = 20, int MaximumPercent = 50,
    bool UseTimeSchedule = false, int NightPercent = 10, int MorningPercent = 20, int DayPercent = 30,
    int NightMaximumPercent = 20, int MorningMaximumPercent = 30, int DayMaximumPercent = 50)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Mode) || ManualPercent is < 1 or > 100 || MaximumPercent is < 1 or > 50 ||
            StartPercent is < 1 or > 50 || NightPercent is < 1 or > 50 ||
            MorningPercent is < 1 or > 50 || DayPercent is < 1 or > 50 ||
            NightMaximumPercent is < 1 or > 50 || MorningMaximumPercent is < 1 or > 50 || DayMaximumPercent is < 1 or > 50)
            throw new InvalidOperationException("手动音量应为 1–100%，自动音量应为 1–50%。");
    }

    public int[] GetLevels(DateTime localTime)
    {
        Validate();
        if (Mode == CalibrationVolumeMode.Manual) return [ManualPercent];
        var start = UseTimeSchedule
            ? localTime.Hour < 8 || localTime.Hour >= 22 ? NightPercent : localTime.Hour < 12 ? MorningPercent : DayPercent
            : StartPercent;
        var maximum = UseTimeSchedule ? Math.Min(MaximumPercent,
            localTime.Hour < 8 || localTime.Hour >= 22 ? NightMaximumPercent : localTime.Hour < 12 ? MorningMaximumPercent : DayMaximumPercent)
            : MaximumPercent;
        start = Math.Min(start, maximum);
        var levels = new List<int> { start };
        while (levels[^1] < maximum) levels.Add(Math.Min(maximum, levels[^1] + 10));
        return levels.ToArray();
    }
}

public static class CalibrationPreferences
{
    private static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PairUp");
    public static CalibrationOptions Load()
    {
        try
        {
            var value = JsonSerializer.Deserialize<CalibrationOptions>(File.ReadAllText(Path.Combine(DirectoryPath, "calibration-options.json"))) ?? new();
            value.Validate();
            return value;
        }
        catch { return new(); }
    }

    public static void Save(CalibrationOptions value) { value.Validate(); Write("calibration-options.json", value); }
    public static int LoadAuditionPercent()
    {
        try
        {
            var value = JsonSerializer.Deserialize<int>(File.ReadAllText(Path.Combine(DirectoryPath, "audition-volume.json")));
            return value is >= 1 and <= 100 ? value : 20;
        }
        catch { return 20; }
    }
    public static void SaveAuditionPercent(int value)
    {
        if (value is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(value));
        Write("audition-volume.json", value);
    }
    private static void Write<T>(string name, T value)
    {
        Directory.CreateDirectory(DirectoryPath);
        var path = Path.Combine(DirectoryPath, name);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
