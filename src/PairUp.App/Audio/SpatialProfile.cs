using System.Text.Json.Serialization;

namespace PairUp.App.Audio;

public enum SpatialRole
{
    FullRange,
    FrontLeft,
    FrontRight,
    Center,
    SurroundLeft,
    SurroundRight,
    Bass
}

/// <summary>
/// Music matrix and frequency allocation for one forwarded output. Zero disables a cutoff;
/// the role itself never guesses a speaker's bass capability or changes its timing/volume.
/// Missing settings retain the original stereo signal. This is not native surround decoding.
/// </summary>
public sealed record SpatialProfile(
    SpatialRole Role = SpatialRole.FullRange,
    double LowCutHz = 0,
    double HighCutHz = 0,
    double TrimDb = 0)
{
    [JsonIgnore]
    public bool IsBypass => Role == SpatialRole.FullRange && LowCutHz == 0 && HighCutHz == 0 && TrimDb == 0;

    public bool TryValidate(out string? error)
    {
        error = !Enum.IsDefined(Role) ? "音箱位置无效。"
            : !ValidCutoff(LowCutHz) || !ValidCutoff(HighCutHz) ? "截止频率必须为 0（关闭），或 20 到 20000 Hz 之间的有效数字。"
            : !double.IsFinite(TrimDb) || TrimDb < -24 || TrimDb > 0 ? "音场增益必须为 -24 到 0 dB 之间的有效数字。"
            : LowCutHz > 0 && HighCutHz > 0 && LowCutHz >= HighCutHz ? "低频截止必须小于高频截止。"
            : null;
        return error is null;
    }

    public void Validate()
    {
        if (!TryValidate(out var error)) throw new ArgumentException(error, nameof(SpatialProfile));
    }

    private static bool ValidCutoff(double hz) => double.IsFinite(hz) && (hz == 0 || hz >= 20 && hz <= 20000);
}
