using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Structure;
using CableTrayVoidCutter.Models;

namespace CableTrayVoidCutter.Core;

/// <summary>
/// Creates void openings / family instances for each <see cref="ClashResult"/>.
///
/// Strategy:
///   • Host Wall   → <c>doc.Create.NewOpening()</c> (rectangular or circular arc-loop).
///   • Host Beam   → void family placed + <c>InstanceVoidCutUtils</c>.
///   • Linked elem → GA_Reservation family placed in host as coordination marker.
///
/// Family selection priority per MEP sub-type:
///   • Conduit     → conduitVoidSymbol  → cableTrayVoidSymbol (fallback)
///   • Ladder tray → ladderTrayVoidSymbol → cableTrayVoidSymbol (fallback)
///   • Cable tray  → cableTrayVoidSymbol
///   • Beam (any)  → beamVoidSymbol → MEP-specific symbol (fallback)
/// </summary>
public static class VoidPlacer
{
    // ── Public entry point ────────────────────────────────────────────────────

    /// <summary>
    /// Processes all selected clashes and returns a summary message.
    /// Must be called inside an open transaction.
    /// </summary>
    /// <param name="doc">Active Revit document.</param>
    /// <param name="clashes">Clash list (only IsSelected items processed).</param>
    /// <param name="marginFeet">Clearance margin in feet.</param>
    /// <param name="cableTrayVoidSymbol">Void family for horizontal cable trays.</param>
    /// <param name="ladderTrayVoidSymbol">Void family for ladder trays.</param>
    /// <param name="conduitVoidSymbol">Void family for circular conduits.</param>
    /// <param name="beamVoidSymbol">Void family for structural beams (overrides MEP-specific).</param>
    /// <param name="placementLog">Optional log to record inserted voids for alignment tracking.</param>
    public static string PlaceVoids(
        Document                 doc,
        IEnumerable<ClashResult> clashes,
        double                   marginFeet,
        FamilySymbol?            cableTrayVoidSymbol,
        FamilySymbol?            ladderTrayVoidSymbol,
        FamilySymbol?            conduitVoidSymbol,
        FamilySymbol?            beamVoidSymbol,
        VoidPlacementLog?        placementLog = null)
    {
        int wallsOk = 0, beamsOk = 0, linked = 0, failed = 0;
        string docPath = doc.PathName;

        foreach (var clash in clashes.Where(c => c.IsSelected))
        {
            try
            {
                ElementId? placedId = null;

                if (clash.Source == ElementSource.Host)
                {
                    if (clash.ClashType == ClashType.Wall)
                    {
                        var wall = doc.GetElement(clash.ClashingElementId) as Wall;
                        if (wall is null) { failed++; continue; }

                        placedId = CreateWallOpening(doc, wall, clash, marginFeet);
                        wallsOk++;
                    }
                    else // Beam
                    {
                        var beam = doc.GetElement(clash.ClashingElementId) as FamilyInstance;
                        if (beam is null) { failed++; continue; }

                        var sym = beamVoidSymbol ?? SelectMepSymbol(clash,
                                      cableTrayVoidSymbol, ladderTrayVoidSymbol, conduitVoidSymbol);
                        if (sym is null) { failed++; continue; }

                        placedId = PlaceVoidOnBeam(doc, beam, clash, sym, marginFeet);
                        beamsOk++;
                    }
                }
                else // Linked
                {
                    var sym = SelectMepSymbol(clash,
                                  cableTrayVoidSymbol, ladderTrayVoidSymbol, conduitVoidSymbol);
                    sym ??= beamVoidSymbol;
                    if (sym is null) { failed++; continue; }

                    placedId = PlaceReservationFamily(doc, clash, sym, marginFeet);
                    linked++;
                }

                // Record placement for alignment tracking
                if (placedId is not null && placementLog is not null)
                {
                    var mepElem = doc.GetElement(clash.CableTrayId);
                    var mepBb   = mepElem?.get_BoundingBox(null);
                    if (mepBb is not null)
                    {
                        placementLog.Upsert(new VoidPlacementRecord
                        {
                            VoidElementId = placedId.Value,
                            MepElementId  = clash.CableTrayId.Value,
                            MepX          = (mepBb.Min.X + mepBb.Max.X) / 2.0,
                            MepY          = (mepBb.Min.Y + mepBb.Max.Y) / 2.0,
                            MepZ          = (mepBb.Min.Z + mepBb.Max.Z) / 2.0,
                            DocumentPath  = docPath,
                            Timestamp     = DateTime.UtcNow
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                failed++;
                System.Diagnostics.Debug.WriteLine(
                    $"[VoidPlacer] Failed on {clash.DisplayName}: {ex.Message}");
            }
        }

        return $"Done.\n" +
               $"  Wall openings created : {wallsOk}\n" +
               $"  Beam voids placed     : {beamsOk}\n" +
               $"  Linked reservations   : {linked}\n" +
               $"  Errors                : {failed}";
    }

    // ── Symbol selection ──────────────────────────────────────────────────────

    private static FamilySymbol? SelectMepSymbol(
        ClashResult   clash,
        FamilySymbol? cableTraySymbol,
        FamilySymbol? ladderTraySymbol,
        FamilySymbol? conduitSymbol)
    {
        if (clash.MepCategory == MepCategory.Conduit)
            return conduitSymbol ?? cableTraySymbol;

        if (clash.IsLadderTray)
            return ladderTraySymbol ?? cableTraySymbol;

        return cableTraySymbol;
    }

    // ── Wall opening ──────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a native opening in the wall and returns its ElementId for logging.
    /// </summary>
    private static ElementId? CreateWallOpening(
        Document doc, Wall wall, ClashResult clash, double margin)
    {
        var center = clash.IntersectionMidPoint;

        var mep   = doc.GetElement(clash.CableTrayId);
        var mepBb = mep?.get_BoundingBox(null);

        if (center.IsAlmostEqualTo(XYZ.Zero))
        {
            if (mepBb is null) return null;
            center = GetCenter(mepBb);
        }

        double centerZ = mepBb is not null
            ? (mepBb.Min.Z + mepBb.Max.Z) / 2.0
            : center.Z;

        var wallCurve    = ((LocationCurve)wall.Location).Curve;
        var proj         = wallCurve.Project(center);
        var wallCenterPt = wallCurve.Evaluate(proj.Parameter, false);
        center = new XYZ(wallCenterPt.X, wallCenterPt.Y, centerZ);

        var wallDir = (wallCurve.GetEndPoint(1) - wallCurve.GetEndPoint(0)).Normalize();
        var upDir   = wallDir.CrossProduct(wall.Orientation).Normalize();

        CurveArray curveArray;

        if (clash.TrayShape == TrayShape.Circular)
        {
            double radius = clash.TrayDiameter / 2.0 + margin;
            if (radius <= 0)
            {
                if (mepBb is null) return null;
                radius = Math.Max(mepBb.Max.X - mepBb.Min.X, mepBb.Max.Z - mepBb.Min.Z) / 2.0 + margin;
            }

            var p0   = center + wallDir * radius;
            var p1   = center - wallDir * radius;
            var pTop = center + upDir * radius;
            var pBot = center - upDir * radius;

            curveArray = new CurveArray();
            curveArray.Append(Arc.Create(p0, p1, pTop));
            curveArray.Append(Arc.Create(p1, p0, pBot));
        }
        else
        {
            double halfW = (clash.TrayWidth  > 0 ? clash.TrayWidth  / 2.0 : 0.25) + margin;
            double halfH = (clash.TrayHeight > 0 ? clash.TrayHeight / 2.0 : 0.25) + margin;

            if ((clash.TrayWidth <= 0 || clash.TrayHeight <= 0) && mepBb is not null)
            {
                if (clash.TrayWidth  <= 0) halfW = (mepBb.Max.X - mepBb.Min.X) / 2.0 + margin;
                if (clash.TrayHeight <= 0) halfH = (mepBb.Max.Z - mepBb.Min.Z) / 2.0 + margin;
            }

            var p0 = center - wallDir * halfW - upDir * halfH;
            var p1 = center + wallDir * halfW - upDir * halfH;
            var p2 = center + wallDir * halfW + upDir * halfH;
            var p3 = center - wallDir * halfW + upDir * halfH;

            curveArray = new CurveArray();
            curveArray.Append(Line.CreateBound(p0, p1));
            curveArray.Append(Line.CreateBound(p1, p2));
            curveArray.Append(Line.CreateBound(p2, p3));
            curveArray.Append(Line.CreateBound(p3, p0));
        }

        var opening = doc.Create.NewOpening(wall, curveArray, true);
        return opening?.Id;
    }

    // ── Beam void (family instance) ───────────────────────────────────────────

    private static ElementId? PlaceVoidOnBeam(
        Document doc, FamilyInstance beam,
        ClashResult clash, FamilySymbol voidSymbol,
        double margin)
    {
        EnsureSymbolActive(doc, voidSymbol);

        var insertPt = ResolveInsertPoint(doc, clash);
        if (insertPt is null) return null;

        var beamCurve = ((LocationCurve)beam.Location).Curve;
        var beamDir   = (beamCurve.GetEndPoint(1) - beamCurve.GetEndPoint(0)).Normalize();

        var level    = GetNearestLevel(doc, insertPt.Z);
        var instance = level is not null
            ? doc.Create.NewFamilyInstance(insertPt, voidSymbol, level,
                                           StructuralType.NonStructural)
            : doc.Create.NewFamilyInstance(insertPt, voidSymbol,
                                           StructuralType.NonStructural);

        AlignInstanceToDirection(doc, instance, insertPt, beamDir);

        double beamDepth = 0;
        var beamBb = beam.get_BoundingBox(null);
        if (beamBb is not null) beamDepth = beamBb.Max.Y - beamBb.Min.Y;

        if (clash.TrayShape == TrayShape.Circular)
        {
            double d = clash.TrayDiameter + 2 * margin;
            SetDimParam(instance, d,  "GA_Reservation Largeur",  "Width");
            SetDimParam(instance, d,  "GA_Reservation Longueur", "Height");
        }
        else
        {
            SetDimParam(instance, clash.TrayWidth  + 2 * margin,
                        "GA_Reservation Largeur",  "Width");
            SetDimParam(instance, clash.TrayHeight + 2 * margin,
                        "GA_Reservation Longueur", "Height");
        }

        SetDimParam(instance, beamDepth > 0 ? beamDepth : 0.5,
                    "GA_Reservation Profondeur", "Depth");

        try { InstanceVoidCutUtils.AddInstanceVoidCut(doc, beam, instance); }
        catch { /* family may not be a void-cutting type; skip */ }

        return instance.Id;
    }

    // ── Linked reservation ────────────────────────────────────────────────────

    private static ElementId? PlaceReservationFamily(
        Document doc, ClashResult clash,
        FamilySymbol symbol, double margin)
    {
        EnsureSymbolActive(doc, symbol);

        var insertPt = ResolveInsertPoint(doc, clash);
        if (insertPt is null) return null;

        var level    = GetNearestLevel(doc, insertPt.Z);
        var instance = level is not null
            ? doc.Create.NewFamilyInstance(insertPt, symbol, level,
                                           StructuralType.NonStructural)
            : doc.Create.NewFamilyInstance(insertPt, symbol,
                                           StructuralType.NonStructural);

        if (clash.TrayShape == TrayShape.Circular)
        {
            double d = clash.TrayDiameter + 2 * margin;
            SetDimParam(instance, d, "GA_Reservation Largeur",  "Width");
            SetDimParam(instance, d, "GA_Reservation Longueur", "Height");
        }
        else
        {
            SetDimParam(instance, clash.TrayWidth  + 2 * margin,
                        "GA_Reservation Largeur",  "Width");
            SetDimParam(instance, clash.TrayHeight + 2 * margin,
                        "GA_Reservation Longueur", "Height");
        }

        // Depth = wall/slab thickness estimated from the intersection; 0.5 ft as safe default
        SetDimParam(instance, 0.5, "GA_Reservation Profondeur", "Depth");
        SetTextParam(instance, "Comments", $"RESERVATION – linked: {clash.LinkName}");

        return instance.Id;
    }

    // ── Level helper ──────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the level whose elevation is at or below <paramref name="z"/> (nearest from below).
    /// Falls back to the lowest level when all levels are above z.
    /// </summary>
    private static Level? GetNearestLevel(Document doc, double z)
    {
        var levels = new FilteredElementCollector(doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .OrderBy(l => l.Elevation)
            .ToList();

        if (levels.Count == 0) return null;

        return levels.LastOrDefault(l => l.Elevation <= z + 1e-4)
               ?? levels[0];
    }

    // ── Utility helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the insertion point from the clash, falling back to the MEP element centre.
    /// </summary>
    private static XYZ? ResolveInsertPoint(Document doc, ClashResult clash)
    {
        var pt = clash.IntersectionMidPoint;
        if (!pt.IsAlmostEqualTo(XYZ.Zero)) return pt;

        var bb = doc.GetElement(clash.CableTrayId)?.get_BoundingBox(null);
        return bb is not null ? GetCenter(bb) : null;
    }

    private static void EnsureSymbolActive(Document doc, FamilySymbol sym)
    {
        if (!sym.IsActive)
        {
            sym.Activate();
            doc.Regenerate();
        }
    }

    private static XYZ GetCenter(BoundingBoxXYZ bb) =>
        new((bb.Min.X + bb.Max.X) / 2,
            (bb.Min.Y + bb.Max.Y) / 2,
            (bb.Min.Z + bb.Max.Z) / 2);

    private static void AlignInstanceToDirection(
        Document doc, FamilyInstance inst,
        XYZ origin, XYZ direction)
    {
        if (direction.IsAlmostEqualTo(XYZ.BasisX)) return;

        var axis  = Line.CreateUnbound(origin, XYZ.BasisZ);
        var angle = XYZ.BasisX.AngleTo(direction);
        var cross = XYZ.BasisX.CrossProduct(direction);
        if (cross.Z < 0) angle = -angle;

        ElementTransformUtils.RotateElement(doc, inst.Id, axis, angle);
    }

    /// <summary>
    /// Sets a dimension parameter by trying <paramref name="gaName"/> first,
    /// then <paramref name="fallbackName"/>.
    /// </summary>
    private static void SetDimParam(
        FamilyInstance inst, double valueFeet,
        string gaName, string fallbackName)
    {
        var p = inst.LookupParameter(gaName)
             ?? inst.LookupParameter(fallbackName);
        if (p is not null && !p.IsReadOnly && p.StorageType == StorageType.Double)
            p.Set(valueFeet);
    }

    private static void SetTextParam(FamilyInstance inst, string paramName, string value)
    {
        var p = inst.LookupParameter(paramName);
        if (p is not null && !p.IsReadOnly && p.StorageType == StorageType.String)
            p.Set(value);
    }

    // ── Family loader ─────────────────────────────────────────────────────────

    /// <summary>
    /// Loads a void family from disk and returns its first symbol, or null on failure.
    /// </summary>
    public static FamilySymbol? LoadFamily(Document doc, string rfaPath)
    {
        if (!File.Exists(rfaPath)) return null;

        Family? family = null;

        if (!doc.LoadFamily(rfaPath, out family))
        {
            var familyName = Path.GetFileNameWithoutExtension(rfaPath);
            family = new FilteredElementCollector(doc)
                .OfClass(typeof(Family))
                .Cast<Family>()
                .FirstOrDefault(f => f.Name == familyName);
        }

        if (family is null) return null;

        var symId = family.GetFamilySymbolIds().FirstOrDefault();
        if (symId is null) return null;

        return doc.GetElement(symId) as FamilySymbol;
    }
}
