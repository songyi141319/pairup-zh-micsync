using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace PairUp.App.Services;

/// <summary>Native callbacks only queue a refresh; never enumerate or change audio here.</summary>
internal sealed class AudioDeviceWatcher : IMMNotificationClient, IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly Action _changed;
    private int _disposed;

    public AudioDeviceWatcher(Action changed)
    {
        _changed = changed;
        try { _enumerator.RegisterEndpointNotificationCallback(this); }
        catch { _enumerator.Dispose(); throw; }
    }

    private void Notify()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try { _changed(); } catch { /* Native notifications must not propagate application errors. */ }
    }
    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => Notify();
    public void OnDeviceAdded(string deviceId) => Notify();
    public void OnDeviceRemoved(string deviceId) => Notify();
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        // Windows can choose its replacement default after the removal notification.
        if (flow == DataFlow.Render && role == Role.Multimedia) Notify();
    }
    public void OnPropertyValueChanged(string deviceId, PropertyKey key) { }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _enumerator.UnregisterEndpointNotificationCallback(this); }
        finally { _enumerator.Dispose(); }
    }
}
