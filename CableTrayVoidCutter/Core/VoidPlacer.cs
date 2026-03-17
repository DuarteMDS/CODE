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
///   • Host Wall / Floor → <c>doc.Create.NewOpening()</c>.
///   • Host Beam         → void family + <c>InstanceVoidCutUtils</c>.
///   • Linked element    → GA_Reservation family in host as coordination marker.
///
/// Each clash carries its own <see cref="ClashResult.AssignedFamily"/>; no global family
/// selectors are used.  The caller must load all required symbols before calling
/// <see cref="PlaceVoids"/> (in a separate committed transaction).
/// </summary>
public static class VoidPlacer
{
    // ── Public entry point ────────────────────────────────────────────────────

    /// <summary>
    /// Processes all selected clashes and returns a summary string.
    /// Must be called inside an open <see cref="Transaction"/>.
    /// </summary>
    /// <param name="doc">Active Revit document.</param>
    /// <param name="clashes">Clash list; only <c>IsSelected</c> items are processed.</param>
    /// <param name="marginFeet">Clearance margin in feet.</param>
    /// <param name="symbolCache">Pre-loaded symbols keyed by .rfa file path.</param>
    /// <param name="placementLog">Optional log for alignment tracking.</param>
    public static string PlaceVoids(
        Document                     doc,
        IEnumerable<ClashResult>     clashes,
        double                       marginFeet,
        IDictionary<string, FamilySymbol> symbolCache,
        VoidPlacementLog?            placementLog = null)
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
                    if (clash.ClashType is ClashType.Wall or ClashType.Floor)
                    {
                        var wall = doc.GetElement(clash.ClashingElementId) as Wall;
                        if (wall is not null)
                        {
                            placedId = CreateWallOpening(doc, wall, clash, marginFeet);
                            wallsOk++;
                        }
                        else { failed++; continue; }
                    }
                    else // Beam
                    {
                        var beam = doc.GetElement(clash.ClashingElementId) as FamilyInstance;
                        if (beam is null) { failed++; continue; }

                        var sym = ResolveSymbol(clash, symbolCache);
                        if (sym is null) { failed++; continue; }

                        placedId = PlaceVoidOnBeam(doc, beam, clash, sym, marginFeet);
                        beamsOk++;
                    }
                }
                else // Linked
                {
                    var sym = ResolveSymbol(clash, symbolCache);
                    if (sym is null) { failed++; continue; }

                    placedId = PlaceReservationFamily(doc, clash, sym, marginFeet);
                    linked++;
                }

                // Record placement for future alignment checks
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
                    $"[VoidPlacer] {clash.DisplayName}: {ex.Message}");
            }
        }

        return $"Terminé.\n" +
               $"  Ouvertures murs/dalles : {wallsOk}\n" +
               $"  Vides poutres          : {beamsOk}\n" +
               $"  Réservations liées     : {linked}\n" +
               $"  Erreurs                : {failed}";
    }

    // ── Symbol resolution ─────────────────────────────────────────────────────

    private static FamilySymbol? ResolveSymbol(
        ClashResult clash, IDictionary<string, FamilySymbol> cache)
    {
        var path = clash.AssignedFamily?.FilePath;
        if (path is not null && cache.TryGetValue(path, out var sym)) return sym;
        return null;
    }

    // ── Wall / floor opening ──────────────────────────────────────────────────

    private static ElementId? CreateWallOpening(
        Document doc, Wall wall, ClashResult clash, double margin)
    {
        var mep   = doc.GetElement(clash.CableTrayId);
        var mepBb = mep?.get_BoundingBox(null);

        var center = clash.IntersectionMidPoint;
        if (center.IsAlmostEqualTo(XYZ.Zero))
        {
            if (mepBb is null) return null;
            center = GetCenter(mepBb);
        }

        // Use MEP bounding-box Z centre so the void is centred on the element, not the centroid
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
            curveArray = new CurveArray();
            curveArray.Append(Arc.Create(p0, p1, center + upDir * radius));
            curveArray.Append(Arc.Create(p1, p0, center - upDir * radius));
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

    // ── Beam void ─────────────────────────────────────────────────────────────

    private static ElementId? PlaceVoidOnBeam(
        Document doc, FamilyInstance beam,
        ClashResult clash, FamilySymbol voidSymbol, double margin)
    {
        EnsureSymbolActive(doc, voidSymbol);

        var insertPt = ResolveInsertPoint(doc, clash);
        if (insertPt is null) return null;

        var beamCurve = ((LocationCurve)beam.Location).Curve;
        var beamDir   = (beamCurve.GetEndPoint(1) - beamCurve.GetEndPoint(0)).Normalize();

        var instance = PlaceAtAbsoluteZ(doc, insertPt, voidSymbol);

        AlignInstanceToDirection(doc, instance, insertPt, beamDir);

        var beamBb     = beam.get_BoundingBox(null);
        double beamDepth = beamBb is not null ? beamBb.Max.Y - beamBb.Min.Y : 0.5;

        if (clash.TrayShape == TrayShape.Circular)
        {
            double d = clash.TrayDiameter + 2 * margin;
            SetDimParam(instance, d,          "GA_Reservation Largeur",  "Width");
            SetDimParam(instance, d,          "GA_Reservation Longueur", "Height");
        }
        else
        {
            SetDimParam(instance, clash.TrayWidth  + 2 * margin, "GA_Reservation Largeur",  "Width");
            SetDimParam(instance, clash.TrayHeight + 2 * margin, "GA_Reservation Longueur", "Height");
        }

        SetDimParam(instance, beamDepth, "GA_Reservation Profondeur", "Depth");

        try { InstanceVoidCutUtils.AddInstanceVoidCut(doc, beam, instance); }
        catch { /* family may not be a void-cutting type */ }

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

        var instance = PlaceAtAbsoluteZ(doc, insertPt, symbol);

        if (clash.TrayShape == TrayShape.Circular)
        {
            double d = clash.TrayDiameter + 2 * margin;
            SetDimParam(instance, d, "GA_Reservation Largeur",  "Width");
            SetDimParam(instance, d, "GA_Reservation Longueur", "Height");
        }
        else
        {
            SetDimParam(instance, clash.TrayWidth  + 2 * margin, "GA_Reservation Largeur",  "Width");
            SetDimParam(instance, clash.TrayHeight + 2 * margin, "GA_Reservation Longueur", "Height");
        }

        SetDimParam(instance, 0.5, "GA_Reservation Profondeur", "Depth");
        SetTextParam(instance, "Comments", $"RESERVATION – lien: {clash.LinkName}");

        return instance.Id;
    }

    // ── Level-correct placement ───────────────────────────────────────────────

    /// <summary>
    /// Places a family instance at absolute world coordinates.
    /// The strategy avoids the level-offset issue that affects some family templates:
    ///   1. Place with no-level overload (position is always world-absolute).
    ///   2. Then associate the instance with the nearest level via its Level parameter.
    /// This guarantees the visual position is correct regardless of family template.
    /// </summary>
    private static FamilyInstance PlaceAtAbsoluteZ(
        Document doc, XYZ insertPt, FamilySymbol symbol)
    {
        // Place at absolute XYZ — no level offset applied
        var instance = doc.Create.NewFamilyInstance(
            insertPt, symbol, StructuralType.NonStructural);

        // Set level association so the void appears under the correct level in schedules
        var level = GetNearestLevel(doc, insertPt.Z);
        if (level is not null)
        {
            var levelParam =
                instance.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM)
             ?? instance.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM);

            if (levelParam is not null && !levelParam.IsReadOnly)
                levelParam.Set(level.Id);
        }

        return instance;
    }

    /// <summary>
    /// Returns the level whose elevation is closest from below to <paramref name="z"/>.
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

    // ── Family loader ─────────────────────────────────────────────────────────

    /// <summary>
    /// Loads a void family from <paramref name="rfaPath"/> and returns its first symbol,
    /// or null on failure. Must be called inside an open transaction.
    /// </summary>
    public static FamilySymbol? LoadFamily(Document doc, string rfaPath)
    {
        if (!File.Exists(rfaPath)) return null;

        if (!doc.LoadFamily(rfaPath, out var family))
        {
            var name = Path.GetFileNameWithoutExtension(rfaPath);
            family = new FilteredElementCollector(doc)
                .OfClass(typeof(Family)).Cast<Family>()
                .FirstOrDefault(f => f.Name == name);
        }

        if (family is null) return null;
        var symId = family.GetFamilySymbolIds().FirstOrDefault();
        return symId is not null ? doc.GetElement(symId) as FamilySymbol : null;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static XYZ? ResolveInsertPoint(Document doc, ClashResult clash)
    {
        var pt = clash.IntersectionMidPoint;
        if (!pt.IsAlmostEqualTo(XYZ.Zero)) return pt;
        var bb = doc.GetElement(clash.CableTrayId)?.get_BoundingBox(null);
        return bb is not null ? GetCenter(bb) : null;
    }

    private static void EnsureSymbolActive(Document doc, FamilySymbol sym)
    {
        if (!sym.IsActive) { sym.Activate(); doc.Regenerate(); }
    }

    private static XYZ GetCenter(BoundingBoxXYZ bb) =>
        new((bb.Min.X + bb.Max.X) / 2,
            (bb.Min.Y + bb.Max.Y) / 2,
            (bb.Min.Z + bb.Max.Z) / 2);

    private static void AlignInstanceToDirection(
        Document doc, FamilyInstance inst, XYZ origin, XYZ direction)
    {
        if (direction.IsAlmostEqualTo(XYZ.BasisX)) return;
        var axis  = Line.CreateUnbound(origin, XYZ.BasisZ);
        double angle = XYZ.BasisX.AngleTo(direction);
        if (XYZ.BasisX.CrossProduct(direction).Z < 0) angle = -angle;
        ElementTransformUtils.RotateElement(doc, inst.Id, axis, angle);
    }

    private static void SetDimParam(
        FamilyInstance inst, double valueFeet, string gaName, string fallback)
    {
        var p = inst.LookupParameter(gaName) ?? inst.LookupParameter(fallback);
        if (p is not null && !p.IsReadOnly && p.StorageType == StorageType.Double)
            p.Set(valueFeet);
    }

    private static void SetTextParam(FamilyInstance inst, string paramName, string value)
    {
        var p = inst.LookupParameter(paramName);
        if (p is not null && !p.IsReadOnly && p.StorageType == StorageType.String)
            p.Set(value);
    }
}
