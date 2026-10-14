using System.IO;
using System.Text.Json;

namespace PairUp.App.Services;

/// <summary>Local labels keyed by the exact Windows endpoint ID; audio settings are separate.</summary>
public sealed class DeviceNotesStore
{
    public const int MaximumLength = 80;
    private readonly string _filePath;

    public DeviceNotesStore(string? filePath = null) => _filePath = filePath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PairUp", "device-notes.json");

    public static string Normalize(string? value)
    {
        var note = (value ?? string.Empty).Trim();
        if (note.Length > MaximumLength) throw new InvalidOperationException("设备备注最多 80 个字。");
        return note;
    }

    public Dictionary<string, string> Load()
    {
        string json;
        try { json = File.ReadAllText(_filePath); }
        catch (FileNotFoundException) { return new(StringComparer.Ordinal); }
        catch (DirectoryNotFoundException) { return new(StringComparer.Ordinal); }
        // Invalid files remain untouched. Saving re-reads them and reports the problem
        // instead of replacing another device's labels with an empty collection.
        var stored = JsonSerializer.Deserialize<Dictionary<string, string?>>(json)
            ?? throw new JsonException("设备备注文件内容无效。");
        var notes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, value) in stored)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new JsonException("设备备注缺少设备编号。");
            var note = Normalize(value);
            if (note.Length > 0) notes[id] = note;
        }
        return notes;
    }

    public Dictionary<string, string> Set(string id, string? value)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("无法确认这台设备，请刷新设备列表。");
        var note = Normalize(value);
        var notes = Load();
        if (note.Length == 0) notes.Remove(id); else notes[id] = note;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_filePath))!);
        var temporary = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(notes, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, _filePath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return notes;
    }
}
