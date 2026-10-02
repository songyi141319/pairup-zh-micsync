using System.IO;
using System.Text.Json;

namespace PairUp.App.Services;

public sealed record DeviceSettings(
    string DeviceId, bool IsConnected, double Volume, double LatencyMs, bool IsFavorite = false,
    double BassBoost = 0, double Treble = 0, bool IsMono = false);

public sealed record AppSettings(double MasterVolume, List<DeviceSettings> Devices);

public sealed class SettingsStore
{
    private readonly string FilePath;

    public SettingsStore(string? filePath = null) => FilePath = filePath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PairUp", "settings.json");

    public (double MasterVolume, Dictionary<string, DeviceSettings> Devices) Load()
    {
        if (!File.Exists(FilePath))
            return (100, new Dictionary<string, DeviceSettings>());

        string json;
        try
        {
            json = File.ReadAllText(FilePath);
        }
        catch
        {
            return (100, new Dictionary<string, DeviceSettings>());
        }

        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(json);
            if (settings is not null)
                return (settings.MasterVolume, settings.Devices.ToDictionary(s => s.DeviceId, StringComparer.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            // Pre-master-volume files were a flat array instead of {MasterVolume, Devices} — fall
            // back to that shape so upgrading the app doesn't silently wipe saved favorites/connections.
            try
            {
                var legacy = JsonSerializer.Deserialize<List<DeviceSettings>>(json);
                if (legacy is not null)
                    return (100, legacy.ToDictionary(s => s.DeviceId, StringComparer.OrdinalIgnoreCase));
            }
            catch (JsonException) { }
        }

        return (100, new Dictionary<string, DeviceSettings>());
    }

    public void Save(double masterVolume, IEnumerable<DeviceSettings> devices)
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);

        var settings = new AppSettings(masterVolume, devices.ToList());
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, json);
            File.Move(temporary, FilePath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
