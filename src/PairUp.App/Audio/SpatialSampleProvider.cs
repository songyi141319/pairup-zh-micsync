using System.Threading;
using NAudio.Dsp;
using NAudio.Wave;

namespace PairUp.App.Audio;

/// <summary>
/// Allocates music before channel conversion. FullRange preserves L/R; FrontLeft=L;
/// FrontRight=R; Center/Bass=(L+R)/2; SurroundLeft=.65L-.35R;
/// SurroundRight=.65R-.35L. Each allocated signal is copied to both output channels.
/// Every matrix has an absolute coefficient sum at most one. Mono is interpreted as L=R,
/// so surround retains 0.3 of the mono content. No channel is claimed as a discrete 5.1 stem.
/// Optional second-order Butterworth high/low passes precede nonpositive trim. Active output
/// is limited to [-1, 1] to contain filter transients; original-sound bypass is bit-exact.
/// No delay line, device clock, source volume or resampling setting is changed here.
/// The consumer supplies complete interleaved frames, as the NAudio render chain does.
/// </summary>
public sealed class SpatialSampleProvider : ISampleProvider
{
    private sealed record Configuration(SpatialProfile Profile, bool IsBypassed);
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly object _configurationGate = new();
    private Configuration _requested;
    private Configuration? _active;
    private BiQuadFilter[]? _highPass;
    private BiQuadFilter[]? _lowPass;
    private double _gain = 1;

    public WaveFormat WaveFormat => _source.WaveFormat;

    public SpatialSampleProvider(ISampleProvider source, SpatialProfile? profile = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateChannels(source.WaveFormat.Channels);
        _source = source;
        _channels = source.WaveFormat.Channels;
        profile ??= new SpatialProfile();
        profile.Validate();
        _requested = new Configuration(profile, false);
    }

    public SpatialProfile Profile
    {
        get => Volatile.Read(ref _requested).Profile;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            value.Validate();
            lock (_configurationGate) Volatile.Write(ref _requested, _requested with { Profile = value });
        }
    }

    public bool IsBypassed
    {
        get => Volatile.Read(ref _requested).IsBypassed;
        set { lock (_configurationGate) Volatile.Write(ref _requested, _requested with { IsBypassed = value }); }
    }

    public static void ValidateChannels(int channels)
    {
        if (channels is not (1 or 2))
            throw new NotSupportedException("目前仅支持单声道和立体声音频；请在 Windows 声音设置中选择 1 或 2 声道。尚不支持多声道音源或输出。");
    }

    public int Read(float[] buffer, int offset, int count)
    {
        // Snapshot before reading upstream: a UI change cannot split one audio block.
        var configuration = Volatile.Read(ref _requested);
        var bypass = configuration.IsBypassed || configuration.Profile.IsBypass;
        if (!bypass && count % _channels != 0)
            throw new ArgumentException("音场处理需要完整的声道帧。", nameof(count));
        Apply(configuration);
        var read = _source.Read(buffer, offset, count);
        if (bypass) return read;
        if (read % _channels != 0)
            throw new InvalidOperationException("音源返回了不完整的声道帧，无法安全分配音场。");

        for (var i = 0; i < read; i += _channels)
        {
            var left = FiniteSample(buffer[offset + i]);
            var right = _channels == 2 ? FiniteSample(buffer[offset + i + 1]) : left;
            double assigned = configuration.Profile.Role switch
            {
                SpatialRole.FrontLeft => left,
                SpatialRole.FrontRight => right,
                SpatialRole.Center or SpatialRole.Bass => (left + right) * 0.5,
                SpatialRole.SurroundLeft => left * 0.65 - right * 0.35,
                SpatialRole.SurroundRight => right * 0.65 - left * 0.35,
                _ => left
            };
            buffer[offset + i] = Process((float)assigned, 0);
            if (_channels == 2)
                buffer[offset + i + 1] = Process((float)(configuration.Profile.Role == SpatialRole.FullRange ? right : assigned), 1);
        }
        return read;
    }

    private void Apply(Configuration configuration)
    {
        if (_active == configuration) return;
        // New filter state belongs exclusively to the playback thread. Profile replacements
        // and calibration transitions cannot race mutable filter histories or revive old tails.
        _highPass = CreateFilters(configuration.Profile.LowCutHz, highPass: true);
        _lowPass = CreateFilters(configuration.Profile.HighCutHz, highPass: false);
        _gain = Math.Pow(10, configuration.Profile.TrimDb / 20);
        _active = configuration;
    }

    private BiQuadFilter[]? CreateFilters(double cutoff, bool highPass)
    {
        if (cutoff == 0) return null;
        // Nyquist is half the actual source rate; keep coefficients below that limit even
        // when a saved 20 kHz cutoff is used with a low-rate mono source.
        var frequency = (float)Math.Min(cutoff, WaveFormat.SampleRate * 0.49);
        var filters = new BiQuadFilter[_channels];
        for (var channel = 0; channel < _channels; channel++)
            filters[channel] = highPass
                ? BiQuadFilter.HighPassFilter(WaveFormat.SampleRate, frequency, 0.70710678f)
                : BiQuadFilter.LowPassFilter(WaveFormat.SampleRate, frequency, 0.70710678f);
        return filters;
    }

    private float Process(float sample, int channel)
    {
        if (_highPass is not null) sample = _highPass[channel].Transform(sample);
        if (_lowPass is not null) sample = _lowPass[channel].Transform(sample);
        var trimmed = sample * _gain;
        return double.IsNaN(trimmed) ? 0 : (float)Math.Clamp(trimmed, -1, 1);
    }

    private static double FiniteSample(float sample) => float.IsFinite(sample) ? sample : 0;
}
