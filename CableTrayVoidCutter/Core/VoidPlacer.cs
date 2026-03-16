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
///   • Linked elem → void family placed in host as a reservation marker.
/// </summary>
public static class VoidPlacer
{
    // ── Public entry point ────────────────────────────────────────────────────

    /// <summary>
    /// Processes all selected clashes and returns a summary message.
    /// Must be called inside an open transaction.
    /// </summary>
    public static string PlaceVoids(
        Document                 doc,
        IEnumerable<ClashResult> clashes,
        double                   marginFeet,
        FamilySymbol?            wallVoidSymbol,
        FamilySymbol?            beamVoidSymbol)
    {
        int wallsOk = 0, beamsOk = 0, linked = 0, failed = 0;

        foreach (var clash in clashes.Where(c => c.IsSelected))
        {
            try
            {
                bool ok = clash.Source == ElementSource.Host
                    ? PlaceHostVoid(doc, clash, marginFeet,
                                    wallVoidSymbol, beamVoidSymbol,
                                    ref wallsOk, ref beamsOk)
                    : PlaceLinkedVoid(doc, clash, marginFeet,
                                      wallVoidSymbol, beamVoidSymbol,
                                      ref linked);

                if (!ok) failed++;
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

    // ── Host elements ─────────────────────────────────────────────────────────

    private static bool PlaceHostVoid(
        Document       doc,
        ClashResult    clash,
        double         margin,
        FamilySymbol?  wallVoidSym,
        FamilySymbol?  beamVoidSym,
        ref int        wallsOk,
        ref int        beamsOk)
    {
        if (clash.ClashType == ClashType.Wall)
        {
            var wall = doc.GetElement(clash.ClashingElementId) as Wall;
            if (wall is null) return false;

            CreateWallOpening(doc, wall, clash, margin);
            wallsOk++;
            return true;
        }
        else // Beam
        {
            var beam = doc.GetElement(clash.ClashingElementId) as FamilyInstance;
            if (beam is null) return false;

            var sym = beamVoidSym ?? wallVoidSym;
            if (sym is null) return false;

            PlaceVoidOnBeam(doc, beam, clash, sym, margin);
            beamsOk++;
            return true;
        }
    }

    // ── Linked elements ───────────────────────────────────────────────────────

    private static bool PlaceLinkedVoid(
        Document       doc,
        ClashResult    clash,
        double         margin,
        FamilySymbol?  wallVoidSym,
        FamilySymbol?  beamVoidSym,
        ref int        linked)
    {
        var sym = clash.ClashType == ClashType.Wall ? wallVoidSym : beamVoidSym;
        sym ??= wallVoidSym ?? beamVoidSym;
        if (sym is null) return false;

        PlaceReservationFamily(doc, clash, sym, margin);
        linked++;
        return true;
    }

    // ── Wall opening ──────────────────────────────────────────────────────────

    /// <summary>
    /// Creates an opening in a wall sized to the cable tray / conduit + margin.
    /// Uses the intersection midpoint as the opening centre.
    /// Rectangular trays → rectangular opening.
    /// Circular conduits → circular opening (two arcs).
    /// </summary>
    private static void CreateWallOpening(
        Document doc, Wall wall, ClashResult clash, double margin)
    {
        // ── Insertion point: centre of the intersection in the wall plane ────
        var center = clash.IntersectionMidPoint;

        // Always fetch the MEP element to derive its geometric Z centre
        var mep   = doc.GetElement(clash.CableTrayId);
        var mepBb = mep?.get_BoundingBox(null);

        // If no intersection XY, fall back to tray bounding box centre
        if (center.IsAlmostEqualTo(XYZ.Zero))
        {
            if (mepBb is null) return;
            center = GetCenter(mepBb);
        }

        // Use the cable tray / fitting bounding-box Z midpoint so the void
        // is aligned to the element's geometric centre, not the intersection centroid.
        double centerZ = mepBb is not null
            ? (mepBb.Min.Z + mepBb.Max.Z) / 2.0
            : center.Z;

        // Project XY onto the wall centreline; apply tray-derived Z
        var wallCurve    = ((LocationCurve)wall.Location).Curve;
        var proj         = wallCurve.Project(center);
        var wallCenterPt = wallCurve.Evaluate(proj.Parameter, false);
        center = new XYZ(wallCenterPt.X, wallCenterPt.Y, centerZ);

        // ── Wall local axes ──────────────────────────────────────────────────
        var wallDir = (wallCurve.GetEndPoint(1) - wallCurve.GetEndPoint(0)).Normalize();
        // upDir must lie in the wall face plane and be perpendicular to wallDir.
        // wallDir × wall.Orientation gives BasisZ for a plumb wall, and a
        // horizontal axis for a horizontal/sloped wall – both are correct.
        var upDir   = wallDir.CrossProduct(wall.Orientation).Normalize();

        CurveArray curveArray;

        if (clash.TrayShape == TrayShape.Circular)
        {
            // ── Circular opening (conduit) ───────────────────────────────────
            double radius = clash.TrayDiameter / 2.0 + margin;
            if (radius <= 0)
            {
                // Fallback: derive from bounding box if parameter not available
                var mep = doc.GetElement(clash.CableTrayId);
                var bb  = mep?.get_BoundingBox(null);
                if (bb is null) return;
                radius = Math.Max(bb.Max.X - bb.Min.X, bb.Max.Z - bb.Min.Z) / 2.0 + margin;
            }

            // Two 180° arcs forming a circle in the wall plane (right+up axes)
            var p0 = center + wallDir * radius;
            var p1 = center - wallDir * radius;
            var pTop = center + upDir * radius;
            var pBot = center - upDir * radius;

            // Arc 1: right → top → left  (upper half)
            var arc1 = Arc.Create(p0, p1, pTop);
            // Arc 2: left → bottom → right (lower half)
            var arc2 = Arc.Create(p1, p0, pBot);

            curveArray = new CurveArray();
            curveArray.Append(arc1);
            curveArray.Append(arc2);
        }
        else
        {
            // ── Rectangular opening (cable tray) ─────────────────────────────
            double halfW = (clash.TrayWidth  > 0 ? clash.TrayWidth  / 2.0 : 0.25) + margin;
            double halfH = (clash.TrayHeight > 0 ? clash.TrayHeight / 2.0 : 0.25) + margin;

            // Fallback to bounding box if parameters were zero
            if (clash.TrayWidth <= 0 || clash.TrayHeight <= 0)
            {
                var mep = doc.GetElement(clash.CableTrayId);
                var bb  = mep?.get_BoundingBox(null);
                if (bb is not null)
                {
                    if (clash.TrayWidth  <= 0) halfW = (bb.Max.X - bb.Min.X) / 2.0 + margin;
                    if (clash.TrayHeight <= 0) halfH = (bb.Max.Z - bb.Min.Z) / 2.0 + margin;
                }
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

        // true = opening cuts through the full wall thickness
        doc.Create.NewOpening(wall, curveArray, true);
    }

    // ── Beam void (family instance) ───────────────────────────────────────────

    private static void PlaceVoidOnBeam(
        Document doc, FamilyInstance beam,
        ClashResult clash, FamilySymbol voidSymbol,
        double margin)
    {
        EnsureSymbolActive(doc, voidSymbol);

        // Use intersection midpoint as the insert point
        var insertPt = clash.IntersectionMidPoint;
        if (insertPt.IsAlmostEqualTo(XYZ.Zero))
        {
            var mep = doc.GetElement(clash.CableTrayId);
            var bb  = mep?.get_BoundingBox(null);
            if (bb is null) return;
            insertPt = GetCenter(bb);
        }

        var beamCurve = ((LocationCurve)beam.Location).Curve;
        var beamDir   = (beamCurve.GetEndPoint(1) - beamCurve.GetEndPoint(0)).Normalize();

        var instance = doc.Create.NewFamilyInstance(
            insertPt, voidSymbol, StructuralType.NonStructural);

        AlignInstanceToDirection(doc, instance, insertPt, beamDir);

        // Set dimensions from clash tray parameters (with margin)
        double beamDepth = 0;
        var beamBb = beam.get_BoundingBox(null);
        if (beamBb is not null) beamDepth = beamBb.Max.Y - beamBb.Min.Y;

        if (clash.TrayShape == TrayShape.Circular)
        {
            double d = clash.TrayDiameter + 2 * margin;
            SetDimensionParam(instance, "Width",  d);
            SetDimensionParam(instance, "Height", d);
        }
        else
        {
            SetDimensionParam(instance, "Width",  clash.TrayWidth  + 2 * margin);
            SetDimensionParam(instance, "Height", clash.TrayHeight + 2 * margin);
        }

        SetDimensionParam(instance, "Depth", beamDepth > 0 ? beamDepth : 0.5);

        try { InstanceVoidCutUtils.AddInstanceVoidCut(doc, beam, instance); }
        catch { /* family may not be a void-cutting type; skip */ }
    }

    // ── Linked reservation ────────────────────────────────────────────────────

    private static void PlaceReservationFamily(
        Document doc, ClashResult clash,
        FamilySymbol symbol, double margin)
    {
        EnsureSymbolActive(doc, symbol);

        var insertPt = clash.IntersectionMidPoint;
        if (insertPt.IsAlmostEqualTo(XYZ.Zero))
        {
            var mep = doc.GetElement(clash.CableTrayId);
            var bb  = mep?.get_BoundingBox(null);
            if (bb is null) return;
            insertPt = GetCenter(bb);
        }

        var instance = doc.Create.NewFamilyInstance(
            insertPt, symbol, StructuralType.NonStructural);

        if (clash.TrayShape == TrayShape.Circular)
        {
            double d = clash.TrayDiameter + 2 * margin;
            SetDimensionParam(instance, "Width",  d);
            SetDimensionParam(instance, "Height", d);
        }
        else
        {
            SetDimensionParam(instance, "Width",  clash.TrayWidth  + 2 * margin);
            SetDimensionParam(instance, "Height", clash.TrayHeight + 2 * margin);
        }

        SetDimensionParam(instance, "Depth", 0.5);
        SetTextParam(instance, "Comments", $"RESERVATION – linked: {clash.LinkName}");
    }

    // ── Utility helpers ───────────────────────────────────────────────────────

    private static void EnsureSymbolActive(Document doc, FamilySymbol sym)
    {
        if (!sym.IsActive)
        {
            sym.Activate();
            doc.Regenerate(); // required so the activated symbol is usable immediately
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

    private static void SetDimensionParam(FamilyInstance inst, string paramName, double valueFeet)
    {
        var p = inst.LookupParameter(paramName);
        if (p is not null && !p.IsReadOnly)
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
    /// Loads a void family from disk and returns its first symbol,
    /// or null on failure.
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
