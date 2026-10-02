using NAudio.Wave;

namespace PairUp.App.Audio;

/// <summary>A persistent delay line, separate from the queue used to correct clock drift.</summary>
public sealed class FixedDelaySampleProvider : ISampleProvider
{
    public const int MaximumDelayMs = 3000;
    private readonly ISampleProvider _source;
    private readonly float[] _history;
    private readonly object _gate = new();
    private int _writeIndex;
    private int _delaySamples;

    public WaveFormat WaveFormat => _source.WaveFormat;
    public double DelayMs => Volatile.Read(ref _delaySamples) * 1000.0 / WaveFormat.SampleRate / WaveFormat.Channels;

    public FixedDelaySampleProvider(ISampleProvider source)
    {
        _source = source;
        _history = new float[(int)Math.Ceiling(source.WaveFormat.SampleRate * MaximumDelayMs / 1000.0 + 1) * source.WaveFormat.Channels];
    }

    public void SetDelay(double milliseconds)
    {
        var bounded = double.IsFinite(milliseconds) ? Math.Clamp(milliseconds, 0, MaximumDelayMs) : 0;
        var frames = (int)Math.Round(bounded * WaveFormat.SampleRate / 1000.0);
        Volatile.Write(ref _delaySamples, frames * WaveFormat.Channels);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        lock (_gate)
        {
            var read = _source.Read(buffer, offset, count);
            var delay = Volatile.Read(ref _delaySamples);
            for (var i = offset; i < offset + read; i++)
            {
                _history[_writeIndex] = buffer[i];
                var readIndex = _writeIndex - delay;
                if (readIndex < 0) readIndex += _history.Length;
                buffer[i] = _history[readIndex];
                if (++_writeIndex == _history.Length) _writeIndex = 0;
            }
            return read;
        }
    }

    public void Reset(Action? resetSource = null)
    {
        lock (_gate)
        {
            resetSource?.Invoke();
            Array.Clear(_history);
            _writeIndex = 0;
        }
    }
}
