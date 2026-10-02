using NAudio.Wave;

namespace PairUp.App.Audio;

public sealed record AudioQueueSnapshot(double CurrentMs, double MinimumMs);

/// <summary>Producer backlog and intentional playback delay have independent storage.</summary>
public sealed class ChannelAudioBuffer
{
    private readonly BufferedWaveProvider _buffer;
    private readonly FixedDelaySampleProvider _delay;
    private readonly object _gate = new();
    private readonly ISampleProvider _sourceSamples;
    private double _minimumObservedMs = double.PositiveInfinity;
    private int _pendingTrimBytes;

    public ChannelAudioBuffer(WaveFormat sourceFormat)
    {
        _buffer = new BufferedWaveProvider(sourceFormat)
        {
            ReadFully = true,
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromMilliseconds(1500)
        };
        _sourceSamples = _buffer.ToSampleProvider();
        _delay = new FixedDelaySampleProvider(new ObservedSource(this));
    }

    public ISampleProvider Samples => _delay;
    public TimeSpan BufferedDuration => _buffer.BufferedDuration;
    public double DelayMs => _delay.DelayMs;
    public void SetDelay(double milliseconds) => _delay.SetDelay(milliseconds);

    public void Feed(byte[] data, int bytesRecorded)
    {
        lock (_gate)
        {
            var trim = Math.Min(_pendingTrimBytes, bytesRecorded);
            trim -= trim % _buffer.WaveFormat.BlockAlign;
            _pendingTrimBytes -= trim;
            if (trim < bytesRecorded) _buffer.AddSamples(data, trim, bytesRecorded - trim);
            ObserveBacklog();
        }
    }

    public void TrimBacklog(double milliseconds)
    {
        lock (_gate)
        {
            var bytes = AlignedBytes(milliseconds);
            // Multiple timer ticks cannot schedule deletion of more audio than is queued.
            _pendingTrimBytes = Math.Min(_buffer.BufferedBytes, _pendingTrimBytes + bytes);
        }
    }

    public void Nudge(double milliseconds)
    {
        lock (_gate)
        {
            var silence = new byte[AlignedBytes(milliseconds)];
            if (silence.Length > 0) _buffer.AddSamples(silence, 0, silence.Length);
            ObserveBacklog();
        }
    }

    public void Clear()
    {
        // Same order as Read: delay gate, then queue gate. Never invert these locks.
        _delay.Reset(() =>
        {
            lock (_gate)
            {
                _pendingTrimBytes = 0;
                _buffer.ClearBuffer();
                _minimumObservedMs = double.PositiveInfinity;
            }
        });
    }

    public AudioQueueSnapshot TakeSnapshot()
    {
        lock (_gate)
        {
            var current = _buffer.BufferedDuration.TotalMilliseconds;
            var minimum = double.IsFinite(_minimumObservedMs) ? Math.Min(current, _minimumObservedMs) : current;
            _minimumObservedMs = double.PositiveInfinity;
            return new(current, minimum);
        }
    }

    private void ObserveBacklog() => _minimumObservedMs = Math.Min(_minimumObservedMs, _buffer.BufferedDuration.TotalMilliseconds);

    private sealed class ObservedSource(ChannelAudioBuffer owner) : ISampleProvider
    {
        public WaveFormat WaveFormat => owner._buffer.WaveFormat;
        public int Read(float[] buffer, int offset, int count)
        {
            lock (owner._gate)
            {
                var read = owner._sourceSamples.Read(buffer, offset, count);
                owner.ObserveBacklog();
                return read;
            }
        }
    }

    private int AlignedBytes(double milliseconds)
    {
        if (!double.IsFinite(milliseconds) || milliseconds <= 0) return 0;
        var bytes = (int)(Math.Min(milliseconds, 1500) * _buffer.WaveFormat.AverageBytesPerSecond / 1000.0);
        return bytes - bytes % _buffer.WaveFormat.BlockAlign;
    }
}
