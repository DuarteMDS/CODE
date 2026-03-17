using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Structure;
using CableTrayVoidCutter.Models;

namespace CableTrayVoidCutter.Core;

/// <summary>
/// Creates void openings / family instances for each <see cref="ClashResult"/>.
///
/// Insertion point strategy (for family instances):
///   • Wall  → XY projected onto the wall centreline (world space); Z = MEP BB centre.
///   • Floor → XY = intersection midpoint; Z = slab centre.
///   • Beam  → intersection midpoint.
///
/// Depth strategy:
///   • Wall  → wall.Width (host or linked document).
///   • Floor → slab BB height.
///   • Beam  → beam BB depth.
///
/// Level strategy:
///   • Family placed at absolute world XYZ (no level-template offset issue).
///   • Level association set afterwards via FAMILY_LEVEL_PARAM.
///   • Explicit elevation offset (INSTANCE_ELEVATION_PARAM) set so schedules/views
///     show the correct height above the associated level.
/// </summary>
public static class VoidPlacer
{
    // ── Public entry point ────────────────────────────────────────────────────

    public static string PlaceVoids(
        Document                          doc,
        IEnumerable<ClashResult>          clashes,
        double                            marginFeet,
        IDictionary<string, FamilySymbol> symbolCache,
        VoidPlacementLog?                 placementLog = null)
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
                        if (wall is null) { failed++; continue; }
                        placedId = CreateWallOpening(doc, wall, clash, marginFeet);
                        wallsOk++;
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

                // Record placement
                if (placedId is not null && placementLog is not null)
                {
                    var mepBb = doc.GetElement(clash.CableTrayId)?.get_BoundingBox(null);
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
                System.Diagnostics.Debug.WriteLine($"[VoidPlacer] {clash.DisplayName}: {ex.Message}");
            }
        }

        return $"Terminé.\n" +
               $"  Ouvertures murs/dalles : {wallsOk}\n" +
               $"  Vides poutres          : {beamsOk}\n" +
               $"  Réservations liées     : {linked}\n" +
               $"  Erreurs                : {failed}";
    }

    // ── Wall opening (host) ───────────────────────────────────────────────────

    private static ElementId? CreateWallOpening(
        Document doc, Wall wall, ClashResult clash, double margin)
    {
        var mepBb  = doc.GetElement(clash.CableTrayId)?.get_BoundingBox(null);
        var center = ResolveWallInsertPoint(clash.IntersectionMidPoint, mepBb,
                                            wall, Transform.Identity);
        if (center is null) return null;

        var wallCurve = ((LocationCurve)wall.Location).Curve;
        var wallDir   = (wallCurve.GetEndPoint(1) - wallCurve.GetEndPoint(0)).Normalize();
        var upDir     = wallDir.CrossProduct(wall.Orientation).Normalize();

        CurveArray curveArray;

        if (clash.TrayShape == TrayShape.Circular)
        {
            double radius = clash.TrayDiameter / 2.0 + margin;
            if (radius <= 0 && mepBb is not null)
                radius = Math.Max(mepBb.Max.X - mepBb.Min.X, mepBb.Max.Z - mepBb.Min.Z) / 2.0 + margin;
            if (radius <= 0) return null;

            curveArray = new CurveArray();
            var p0 = center + wallDir * radius; var p1 = center - wallDir * radius;
            curveArray.Append(Arc.Create(p0, p1, center + upDir * radius));
            curveArray.Append(Arc.Create(p1, p0, center - upDir * radius));
        }
        else
        {
            double halfW = (clash.TrayWidth  > 0 ? clash.TrayWidth  / 2.0 : 0.25) + margin;
            double halfH = (clash.TrayHeight > 0 ? clash.TrayHeight / 2.0 : 0.25) + margin;
            if (clash.TrayWidth  <= 0 && mepBb is not null)
                halfW = (mepBb.Max.X - mepBb.Min.X) / 2.0 + margin;
            if (clash.TrayHeight <= 0 && mepBb is not null)
                halfH = (mepBb.Max.Z - mepBb.Min.Z) / 2.0 + margin;

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

        return doc.Create.NewOpening(wall, curveArray, true)?.Id;
    }

    // ── Beam void (host) ─────────────────────────────────────────────────────

    private static ElementId? PlaceVoidOnBeam(
        Document doc, FamilyInstance beam,
        ClashResult clash, FamilySymbol voidSymbol, double margin)
    {
        EnsureSymbolActive(doc, voidSymbol);

        // Insertion point: intersection midpoint projected onto beam axis, MEP Z
        var mepBb    = doc.GetElement(clash.CableTrayId)?.get_BoundingBox(null);
        var insertPt = ResolveBeamInsertPoint(clash.IntersectionMidPoint, mepBb, beam);
        if (insertPt is null) return null;

        var beamCurve = ((LocationCurve)beam.Location).Curve;
        var beamDir   = (beamCurve.GetEndPoint(1) - beamCurve.GetEndPoint(0)).Normalize();

        var instance = PlaceAtAbsoluteZ(doc, insertPt, voidSymbol);
        AlignInstanceToDirection(doc, instance, insertPt, beamDir);

        // Depth = beam cross-section thickness
        var beamBb    = beam.get_BoundingBox(null);
        double depth  = beamBb is not null
            ? Math.Abs(beamBb.Max.Y - beamBb.Min.Y)
            : GetStructuralDepth(doc, clash);

        SetMepDimensions(instance, clash, margin);
        SetDimParam(instance, depth, "GA_Reservation Profondeur", "Depth");

        try { InstanceVoidCutUtils.AddInstanceVoidCut(doc, beam, instance); }
        catch { /* family may not cut */ }

        return instance.Id;
    }

    // ── Linked reservation ────────────────────────────────────────────────────

    private static ElementId? PlaceReservationFamily(
        Document doc, ClashResult clash,
        FamilySymbol symbol, double margin)
    {
        EnsureSymbolActive(doc, symbol);

        var mepBb    = doc.GetElement(clash.CableTrayId)?.get_BoundingBox(null);
        var insertPt = ResolveLinkedInsertPoint(doc, clash, mepBb);
        if (insertPt is null) return null;

        var instance = PlaceAtAbsoluteZ(doc, insertPt, symbol);

        SetMepDimensions(instance, clash, margin);

        // Depth = actual wall / slab thickness from the linked document
        double depth = GetStructuralDepth(doc, clash);
        SetDimParam(instance, depth, "GA_Reservation Profondeur", "Depth");

        SetTextParam(instance, "Comments", $"RESERVATION – lien: {clash.LinkName}");

        return instance.Id;
    }

    // ── Insert-point resolution ───────────────────────────────────────────────

    /// <summary>
    /// For a wall clash (host or linked), resolves the insertion point as:
    ///   XY → MEP axis projected onto the wall centreline (world space).
    ///   Z  → centre of the MEP element bounding box.
    /// </summary>
    private static XYZ? ResolveLinkedInsertPoint(
        Document doc, ClashResult clash, BoundingBoxXYZ? mepBb)
    {
        // Try to get the clashing element from the linked document
        var clashingElem = GetClashingElement(doc, clash);

        if (clash.ClashType == ClashType.Wall && clashingElem is Wall linkedWall)
        {
            // Reconstruct world-space wall curve
            var lc    = ((LocationCurve)linkedWall.Location).Curve;
            var wPt0  = clash.LinkTransform.OfPoint(lc.GetEndPoint(0));
            var wPt1  = clash.LinkTransform.OfPoint(lc.GetEndPoint(1));
            var worldCurve = Line.CreateBound(wPt0, wPt1);

            var raw   = clash.IntersectionMidPoint;
            if (raw.IsAlmostEqualTo(XYZ.Zero) && mepBb is not null) raw = GetCenter(mepBb);
            if (raw.IsAlmostEqualTo(XYZ.Zero)) return null;

            // Project onto wall centre (XY)
            var proj    = worldCurve.Project(raw);
            var wallXY  = worldCurve.Evaluate(proj.Parameter, false);

            // Z from MEP element centre
            double z = mepBb is not null
                ? (mepBb.Min.Z + mepBb.Max.Z) / 2.0
                : raw.Z;

            return new XYZ(wallXY.X, wallXY.Y, z);
        }

        if (clash.ClashType == ClashType.Floor && mepBb is not null)
        {
            // For floor/slab: use intersection midpoint XY; Z = slab centre
            var raw = clash.IntersectionMidPoint;
            if (raw.IsAlmostEqualTo(XYZ.Zero)) raw = GetCenter(mepBb);

            // Slab Z centre from clashing element BB (world space via transform)
            double z = raw.Z;
            var elemBb = clashingElem?.get_BoundingBox(null);
            if (elemBb is not null)
            {
                var c0 = clash.LinkTransform.OfPoint(elemBb.Min);
                var c1 = clash.LinkTransform.OfPoint(elemBb.Max);
                z = (c0.Z + c1.Z) / 2.0;
            }

            return new XYZ(raw.X, raw.Y, z);
        }

        // Generic fallback: intersection midpoint or MEP centre
        var fallback = clash.IntersectionMidPoint;
        if (!fallback.IsAlmostEqualTo(XYZ.Zero)) return fallback;
        return mepBb is not null ? GetCenter(mepBb) : null;
    }

    /// <summary>
    /// For host-wall clashes: project intersection onto wall centreline, use MEP Z.
    /// </summary>
    private static XYZ? ResolveWallInsertPoint(
        XYZ rawPt, BoundingBoxXYZ? mepBb, Wall wall, Transform toWorld)
    {
        var raw = rawPt;
        if (raw.IsAlmostEqualTo(XYZ.Zero))
        {
            if (mepBb is null) return null;
            raw = GetCenter(mepBb);
        }

        var lc       = ((LocationCurve)wall.Location).Curve;
        var wPt0     = toWorld.OfPoint(lc.GetEndPoint(0));
        var wPt1     = toWorld.OfPoint(lc.GetEndPoint(1));
        var worldCurve = Line.CreateBound(wPt0, wPt1);

        var proj   = worldCurve.Project(raw);
        var wallXY = worldCurve.Evaluate(proj.Parameter, false);

        double z = mepBb is not null
            ? (mepBb.Min.Z + mepBb.Max.Z) / 2.0
            : raw.Z;

        return new XYZ(wallXY.X, wallXY.Y, z);
    }

    /// <summary>
    /// For beam clashes: project intersection onto beam axis, use MEP Z.
    /// </summary>
    private static XYZ? ResolveBeamInsertPoint(
        XYZ rawPt, BoundingBoxXYZ? mepBb, FamilyInstance beam)
    {
        var raw = rawPt;
        if (raw.IsAlmostEqualTo(XYZ.Zero))
        {
            if (mepBb is null) return null;
            raw = GetCenter(mepBb);
        }

        var beamCurve = ((LocationCurve)beam.Location).Curve;
        var proj      = beamCurve.Project(raw);
        var beamPt    = beamCurve.Evaluate(proj.Parameter, false);

        double z = mepBb is not null
            ? (mepBb.Min.Z + mepBb.Max.Z) / 2.0
            : raw.Z;

        return new XYZ(beamPt.X, beamPt.Y, z);
    }

    // ── Structural depth (wall thickness / slab height) ───────────────────────

    /// <summary>
    /// Returns the depth of the clashing structural element (wall thickness for walls,
    /// slab BB height for floors). Resolves the element from the linked document when needed.
    /// </summary>
    private static double GetStructuralDepth(Document doc, ClashResult clash)
    {
        const double fallback = 0.5; // ~15 cm default

        var elem = GetClashingElement(doc, clash);
        if (elem is null) return fallback;

        if (clash.ClashType == ClashType.Wall)
        {
            if (elem is Wall w) return w.Width;
        }
        else if (clash.ClashType == ClashType.Floor)
        {
            var bb = elem.get_BoundingBox(null);
            if (bb is not null) return Math.Abs(bb.Max.Z - bb.Min.Z);
        }
        else // Beam – use BB depth in local Y
        {
            var bb = elem.get_BoundingBox(null);
            if (bb is not null) return Math.Abs(bb.Max.Y - bb.Min.Y);
        }

        return fallback;
    }

    /// <summary>
    /// Resolves the clashing element: from host doc if Source=Host, or from the
    /// linked document if Source=Linked.
    /// </summary>
    private static Element? GetClashingElement(Document doc, ClashResult clash)
    {
        if (clash.Source == ElementSource.Host)
            return doc.GetElement(clash.ClashingElementId);

        if (clash.LinkInstanceId is null) return null;

        var link    = doc.GetElement(clash.LinkInstanceId) as RevitLinkInstance;
        var linkDoc = link?.GetLinkDocument();
        return linkDoc?.GetElement(clash.ClashingElementId);
    }

    // ── Level-correct absolute placement ─────────────────────────────────────

    /// <summary>
    /// Places the family at exact world-space XYZ (no level-offset issue).
    /// The no-level overload guarantees the Z is the absolute project elevation.
    /// After placement, sets the Level and ElevationFromLevel parameters so that
    /// schedules, tags, and the project browser show the correct level association.
    /// </summary>
    private static FamilyInstance PlaceAtAbsoluteZ(
        Document doc, XYZ insertPt, FamilySymbol symbol)
    {
        // 3-param overload: XYZ is always world-absolute, no level-template offset
        var instance = doc.Create.NewFamilyInstance(
            insertPt, symbol, StructuralType.NonStructural);

        var level = GetNearestLevel(doc, insertPt.Z);
        if (level is null) return instance;

        // Associate with the nearest level below
        var levelParam =
            instance.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM)
         ?? instance.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM);

        if (levelParam is not null && !levelParam.IsReadOnly)
            levelParam.Set(level.Id);

        // Set the elevation offset so it is consistent with the absolute Z
        double offsetFromLevel = insertPt.Z - level.Elevation;
        var elevParam =
            instance.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM)
         ?? instance.get_Parameter(BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM);

        if (elevParam is not null && !elevParam.IsReadOnly)
            elevParam.Set(offsetFromLevel);

        return instance;
    }

    /// <summary>
    /// Nearest level whose elevation is ≤ z (closest from below).
    /// Falls back to the lowest level when all levels are above z.
    /// </summary>
    private static Level? GetNearestLevel(Document doc, double z)
    {
        var levels = new FilteredElementCollector(doc)
            .OfClass(typeof(Level)).Cast<Level>()
            .OrderBy(l => l.Elevation).ToList();

        if (levels.Count == 0) return null;
        return levels.LastOrDefault(l => l.Elevation <= z + 1e-4) ?? levels[0];
    }

    // ── Family loader ─────────────────────────────────────────────────────────

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

    private static FamilySymbol? ResolveSymbol(
        ClashResult clash, IDictionary<string, FamilySymbol> cache)
    {
        var path = clash.AssignedFamily?.FilePath;
        return path is not null && cache.TryGetValue(path, out var sym) ? sym : null;
    }

    /// <summary>
    /// Sets Width (Largeur) and Height (Longueur) based on the MEP section.
    /// Tries GA_Reservation parameter names first, then standard names.
    /// </summary>
    private static void SetMepDimensions(
        FamilyInstance instance, ClashResult clash, double margin)
    {
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
