using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CableTrayVoidCutter.Models;

/// <summary>
/// Which wall orientations are included in clash detection.
/// </summary>
public enum WallOrientationFilter
{
    /// <summary>Only plumb walls (standard Revit walls). Default.</summary>
    Vertical,
    /// <summary>Only walls whose face normal is mostly vertical (sloped / horizontal).</summary>
    Horizontal,
    /// <summary>All walls regardless of orientation.</summary>
    Both
}

/// <summary>
/// Describes a loaded void family symbol.
/// <para>TargetType values: "CableTray" | "LadderTray" | "Conduit" | "Beam" | "Wall" | "Both"</para>
/// </summary>
public class VoidFamilyEntry
{
    public string Name        { get; set; } = string.Empty;
    /// <summary>Full path to the .rfa file on disk.</summary>
    public string FilePath    { get; set; } = string.Empty;
    /// <summary>"CableTray" | "LadderTray" | "Conduit" | "Beam" | "Wall" (legacy) | "Both"</summary>
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

    // ── MEP scan filters ─────────────────────────────────────────────────────
    public bool ScanCableTrays        { get; set; } = true;
    public bool ScanCableTrayFittings { get; set; } = true;
    public bool ScanConduits          { get; set; } = true;

    /// <summary>Which wall orientations to include when detecting clashes.</summary>
    public WallOrientationFilter WallOrientation { get; set; } = WallOrientationFilter.Vertical;

    /// <summary>Families available in the family picker.</summary>
    public List<VoidFamilyEntry> VoidFamilies { get; set; } = [];

    // ── Per-type last-used family paths ───────────────────────────────────────

    /// <summary>Void family for horizontal cable trays (beams + linked reservations).</summary>
    public string? LastCableTrayFamilyPath  { get; set; }

    /// <summary>Void family for ladder trays (beams + linked reservations).</summary>
    public string? LastLadderTrayFamilyPath { get; set; }

    /// <summary>Void family for circular conduits (beams + linked reservations).</summary>
    public string? LastConduitFamilyPath    { get; set; }

    /// <summary>Void family specifically for structural beams (overrides MEP-specific when set).</summary>
    public string? LastBeamFamilyPath       { get; set; }

    // ── Legacy compat ─────────────────────────────────────────────────────────
    /// <summary>Kept for backward compatibility; maps to LastCableTrayFamilyPath on load.</summary>
    public string? LastWallFamilyPath
    {
        get => LastCableTrayFamilyPath;
        set { if (LastCableTrayFamilyPath is null) LastCableTrayFamilyPath = value; }
    }

    // ── Persistence helpers ───────────────────────────────────────────────────

    [JsonIgnore]
    private static readonly string SettingsDir =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CableTrayVoidCutter");

    [JsonIgnore]
    public static readonly string SettingsFile =
        Path.Combine(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CableTrayVoidCutter"),
            "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented          = true,
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

    public VoidFamilyEntry? GetLastCableTrayFamily() =>
        VoidFamilies.FirstOrDefault(f => f.FilePath == LastCableTrayFamilyPath);

    public VoidFamilyEntry? GetLastLadderTrayFamily() =>
        VoidFamilies.FirstOrDefault(f => f.FilePath == LastLadderTrayFamilyPath);

    public VoidFamilyEntry? GetLastConduitFamily() =>
        VoidFamilies.FirstOrDefault(f => f.FilePath == LastConduitFamilyPath);

    public VoidFamilyEntry? GetLastBeamFamily() =>
        VoidFamilies.FirstOrDefault(f => f.FilePath == LastBeamFamilyPath);
}
