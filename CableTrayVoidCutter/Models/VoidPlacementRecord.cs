using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CableTrayVoidCutter.Models;

/// <summary>
/// Persisted record of a single placed void and the MEP/host pair it was created for.
/// Used to detect duplicate placements and to identify voids that need realignment
/// after MEP elements move.
/// </summary>
public class VoidPlacementRecord
{
    /// <summary>ElementId.Value of the placed void / opening element.</summary>
    public long     VoidElementId  { get; set; }

    /// <summary>ElementId.Value of the source MEP element.</summary>
    public long     MepElementId   { get; set; }

    /// <summary>ElementId.Value of the host structural element (wall, floor, beam).</summary>
    public long     HostElementId  { get; set; }

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

    /// <summary>Adds or updates a record keyed by MEP+Host pair.</summary>
    public void Upsert(VoidPlacementRecord record)
    {
        // Remove any prior record for the same MEP↔Host clash
        Records.RemoveAll(r =>
            r.MepElementId  == record.MepElementId  &&
            r.HostElementId == record.HostElementId &&
            string.Equals(r.DocumentPath, record.DocumentPath,
                          StringComparison.OrdinalIgnoreCase));
        Records.Add(record);
    }

    /// <summary>
    /// Returns the placement record for a specific MEP↔Host clash, or null if none.
    /// This is the primary key used to detect whether a clash already has an opening.
    /// </summary>
    public VoidPlacementRecord? FindByMepHost(long mepId, long hostId, string docPath) =>
        Records.FirstOrDefault(r =>
            r.MepElementId  == mepId  &&
            r.HostElementId == hostId &&
            string.Equals(r.DocumentPath, docPath, StringComparison.OrdinalIgnoreCase));
}
