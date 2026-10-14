using System.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace PairUp.App.Audio;

/// <summary>
/// One read-boundary snapshot controls spatial allocation and the existing output EQ/mono.
/// Calibration reads the same converted/resampled stream with both effects bypassed;
/// finishing restores the requested settings with fresh filter histories. The resampler
/// and original buffered source are retained so no user delay or device clock is changed.
/// </summary>
public sealed class SpatialOutputSampleProvider : ISampleProvider
{
    private sealed record Configuration(SpatialProfile Spatial, double BassGainDb = 0,
        double TrebleGainDb = 0, bool MonoDownmix = false, bool CalibrationBypass = false);
    private readonly SpatialSampleProvider _spatial;
    private readonly ISampleProvider _convertedSource;
    private readonly object _configurationGate = new();
    private Configuration _requested = new(new SpatialProfile());
    private Configuration? _applied;
    private DeviceProcessingSampleProvider _processing;

    public WaveFormat WaveFormat => _convertedSource.WaveFormat;

    public SpatialOutputSampleProvider(ISampleProvider source, int outputChannels, int outputSampleRate)
    {
        ArgumentNullException.ThrowIfNull(source);
        SpatialSampleProvider.ValidateChannels(source.WaveFormat.Channels);
        SpatialSampleProvider.ValidateChannels(outputChannels);
        if (outputSampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(outputSampleRate));
        _spatial = new SpatialSampleProvider(source);
        ISampleProvider chain = _spatial;
        if (source.WaveFormat.Channels == 1 && outputChannels == 2)
            chain = new MonoToStereoSampleProvider(chain);
        else if (source.WaveFormat.Channels == 2 && outputChannels == 1)
            chain = new StereoToMonoSampleProvider(chain);
        if (source.WaveFormat.SampleRate != outputSampleRate)
            chain = new WdlResamplingSampleProvider(chain, outputSampleRate);
        _convertedSource = chain;
        _processing = new DeviceProcessingSampleProvider(chain);
    }

    public SpatialProfile Spatial
    {
        get => Volatile.Read(ref _requested).Spatial;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            value.Validate();
            Update(current => current with { Spatial = value });
        }
    }

    public double BassGainDb
    {
        get => Volatile.Read(ref _requested).BassGainDb;
        set => Update(current => current with { BassGainDb = value });
    }

    public double TrebleGainDb
    {
        get => Volatile.Read(ref _requested).TrebleGainDb;
        set => Update(current => current with { TrebleGainDb = value });
    }

    public bool MonoDownmix
    {
        get => Volatile.Read(ref _requested).MonoDownmix;
        set => Update(current => current with { MonoDownmix = value });
    }

    public bool CalibrationBypass
    {
        get => Volatile.Read(ref _requested).CalibrationBypass;
        set { lock (_configurationGate) Volatile.Write(ref _requested, _requested with { CalibrationBypass = value }); }
    }

    private void Update(Func<Configuration, Configuration> change)
    {
        lock (_configurationGate)
        {
            if (_requested.CalibrationBypass) throw new InvalidOperationException("校准期间暂不调整音场或音色。");
            Volatile.Write(ref _requested, change(_requested));
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var configuration = Volatile.Read(ref _requested);
        if (_applied != configuration)
        {
            _spatial.Profile = configuration.Spatial;
            _spatial.IsBypassed = configuration.CalibrationBypass;
            if (_applied is null || _applied.BassGainDb != configuration.BassGainDb ||
                _applied.TrebleGainDb != configuration.TrebleGainDb || _applied.MonoDownmix != configuration.MonoDownmix ||
                _applied.CalibrationBypass != configuration.CalibrationBypass)
                _processing = new DeviceProcessingSampleProvider(_convertedSource)
                {
                    BassGainDb = configuration.BassGainDb,
                    TrebleGainDb = configuration.TrebleGainDb,
                    MonoDownmix = configuration.MonoDownmix
                };
            _applied = configuration;
        }
        return configuration.CalibrationBypass
            ? _convertedSource.Read(buffer, offset, count)
            : _processing.Read(buffer, offset, count);
    }
}
