using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using CableTrayVoidCutter.Models;

namespace CableTrayVoidCutter.Core;

/// <summary>
/// Creates void openings / reservation families for each <see cref="ClashResult"/>.
///
/// Family selection is shape-based (inspired by ConVoid):
///   Circular MEP  → circleSymbol    (e.g. CEG_Resa Wall Circle)
///   Rectangular   → rectSymbol      (e.g. CEG_Resa Wall Rectangular)
///
/// Parameters set on placed families (tried in order):
///   Width  : GA_Width → Width
///   Height : GA_Height → Height
///   Depth  : GA_Depth → Depth
/// </summary>
public static class VoidPlacer
{
    // ── Public entry point ────────────────────────────────────────────────────

    public static string PlaceVoids(
        Document                 doc,
        IEnumerable<ClashResult> clashes,
        double                   marginFeet,
        FamilySymbol?            circleSymbol,
        FamilySymbol?            rectSymbol,
        VoidPlacementLog?        placementLog = null)
    {
        int wallsOk = 0, beamsOk = 0, linked = 0, failed = 0;
        string docPath = doc.PathName;

        foreach (var clash in clashes.Where(c => c.IsSelected))
        {
            try
            {
                // Shape-based symbol selection
                var sym = clash.TrayShape == TrayShape.Circular ? circleSymbol : rectSymbol;

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
                        if (beam is null || sym is null) { failed++; continue; }
                        placedId = PlaceVoidOnBeam(doc, beam, clash, sym, marginFeet);
                        beamsOk++;
                    }
                }
                else // Linked model → reservation family
                {
                    if (sym is null) { failed++; continue; }
                    placedId = PlaceReservationFamily(doc, clash, sym, marginFeet);
                    linked++;
                }

                if (placedId is not null && placementLog is not null)
                    TrackPlacement(doc, placementLog, clash, placedId, docPath);
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

    // ── Host wall / floor opening (native Revit opening) ─────────────────────

    private static ElementId? CreateWallOpening(
        Document doc, Wall wall, ClashResult clash, double margin)
    {
        var mep    = doc.GetElement(clash.CableTrayId);
        var mepBb  = mep?.get_BoundingBox(null);
        var center = ComputeWallCrossing(mep, mepBb, wall, Transform.Identity)
                  ?? (mepBb is not null ? Center(mepBb) : null);
        if (center is null) return null;

        var wallDir = WallDirection(wall);
        var upDir   = wallDir.CrossProduct(wall.Orientation).Normalize();

        CurveArray curves;

        if (clash.TrayShape == TrayShape.Circular)
        {
            double r = MepRadius(clash, mepBb) + margin;
            if (r <= 0) return null;
            var p0 = center + wallDir * r;
            var p1 = center - wallDir * r;
            curves = new CurveArray();
            curves.Append(Arc.Create(p0, p1, center + upDir * r));
            curves.Append(Arc.Create(p1, p0, center - upDir * r));
        }
        else
        {
            double hw = HalfWidth (clash, mepBb) + margin;
            double hh = HalfHeight(clash, mepBb) + margin;
            var p0 = center - wallDir * hw - upDir * hh;
            curves = new CurveArray();
            curves.Append(Line.CreateBound(p0,                              center + wallDir * hw - upDir * hh));
            curves.Append(Line.CreateBound(center + wallDir * hw - upDir * hh, center + wallDir * hw + upDir * hh));
            curves.Append(Line.CreateBound(center + wallDir * hw + upDir * hh, center - wallDir * hw + upDir * hh));
            curves.Append(Line.CreateBound(center - wallDir * hw + upDir * hh, p0));
        }

        return doc.Create.NewOpening(wall, curves, true)?.Id;
    }

    // ── Beam void (host model) ────────────────────────────────────────────────

    private static ElementId? PlaceVoidOnBeam(
        Document doc, FamilyInstance beam,
        ClashResult clash, FamilySymbol sym, double margin)
    {
        Activate(doc, sym);

        var mepBb    = doc.GetElement(clash.CableTrayId)?.get_BoundingBox(null);
        var insertPt = ComputeBeamCrossing(doc.GetElement(clash.CableTrayId), mepBb, beam)
                    ?? (mepBb is not null ? Center(mepBb) : null);
        if (insertPt is null) return null;

        var inst = doc.Create.NewFamilyInstance(insertPt, sym, StructuralType.NonStructural);

        var dir = ElementDir(beam);
        if (dir is not null) AlignToDir(doc, inst, insertPt, dir);

        var beamBb = beam.get_BoundingBox(null);
        SetDimensions(inst, clash, margin);
        SetDim(inst, beamBb is not null ? Math.Abs(beamBb.Max.Y - beamBb.Min.Y) : 0.5,
               "GA_Depth", "Depth");

        try { InstanceVoidCutUtils.AddInstanceVoidCut(doc, beam, inst); } catch { }
        return inst.Id;
    }

    // ── Linked reservation family ─────────────────────────────────────────────

    private static ElementId? PlaceReservationFamily(
        Document doc, ClashResult clash, FamilySymbol sym, double margin)
    {
        Activate(doc, sym);

        var mepElem  = doc.GetElement(clash.CableTrayId);
        var mepBb    = mepElem?.get_BoundingBox(null);

        var linkInst   = clash.LinkInstanceId is not null
                       ? doc.GetElement(clash.LinkInstanceId) as RevitLinkInstance : null;
        var structElem = linkInst?.GetLinkDocument()?.GetElement(clash.ClashingElementId);

        XYZ? insertPt = null;

        if (clash.ClashType == ClashType.Wall && structElem is Wall linkedWall)
        {
            insertPt = ComputeWallCrossing(mepElem, mepBb, linkedWall, clash.LinkTransform);
        }
        else if (clash.ClashType == ClashType.Floor && structElem is not null)
        {
            var raw    = clash.IntersectionMidPoint;
            var elemBb = structElem.get_BoundingBox(null);
            if (elemBb is not null)
            {
                double slabZ = (clash.LinkTransform.OfPoint(elemBb.Min).Z +
                                clash.LinkTransform.OfPoint(elemBb.Max).Z) / 2.0;
                var xy = (raw is not null && !raw.IsAlmostEqualTo(XYZ.Zero)) ? raw
                       : (mepBb is not null ? Center(mepBb) : null);
                if (xy is not null) insertPt = new XYZ(xy.X, xy.Y, slabZ);
            }
        }

        insertPt ??= clash.IntersectionMidPoint;
        if (insertPt is null || insertPt.IsAlmostEqualTo(XYZ.Zero))
        {
            if (mepBb is null) return null;
            insertPt = Center(mepBb);
        }

        var inst = doc.Create.NewFamilyInstance(insertPt, sym, StructuralType.NonStructural);

        SetLevelAssociation(doc, inst, insertPt.Z);
        SetDimensions(inst, clash, margin);

        double depth = StructuralDepth(structElem, clash.ClashType, clash.LinkTransform);
        SetDim(inst, depth, "GA_Depth", "Depth");

        SetText(inst, "Comments", $"RESERVATION – lien: {clash.LinkName}");
        return inst.Id;
    }

    // ── Geometry helpers ──────────────────────────────────────────────────────

    /// <summary>Where MEP axis crosses the wall centre-plane (world coordinates).</summary>
    private static XYZ? ComputeWallCrossing(
        Element? mep, BoundingBoxXYZ? mepBb, Wall wall, Transform wallToWorld)
    {
        var normal = wallToWorld.OfVector(wall.Orientation).Normalize();
        var refPt  = wallToWorld.OfPoint(
                         ((LocationCurve)wall.Location).Curve.GetEndPoint(0));

        if (mep?.Location is LocationCurve lc)
        {
            var s = lc.Curve.GetEndPoint(0);
            var d = lc.Curve.GetEndPoint(1) - s;
            double den = normal.DotProduct(d);
            if (Math.Abs(den) < 1e-9) goto fallback;
            double t = normal.DotProduct(refPt - s) / den;
            if (t < -0.1 || t > 1.1) goto fallback;
            return s + d * t;
        }

        fallback:
        if (mepBb is null) return null;
        var c  = Center(mepBb);
        double dist = normal.DotProduct(c - refPt);
        return c - normal * dist;
    }

    /// <summary>Projects MEP centre onto beam axis at MEP Z.</summary>
    private static XYZ? ComputeBeamCrossing(
        Element? mep, BoundingBoxXYZ? mepBb, FamilyInstance beam)
    {
        if (beam.Location is not LocationCurve beamLc) return null;
        var refPt = mep?.Location is LocationCurve mepLc
            ? mepLc.Curve.Evaluate(0.5, true)
            : (mepBb is not null ? Center(mepBb) : null);
        if (refPt is null) return null;

        var bs  = beamLc.Curve.GetEndPoint(0);
        var bd  = (beamLc.Curve.GetEndPoint(1) - bs).Normalize();
        double t = bd.DotProduct(refPt - bs);
        var pt   = bs + bd * t;
        double z = mepBb is not null ? (mepBb.Min.Z + mepBb.Max.Z) / 2.0 : pt.Z;
        return new XYZ(pt.X, pt.Y, z);
    }

    // ── Level association (schedules/tags only — does NOT move element) ────────

    private static void SetLevelAssociation(Document doc, FamilyInstance inst, double z)
    {
        var level = new FilteredElementCollector(doc)
            .OfClass(typeof(Level)).Cast<Level>()
            .OrderBy(l => l.Elevation)
            .LastOrDefault(l => l.Elevation <= z + 1e-4);
        if (level is null) return;

        var p = inst.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM)
             ?? inst.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM);
        if (p is not null && !p.IsReadOnly) p.Set(level.Id);
    }

    // ── Structural depth ──────────────────────────────────────────────────────

    private static double StructuralDepth(Element? elem, ClashType type, Transform t)
    {
        const double fallback = 0.5;
        if (elem is null) return fallback;
        if (type == ClashType.Wall && elem is Wall w) return w.Width;
        var bb = elem.get_BoundingBox(null);
        if (bb is null) return fallback;
        if (type == ClashType.Floor)
            return Math.Abs(t.OfPoint(bb.Max).Z - t.OfPoint(bb.Min).Z);
        return Math.Abs(bb.Max.Y - bb.Min.Y);
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

    // ── Parameter setting ─────────────────────────────────────────────────────

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

    // ── Small utilities ───────────────────────────────────────────────────────

    private static void Activate(Document doc, FamilySymbol sym)
    {
        if (!sym.IsActive) { sym.Activate(); doc.Regenerate(); }
    }

    private static XYZ Center(BoundingBoxXYZ bb) =>
        new((bb.Min.X + bb.Max.X) / 2, (bb.Min.Y + bb.Max.Y) / 2, (bb.Min.Z + bb.Max.Z) / 2);

    private static XYZ WallDirection(Wall wall)
    {
        var c = ((LocationCurve)wall.Location).Curve;
        return (c.GetEndPoint(1) - c.GetEndPoint(0)).Normalize();
    }

    private static XYZ? ElementDir(FamilyInstance fi) =>
        fi.Location is LocationCurve lc
            ? (lc.Curve.GetEndPoint(1) - lc.Curve.GetEndPoint(0)).Normalize()
            : null;

    private static void AlignToDir(Document doc, FamilyInstance inst, XYZ origin, XYZ dir)
    {
        if (dir.IsAlmostEqualTo(XYZ.BasisX)) return;
        double angle = XYZ.BasisX.AngleTo(dir);
        if (XYZ.BasisX.CrossProduct(dir).Z < 0) angle = -angle;
        ElementTransformUtils.RotateElement(doc, inst.Id, Line.CreateUnbound(origin, XYZ.BasisZ), angle);
    }

    private static double MepRadius(ClashResult c, BoundingBoxXYZ? bb) =>
        c.TrayDiameter > 0 ? c.TrayDiameter / 2.0
        : bb is not null ? Math.Max(bb.Max.X - bb.Min.X, bb.Max.Z - bb.Min.Z) / 2.0
        : 0;

    private static double HalfWidth(ClashResult c, BoundingBoxXYZ? bb) =>
        c.TrayWidth > 0 ? c.TrayWidth / 2.0
        : bb is not null ? (bb.Max.X - bb.Min.X) / 2.0
        : 0.25;

    private static double HalfHeight(ClashResult c, BoundingBoxXYZ? bb) =>
        c.TrayHeight > 0 ? c.TrayHeight / 2.0
        : bb is not null ? (bb.Max.Z - bb.Min.Z) / 2.0
        : 0.25;

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
            MepX          = c.X, MepY = c.Y, MepZ = c.Z,
            DocumentPath  = docPath,
            Timestamp     = DateTime.UtcNow
        });
    }
}
