using Autodesk.Revit.DB;
using CableTrayVoidCutter.Models;

namespace CableTrayVoidCutter.Core;

/// <summary>
/// Compares stored void/MEP positions against the current Revit model to identify
/// voids that are no longer aligned with their source MEP element.
/// </summary>
public static class VoidAlignmentChecker
{
    /// <summary>
    /// Threshold in feet below which position change is ignored (~1 mm).
    /// </summary>
    private const double ThresholdFeet = 1.0 / 304.8;

    /// <summary>
    /// Checks all records in the <paramref name="log"/> that belong to the current document
    /// and returns those whose MEP element has moved beyond the threshold.
    /// </summary>
    public static List<MisalignedVoid> Check(Document doc, VoidPlacementLog log)
    {
        var results   = new List<MisalignedVoid>();
        var docPath   = doc.PathName;

        foreach (var record in log.Records
                     .Where(r => string.Equals(r.DocumentPath, docPath,
                                               StringComparison.OrdinalIgnoreCase)))
        {
            // Resolve MEP element
            var mepId  = new ElementId(record.MepElementId);
            var mep    = doc.GetElement(mepId);
            if (mep is null) continue;

            // Current geometric centre of the MEP element
            var bb = mep.get_BoundingBox(null);
            if (bb is null) continue;
            var currentPos = new XYZ(
                (bb.Min.X + bb.Max.X) / 2.0,
                (bb.Min.Y + bb.Max.Y) / 2.0,
                (bb.Min.Z + bb.Max.Z) / 2.0);

            var storedPos  = new XYZ(record.MepX, record.MepY, record.MepZ);
            double delta   = currentPos.DistanceTo(storedPos);

            if (delta <= ThresholdFeet) continue;

            // Resolve void element (may have been deleted)
            var voidId   = new ElementId(record.VoidElementId);
            var voidElem = doc.GetElement(voidId);

            results.Add(new MisalignedVoid
            {
                Record             = record,
                VoidElement        = voidElem,
                MepElement         = mep,
                CurrentMepPosition = currentPos,
                StoredMepPosition  = storedPos,
                DeltaFeet          = delta
            });
        }

        // Sort by largest displacement first
        results.Sort((a, b) => b.DeltaFeet.CompareTo(a.DeltaFeet));
        return results;
    }
}

/// <summary>
/// A void that is no longer geometrically aligned with its source MEP element.
/// </summary>
public class MisalignedVoid
{
    public VoidPlacementRecord Record             { get; init; } = null!;
    public Element?            VoidElement        { get; init; }
    public Element?            MepElement         { get; init; }
    public XYZ                 CurrentMepPosition { get; init; } = XYZ.Zero;
    public XYZ                 StoredMepPosition  { get; init; } = XYZ.Zero;
    public double              DeltaFeet          { get; init; }

    /// <summary>Human-readable displacement in mm.</summary>
    public double DeltaMm => DeltaFeet * 304.8;

    public string VoidName   => VoidElement  is not null
        ? GeometryHelper.GetElementDisplayName(VoidElement)
        : $"[deleted – id {Record.VoidElementId}]";

    public string MepName    => MepElement   is not null
        ? GeometryHelper.GetElementDisplayName(MepElement)
        : $"[deleted – id {Record.MepElementId}]";

    public string DeltaDisplay => $"{DeltaMm:F0} mm";
}
