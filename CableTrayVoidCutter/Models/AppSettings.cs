using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CableTrayVoidCutter.Models;

/// <summary>Which wall orientations are included in clash detection.</summary>
public enum WallOrientationFilter
{
    Vertical,   // Only plumb walls (default)
    Horizontal, // Only sloped / horizontal walls
    Both        // All walls
}

/// <summary>
/// Persisted settings for the plugin.
/// Saved to %APPDATA%\CableTrayVoidCutter\settings.json.
/// </summary>
public class AppSettings
{
    // ── Clearance margin ──────────────────────────────────────────────────────

    /// <summary>Margin added around the MEP element on all sides, in millimetres.</summary>
    public double MarginMm { get; set; } = 25.0;

    // ── MEP scan filters ──────────────────────────────────────────────────────

    public bool ScanCableTrays { get; set; } = true;
    public bool ScanConduits   { get; set; } = true;

    public WallOrientationFilter WallOrientation { get; set; } = WallOrientationFilter.Vertical;

    // ── Void families (shape-based, inspired by ConVoid) ──────────────────────

    /// <summary>
    /// Path to the round/circular void family (e.g. CEG_Resa Wall Circle).
    /// Used for conduits and any MEP element detected as circular.
    /// </summary>
    public string? CircleFamilyPath { get; set; }

    /// <summary>
    /// Path to the rectangular void family (e.g. CEG_Resa Wall Rectangular).
    /// Used for cable trays, ladder trays, and any rectangular MEP element.
    /// </summary>
    public string? RectangularFamilyPath { get; set; }

    // ── Persistence ───────────────────────────────────────────────────────────

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
        catch { /* corrupted — return defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { /* best-effort */ }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    [JsonIgnore]
    public double MarginFeet => MarginMm / 304.8;
}
