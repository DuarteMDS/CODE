using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using CableTrayVoidCutter.Models;

namespace CableTrayVoidCutter.Core;

/// <summary>
/// Creates void openings for Cable Tray / Conduit ↔ Wall clashes.
///
/// HOST walls  → native Revit opening (NewOpening) — no family needed, always reliable.
/// LINKED walls → reservation family (CEG_Resa Wall Circle / Rectangular).
///
/// Family placement is robust: FamilyPlacementType is detected at runtime and the
/// correct NewFamilyInstance overload is chosen.  Multiple fallbacks are tried so
/// the placement succeeds regardless of whether the family is face-based,
/// wall-hosted, level-based, or freestanding.
///
/// Parameters set: GA_Width / GA_Height (size) + GA_Depth (wall thickness).
/// </summary>
public static class VoidPlacer
{
    // ── Entry point ───────────────────────────────────────────────────────────

    public static string PlaceVoids(
        Document                 doc,
        IEnumerable<ClashResult> clashes,
        double                   marginFeet,
        FamilySymbol?            circleSymbol,
        FamilySymbol?            rectSymbol,
        VoidPlacementLog?        log = null)
    {
        int hostOk = 0, linkedOk = 0, failed = 0;
        string docPath = doc.PathName;

        foreach (var clash in clashes.Where(c => c.IsSelected))
        {
            try
            {
                ElementId? placedId = null;

                if (clash.Source == ElementSource.Host)
                {
                    var wall = doc.GetElement(clash.ClashingElementId) as Wall;
                    if (wall is null) { failed++; continue; }
                    placedId = CreateNativeOpening(doc, wall, clash, marginFeet);
                    if (placedId is not null) hostOk++;
                    else { failed++; continue; }
                }
                else // Linked model → place reservation family
                {
                    var sym = clash.TrayShape == TrayShape.Circular ? circleSymbol : rectSymbol;
                    if (sym is null) { failed++; continue; }

                    var linkInst  = clash.LinkInstanceId is not null
                                  ? doc.GetElement(clash.LinkInstanceId) as RevitLinkInstance : null;
                    var linkedWall = linkInst?.GetLinkDocument()
                                             ?.GetElement(clash.ClashingElementId) as Wall;

                    placedId = PlaceReservationFamily(doc, clash, sym, marginFeet, linkedWall);
                    if (placedId is not null) linkedOk++;
                    else { failed++; continue; }
                }

                if (placedId is not null && log is not null)
                    TrackPlacement(doc, log, clash, placedId, docPath);
            }
            catch (Exception ex)
            {
                failed++;
                System.Diagnostics.Debug.WriteLine($"[VoidPlacer] {clash.DisplayName}: {ex.Message}");
            }
        }

        return $"Terminé.\n" +
               $"  Ouvertures dans murs (hôte)   : {hostOk}\n" +
               $"  Réservations murs (liés)       : {linkedOk}\n" +
               $"  Erreurs                        : {failed}";
    }

    // ── Native opening for host walls ─────────────────────────────────────────

    private static ElementId? CreateNativeOpening(
        Document doc, Wall wall, ClashResult clash, double margin)
    {
        var mep    = doc.GetElement(clash.CableTrayId);
        var mepBb  = mep?.get_BoundingBox(null);
        var center = WallCrossing(mep, mepBb, wall, Transform.Identity)
                  ?? (mepBb is not null ? Center(mepBb) : null);
        if (center is null) return null;

        var wallDir = WallDir(wall);
        var upDir   = wallDir.CrossProduct(wall.Orientation).Normalize();

        CurveArray curves;

        if (clash.TrayShape == TrayShape.Circular)
        {
            double r = (clash.TrayDiameter > 0 ? clash.TrayDiameter / 2.0
                       : mepBb is not null ? Math.Max(mepBb.Max.X - mepBb.Min.X,
                                                      mepBb.Max.Z - mepBb.Min.Z) / 2.0 : 0)
                     + margin;
            if (r <= 0) return null;
            var p0 = center + wallDir * r;
            var p1 = center - wallDir * r;
            curves = new CurveArray();
            curves.Append(Arc.Create(p0, p1, center + upDir * r));
            curves.Append(Arc.Create(p1, p0, center - upDir * r));
        }
        else
        {
            double hw = (clash.TrayWidth  > 0 ? clash.TrayWidth  / 2.0
                        : mepBb is not null ? (mepBb.Max.X - mepBb.Min.X) / 2.0 : 0.25) + margin;
            double hh = (clash.TrayHeight > 0 ? clash.TrayHeight / 2.0
                        : mepBb is not null ? (mepBb.Max.Z - mepBb.Min.Z) / 2.0 : 0.25) + margin;
            var p0 = center - wallDir * hw - upDir * hh;
            curves = new CurveArray();
            curves.Append(Line.CreateBound(p0,                              center + wallDir * hw - upDir * hh));
            curves.Append(Line.CreateBound(center + wallDir * hw - upDir * hh, center + wallDir * hw + upDir * hh));
            curves.Append(Line.CreateBound(center + wallDir * hw + upDir * hh, center - wallDir * hw + upDir * hh));
            curves.Append(Line.CreateBound(center - wallDir * hw + upDir * hh, p0));
        }

        return doc.Create.NewOpening(wall, curves, true)?.Id;
    }

    // ── Reservation family for linked walls ───────────────────────────────────

    private static ElementId? PlaceReservationFamily(
        Document doc, ClashResult clash, FamilySymbol sym,
        double margin, Wall? linkedWall)
    {
        Activate(doc, sym);

        var mepElem = doc.GetElement(clash.CableTrayId);
        var mepBb   = mepElem?.get_BoundingBox(null);

        // Exact insertion: where MEP axis crosses wall centre-plane
        var insertPt = (linkedWall is not null
            ? WallCrossing(mepElem, mepBb, linkedWall, clash.LinkTransform)
            : null)
            ?? clash.IntersectionMidPoint;

        if (insertPt is null || insertPt.IsAlmostEqualTo(XYZ.Zero))
        {
            if (mepBb is null) return null;
            insertPt = Center(mepBb);
        }

        // Place using the right overload for this family type
        var inst = TryPlace(doc, sym, insertPt);
        if (inst is null) return null;

        SetDimensions(inst, clash, margin);
        SetDim(inst, linkedWall is not null ? linkedWall.Width : 0.5, "GA_Depth", "Depth");
        SetText(inst, "Comments", $"RESERVATION – lien: {clash.LinkName}");
        return inst.Id;
    }

    // ── Smart family placement (handles all FamilyPlacementType values) ───────

    /// <summary>
    /// Tries the correct NewFamilyInstance overload for the family's placement type,
    /// with automatic fallbacks.  This is the fix for the "wrong placement type"
    /// exception thrown when face-based or wall-hosted families are placed with the
    /// bare 3-parameter freestanding overload.
    /// </summary>
    private static FamilyInstance? TryPlace(Document doc, FamilySymbol sym, XYZ pt)
    {
        var ptype = sym.Family.FamilyPlacementType;

        // ── 1. Level-based families (most common for reservation families) ────
        if (ptype is FamilyPlacementType.OneLevelBased
                  or FamilyPlacementType.OneLevelBasedHosted
                  or FamilyPlacementType.TwoLevelsBased)
        {
            var level = NearestLevel(doc, pt.Z);
            if (level is not null)
                try { return doc.Create.NewFamilyInstance(pt, sym, level, StructuralType.NonStructural); }
                catch { /* fall through */ }
        }

        // ── 2. Freestanding (ViewBased, WorkPlaneBased, or unknown) ──────────
        try { return doc.Create.NewFamilyInstance(pt, sym, StructuralType.NonStructural); }
        catch { /* fall through */ }

        // ── 3. Face-based fallback: horizontal work plane at insertion Z ──────
        try
        {
            var sp = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, pt));
            return doc.Create.NewFamilyInstance(pt, sym, sp, StructuralType.NonStructural);
        }
        catch { /* fall through */ }

        // ── 4. Level-based with nearest level (last resort) ──────────────────
        var lvl = NearestLevel(doc, pt.Z);
        if (lvl is not null)
            try { return doc.Create.NewFamilyInstance(pt, sym, lvl, StructuralType.NonStructural); }
            catch { /* fall through */ }

        return null;
    }

    // ── Family loader ─────────────────────────────────────────────────────────

    public static FamilySymbol? LoadFamily(Document doc, string? rfaPath)
    {
        if (rfaPath is null || !File.Exists(rfaPath)) return null;

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

    // ── Geometry ──────────────────────────────────────────────────────────────

    /// <summary>Where the MEP axis crosses the wall centre-plane (world coordinates).</summary>
    private static XYZ? WallCrossing(
        Element? mep, BoundingBoxXYZ? mepBb, Wall wall, Transform wallToWorld)
    {
        var n      = wallToWorld.OfVector(wall.Orientation).Normalize();
        var refPt  = wallToWorld.OfPoint(((LocationCurve)wall.Location).Curve.GetEndPoint(0));

        if (mep?.Location is LocationCurve lc)
        {
            var s = lc.Curve.GetEndPoint(0);
            var d = lc.Curve.GetEndPoint(1) - s;
            double den = n.DotProduct(d);
            if (Math.Abs(den) >= 1e-9)
            {
                double t = n.DotProduct(refPt - s) / den;
                if (t >= -0.1 && t <= 1.1) return s + d * t;
            }
        }

        // Fallback: project BB centre onto wall plane
        if (mepBb is null) return null;
        var c = Center(mepBb);
        return c - n * n.DotProduct(c - refPt);
    }

    // ── Parameters ───────────────────────────────────────────────────────────

    private static void SetDimensions(FamilyInstance inst, ClashResult clash, double margin)
    {
        if (clash.TrayShape == TrayShape.Circular)
        {
            double d = clash.TrayDiameter + 2 * margin;
            SetDim(inst, d, "GA_Width",  "Width");
            SetDim(inst, d, "GA_Height", "Height");
        }
        else
        {
            SetDim(inst, clash.TrayWidth  + 2 * margin, "GA_Width",  "Width");
            SetDim(inst, clash.TrayHeight + 2 * margin, "GA_Height", "Height");
        }
    }

    private static void SetDim(FamilyInstance inst, double v, params string[] names)
    {
        foreach (var n in names)
        {
            var p = inst.LookupParameter(n);
            if (p is not null && !p.IsReadOnly && p.StorageType == StorageType.Double)
                { p.Set(v); return; }
        }
    }

    private static void SetText(FamilyInstance inst, string name, string value)
    {
        var p = inst.LookupParameter(name);
        if (p is not null && !p.IsReadOnly && p.StorageType == StorageType.String)
            p.Set(value);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void Activate(Document doc, FamilySymbol sym)
    {
        if (!sym.IsActive) { sym.Activate(); doc.Regenerate(); }
    }

    private static XYZ Center(BoundingBoxXYZ bb) =>
        new((bb.Min.X + bb.Max.X) / 2, (bb.Min.Y + bb.Max.Y) / 2, (bb.Min.Z + bb.Max.Z) / 2);

    private static XYZ WallDir(Wall wall)
    {
        var c = ((LocationCurve)wall.Location).Curve;
        return (c.GetEndPoint(1) - c.GetEndPoint(0)).Normalize();
    }

    private static Level? NearestLevel(Document doc, double z) =>
        new FilteredElementCollector(doc)
            .OfClass(typeof(Level)).Cast<Level>()
            .OrderBy(l => l.Elevation)
            .LastOrDefault(l => l.Elevation <= z + 1e-4);

    private static void TrackPlacement(
        Document doc, VoidPlacementLog log,
        ClashResult clash, ElementId placedId, string docPath)
    {
        var bb = doc.GetElement(clash.CableTrayId)?.get_BoundingBox(null);
        if (bb is null) return;
        var c = Center(bb);
        log.Upsert(new VoidPlacementRecord
        {
            VoidElementId = placedId.Value,
            MepElementId  = clash.CableTrayId.Value,
            HostElementId = clash.ClashingElementId.Value,
            MepX = c.X, MepY = c.Y, MepZ = c.Z,
            DocumentPath  = docPath,
            Timestamp     = DateTime.UtcNow
        });
    }
}
