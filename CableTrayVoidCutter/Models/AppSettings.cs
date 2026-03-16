using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CableTrayVoidCutter.Models;

/// <summary>
/// Describes a loaded void family symbol (wall or beam type).
/// </summary>
public class VoidFamilyEntry
{
    public string Name        { get; set; } = string.Empty;
    /// <summary>Full path to the .rfa file on disk.</summary>
    public string FilePath    { get; set; } = string.Empty;
    /// <summary>"Wall" | "Beam" | "Both"</summary>
    public string TargetType  { get; set; } = "Both";
}

/// <summary>
/// Persisted settings for the plugin.
/// Saved to %APPDATA%\CableTrayVoidCutter\settings.json.
/// </summary>
public class AppSettings
{
    // ── Persisted properties ─────────────────────────────────────────────────

    /// <summary>Margin added on every side of the cable tray, in millimetres.</summary>
    public double MarginMm { get; set; } = 25.0;

    /// <summary>Families available in the family picker.</summary>
    public List<VoidFamilyEntry> VoidFamilies { get; set; } = [];

    /// <summary>FilePath of the currently selected family for walls.</summary>
    public string? LastWallFamilyPath { get; set; }

    /// <summary>FilePath of the currently selected family for beams.</summary>
    public string? LastBeamFamilyPath { get; set; }

    // ── Persistence helpers ───────────────────────────────────────────────────

    [JsonIgnore]
    private static readonly string SettingsDir =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CableTrayVoidCutter");

    [JsonIgnore]
    private static readonly string SettingsFile =
        Path.Combine(SettingsDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented         = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Loads settings from disk, or returns defaults if not found.</summary>
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var json = File.ReadAllText(SettingsFile);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOpts)
                       ?? new AppSettings();
            }
        }
        catch { /* corrupted file – return defaults */ }

        return new AppSettings();
    }

    /// <summary>Saves the current settings to disk.</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(this, JsonOpts);
            File.WriteAllText(SettingsFile, json);
        }
        catch { /* best-effort */ }
    }

    // ── Convenience ───────────────────────────────────────────────────────────

    /// <summary>Margin in Revit internal units (feet).</summary>
    [JsonIgnore]
    public double MarginFeet => MarginMm / 304.8;

    public VoidFamilyEntry? GetLastWallFamily() =>
        VoidFamilies.FirstOrDefault(f => f.FilePath == LastWallFamilyPath);

    public VoidFamilyEntry? GetLastBeamFamily() =>
        VoidFamilies.FirstOrDefault(f => f.FilePath == LastBeamFamilyPath);
}
