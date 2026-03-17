using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CableTrayVoidCutter.Models;

/// <summary>
/// Persisted record of a single placed void and the MEP element it was created for.
/// Used to detect voids that need realignment after MEP elements move.
/// </summary>
public class VoidPlacementRecord
{
    /// <summary>ElementId.Value of the placed void / opening element.</summary>
    public long     VoidElementId  { get; set; }

    /// <summary>ElementId.Value of the source MEP element.</summary>
    public long     MepElementId   { get; set; }

    /// <summary>X coordinate of the MEP element centre at insertion time (feet).</summary>
    public double   MepX           { get; set; }

    /// <summary>Y coordinate of the MEP element centre at insertion time (feet).</summary>
    public double   MepY           { get; set; }

    /// <summary>Z coordinate of the MEP element centre at insertion time (feet).</summary>
    public double   MepZ           { get; set; }

    /// <summary>Revit document path (PathName) at insertion time.</summary>
    public string   DocumentPath   { get; set; } = string.Empty;

    /// <summary>UTC timestamp of the insertion.</summary>
    public DateTime Timestamp      { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Container for all <see cref="VoidPlacementRecord"/> entries, with JSON persistence.
/// Saved to %APPDATA%\CableTrayVoidCutter\void_log.json.
/// </summary>
public class VoidPlacementLog
{
    public List<VoidPlacementRecord> Records { get; set; } = [];

    // ── Persistence ───────────────────────────────────────────────────────────

    [JsonIgnore]
    private static readonly string LogFile =
        Path.Combine(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CableTrayVoidCutter"),
            "void_log.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true
    };

    public static VoidPlacementLog Load()
    {
        try
        {
            if (File.Exists(LogFile))
            {
                var json = File.ReadAllText(LogFile);
                return JsonSerializer.Deserialize<VoidPlacementLog>(json, JsonOpts)
                       ?? new VoidPlacementLog();
            }
        }
        catch { /* corrupted file – start fresh */ }

        return new VoidPlacementLog();
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(LogFile)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(this, JsonOpts);
            File.WriteAllText(LogFile, json);
        }
        catch { /* best-effort */ }
    }

    /// <summary>Adds or updates a record for the given void/MEP pair.</summary>
    public void Upsert(VoidPlacementRecord record)
    {
        var existing = Records.FirstOrDefault(
            r => r.VoidElementId == record.VoidElementId &&
                 r.DocumentPath  == record.DocumentPath);

        if (existing is not null) Records.Remove(existing);
        Records.Add(record);
    }
}
