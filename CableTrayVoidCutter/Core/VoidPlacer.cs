using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Structure;
using CableTrayVoidCutter.Models;

namespace CableTrayVoidCutter.Core;

/// <summary>
/// Creates void openings / family instances for each <see cref="ClashResult"/>.
///
/// Strategy:
///   • Host Wall   → <c>doc.Create.NewOpening()</c> (rectangular).
///   • Host Beam   → void family placed + <c>InstanceVoidCutUtils</c>.
///   • Linked elem → void family placed in host as a reservation marker
///                   (linked docs cannot be modified from the host).
/// </summary>
public static class VoidPlacer
{
    // ── Public entry point ────────────────────────────────────────────────────

    /// <summary>
    /// Processes all selected clashes and returns a summary message.
    /// Must be called inside an open transaction.
    /// </summary>
    public static string PlaceVoids(
        Document           doc,
        IEnumerable<ClashResult> clashes,
        double             marginFeet,
        FamilySymbol?      wallVoidSymbol,
        FamilySymbol?      beamVoidSymbol)
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
                // Log to Revit journal; don't rethrow so remaining clashes are processed
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

            var tray = doc.GetElement(clash.CableTrayId) as CableTray;
            if (tray is null) return false;

            CreateWallOpening(doc, wall, tray, margin);
            wallsOk++;
            return true;
        }
        else // Beam
        {
            var beam = doc.GetElement(clash.ClashingElementId) as FamilyInstance;
            if (beam is null) return false;

            var tray = doc.GetElement(clash.CableTrayId) as CableTray;
            if (tray is null) return false;

            var sym = beamVoidSym ?? wallVoidSym;
            if (sym is null) return false; // need a family

            PlaceVoidOnBeam(doc, beam, tray, sym, margin);
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
        // We can only place a marker family in the host document.
        var sym = clash.ClashType == ClashType.Wall ? wallVoidSym : beamVoidSym;
        sym ??= wallVoidSym ?? beamVoidSym;
        if (sym is null) return false;

        var tray = doc.GetElement(clash.CableTrayId) as CableTray;
        if (tray is null) return false;

        PlaceReservationFamily(doc, clash, tray, sym, margin);
        linked++;
        return true;
    }

    // ── Wall opening ──────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a rectangular opening in a wall sized to the cable tray
    /// bounding box + margin.
    /// </summary>
    private static void CreateWallOpening(
        Document doc, Wall wall, CableTray tray, double margin)
    {
        var trayCurve = ((LocationCurve)tray.Location).Curve;

        // Bounding box of the tray in world coordinates
        var bb  = tray.get_BoundingBox(null)!;
        var bbW = wall.get_BoundingBox(null)!;

        // Width (perpendicular to wall normal) and height from the tray BB
        double halfWidth  = (bb.Max.X - bb.Min.X) / 2.0 + margin;
        double halfHeight = (bb.Max.Z - bb.Min.Z) / 2.0 + margin;

        // Mid-point of the opening (intersection mid-point)
        var mid = clash_safe_mid(bb);

        // Project mid-point onto the wall face
        var wallOrientation = wall.Orientation;
        var wallFacePt      = ProjectOntoWallFace(wall, mid);

        // Build the corner points for the opening curve loop
        // Opening is in the wall's local XZ plane
        var wallDir  = ((LocationCurve)wall.Location).Curve.ComputeDerivatives(0.5, true).BasisX;
        var upDir    = XYZ.BasisZ;
        var rightDir = wallDir.Normalize();

        var p0 = wallFacePt - rightDir * halfWidth - upDir * halfHeight;
        var p1 = wallFacePt + rightDir * halfWidth - upDir * halfHeight;
        var p2 = wallFacePt + rightDir * halfWidth + upDir * halfHeight;
        var p3 = wallFacePt - rightDir * halfWidth + upDir * halfHeight;

        var curveArray = new CurveArray();
        curveArray.Append(Line.CreateBound(p0, p1));
        curveArray.Append(Line.CreateBound(p1, p2));
        curveArray.Append(Line.CreateBound(p2, p3));
        curveArray.Append(Line.CreateBound(p3, p0));

        // true = opening cuts through the full wall thickness
        doc.Create.NewOpening(wall, curveArray, true);
    }

    // ── Beam void (family instance) ───────────────────────────────────────────

    private static void PlaceVoidOnBeam(
        Document doc, FamilyInstance beam,
        CableTray tray, FamilySymbol voidSymbol,
        double margin)
    {
        EnsureSymbolActive(doc, voidSymbol);

        var bb         = tray.get_BoundingBox(null)!;
        var insertPt   = GetCenter(bb);
        var beamCurve  = ((LocationCurve)beam.Location).Curve;
        var beamDir    = (beamCurve.GetEndPoint(1) - beamCurve.GetEndPoint(0)).Normalize();

        // Place the void family
        var instance = doc.Create.NewFamilyInstance(
            insertPt,
            voidSymbol,
            StructuralType.NonStructural);

        // Rotate to align with the beam direction
        AlignInstanceToDirection(doc, instance, insertPt, beamDir);

        // Set width / height / depth parameters (best-effort by parameter name)
        SetDimensionParam(instance, "Width",  bb.Max.X - bb.Min.X + 2 * margin);
        SetDimensionParam(instance, "Height", bb.Max.Z - bb.Min.Z + 2 * margin);
        SetDimensionParam(instance, "Depth",  beam.get_BoundingBox(null)?.Max.Y
                                              - beam.get_BoundingBox(null)?.Min.Y
                                              ?? 0.5);

        // Connect void to beam (cuts the beam)
        try { InstanceVoidCutUtils.AddInstanceVoidCut(doc, beam, instance); }
        catch { /* family may not be a void-cutting type; skip */ }
    }

    // ── Linked reservation ────────────────────────────────────────────────────

    private static void PlaceReservationFamily(
        Document doc, ClashResult clash,
        CableTray tray, FamilySymbol symbol,
        double margin)
    {
        EnsureSymbolActive(doc, symbol);

        var bb       = tray.get_BoundingBox(null)!;
        var insertPt = clash.IntersectionMidPoint;
        if (insertPt.IsAlmostEqualTo(XYZ.Zero))
            insertPt = GetCenter(bb);

        var instance = doc.Create.NewFamilyInstance(
            insertPt, symbol, StructuralType.NonStructural);

        SetDimensionParam(instance, "Width",  bb.Max.X - bb.Min.X + 2 * margin);
        SetDimensionParam(instance, "Height", bb.Max.Z - bb.Min.Z + 2 * margin);
        SetDimensionParam(instance, "Depth",  0.5); // unknown linked wall thickness

        // Tag it so users know it targets a linked element
        SetTextParam(instance, "Comments",
                     $"RESERVATION – linked: {clash.LinkName}");
    }

    // ── Utility helpers ───────────────────────────────────────────────────────

    private static void EnsureSymbolActive(Document doc, FamilySymbol sym)
    {
        if (!sym.IsActive)
            sym.Activate();
    }

    private static XYZ GetCenter(BoundingBoxXYZ bb) =>
        new((bb.Min.X + bb.Max.X) / 2,
            (bb.Min.Y + bb.Max.Y) / 2,
            (bb.Min.Z + bb.Max.Z) / 2);

    private static XYZ clash_safe_mid(BoundingBoxXYZ bb) => GetCenter(bb);

    private static XYZ ProjectOntoWallFace(Wall wall, XYZ pt)
    {
        // Simple: return a point on the wall centerline plane at pt's height
        var wallCurve = ((LocationCurve)wall.Location).Curve;
        var param     = wallCurve.Project(pt).Parameter;
        var wallPt    = wallCurve.Evaluate(param, false);
        return new XYZ(wallPt.X, wallPt.Y, pt.Z);
    }

    private static void AlignInstanceToDirection(
        Document doc, FamilyInstance inst,
        XYZ origin, XYZ direction)
    {
        if (direction.IsAlmostEqualTo(XYZ.BasisX)) return;

        var axis = Line.CreateUnbound(origin, XYZ.BasisZ);
        var angle = XYZ.BasisX.AngleTo(direction);
        // Determine sign
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
            // Family might already be loaded – look it up by name
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
