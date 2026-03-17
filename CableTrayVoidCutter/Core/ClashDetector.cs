using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using CableTrayVoidCutter.Models;
using WallFilter = CableTrayVoidCutter.Models.WallOrientationFilter;

namespace CableTrayVoidCutter.Core;

/// <summary>
/// Detects where Cable Trays / Conduits (host document) completely traverse
/// Walls in the host document or in any linked model.
///
/// "Complete traversal" means the MEP axis extends past both face-planes of the wall
/// — partial overlaps (e.g. a tray ending inside a wall) are ignored.
/// </summary>
public static class ClashDetector
{
    public static List<ClashResult> Detect(
        Document   doc,
        double     marginFeet,
        bool       scanCableTrays = true,
        bool       scanConduits   = true,
        WallFilter wallOrientation = WallFilter.Vertical)
    {
        var results     = new List<ClashResult>();
        var mepElements = CollectMepElements(doc, scanCableTrays, scanConduits);
        if (mepElements.Count == 0) return results;

        var linkInstances = new FilteredElementCollector(doc)
            .OfClass(typeof(RevitLinkInstance))
            .Cast<RevitLinkInstance>()
            .Where(li => li.GetLinkDocument() is not null)
            .ToList();

        foreach (var info in mepElements)
        {
            var traySolid = GeometryHelper.GetInflatedCableTray(info.Elem, marginFeet);
            if (traySolid is null) continue;

            // ── Host walls ────────────────────────────────────────────────────
            CheckWalls(doc, null, doc, info, traySolid, traySolid,
                       Transform.Identity, wallOrientation, results);

            // ── Linked walls ──────────────────────────────────────────────────
            foreach (var link in linkInstances)
            {
                var linkDoc       = link.GetLinkDocument();
                var linkTransform = link.GetTotalTransform();

                Solid? trayInLink;
                try { trayInLink = SolidUtils.CreateTransformed(traySolid, linkTransform.Inverse); }
                catch { continue; }

                CheckWalls(doc, link, linkDoc!, info, traySolid, trayInLink,
                           linkTransform, wallOrientation, results);
            }
        }

        return results;
    }

    // ── MEP collection ────────────────────────────────────────────────────────

    private record MepInfo(
        Element     Elem,
        MepCategory Category,
        TrayShape   Shape,
        double      Width,
        double      Height,
        double      Diameter,
        bool        IsLadder = false);

    private static List<MepInfo> CollectMepElements(
        Document doc, bool scanCableTrays, bool scanConduits)
    {
        var list = new List<MepInfo>();

        if (scanCableTrays)
        {
            foreach (var ct in new FilteredElementCollector(doc)
                .OfClass(typeof(CableTray)).WhereElementIsNotElementType().Cast<CableTray>())
            {
                var w = ct.get_Parameter(BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM)?.AsDouble()
                     ?? ct.LookupParameter("Width")?.AsDouble() ?? 0;
                var h = ct.get_Parameter(BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM)?.AsDouble()
                     ?? ct.LookupParameter("Height")?.AsDouble() ?? 0;
                list.Add(new MepInfo(ct, MepCategory.CableTray, TrayShape.Rectangular,
                                     w, h, 0, DetectLadderTray(doc, ct)));
            }
        }

        if (scanConduits)
        {
            foreach (var cond in new FilteredElementCollector(doc)
                .OfClass(typeof(Conduit)).WhereElementIsNotElementType().Cast<Conduit>())
            {
                var d = cond.get_Parameter(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM)?.AsDouble()
                     ?? cond.LookupParameter("Diameter")?.AsDouble() ?? 0;
                list.Add(new MepInfo(cond, MepCategory.Conduit, TrayShape.Circular, 0, 0, d));
            }
        }

        return list;
    }

    // ── Wall intersection check ───────────────────────────────────────────────

    private static void CheckWalls(
        Document hostDoc, RevitLinkInstance? link, Document searchDoc,
        MepInfo info, Solid traySolidWorld, Solid traySolidSearch,
        Transform linkTransform, WallFilter wallOrientation,
        List<ClashResult> results)
    {
        IList<Element> candidates;
        try
        {
            candidates = new FilteredElementCollector(searchDoc)
                .OfCategory(BuiltInCategory.OST_Walls)
                .WhereElementIsNotElementType()
                .WherePasses(new ElementIntersectsSolidFilter(traySolidSearch))
                .ToElements();
        }
        catch { return; }

        bool isLinked = link is not null;

        foreach (var elem in candidates)
        {
            if (!isLinked && elem.Id == info.Elem.Id) continue;
            if (elem is not Wall wall) continue;
            if (!WallMatchesFilter(wall, linkTransform, wallOrientation)) continue;
            if (!IsCompleteWallTraversal(info, wall, linkTransform)) continue;

            // Compute intersection midpoint in world coordinates
            Solid? worldWallSolid = null;
            if (isLinked)
            {
                var ws = GeometryHelper.GetSolid(wall);
                if (ws is not null)
                    try { worldWallSolid = SolidUtils.CreateTransformed(ws, linkTransform); } catch { }
            }

            Solid? traySolid0 = isLinked ? GeometryHelper.GetInflatedCableTray(info.Elem, 0) : null;
            Solid? intersection = null;
            if (isLinked && traySolid0 is not null && worldWallSolid is not null)
                intersection = GeometryHelper.Intersect(traySolid0, worldWallSolid);
            else if (!isLinked)
                intersection = TryIntersect(info.Elem, wall);

            var midPt = intersection is not null
                ? SolidCentroid(intersection)
                : (isLinked
                    ? BBMidWorld(info.Elem, wall, linkTransform)
                    : BBMid(info.Elem, wall));

            results.Add(new ClashResult
            {
                CableTrayId          = info.Elem.Id,
                CableTrayName        = GeometryHelper.GetElementDisplayName(info.Elem),
                MepCategory          = info.Category,
                IsLadderTray         = info.IsLadder,
                ClashingElementId    = wall.Id,
                ClashingElementName  = GeometryHelper.GetElementDisplayName(wall),
                ClashType            = ClashType.Wall,
                Source               = isLinked ? ElementSource.Linked : ElementSource.Host,
                LinkInstanceId       = link?.Id,
                LinkName             = link?.Name ?? string.Empty,
                LinkTransform        = linkTransform,
                TrayShape            = info.Shape,
                TrayWidth            = info.Width,
                TrayHeight           = info.Height,
                TrayDiameter         = info.Diameter,
                IntersectionSolid    = intersection,
                IntersectionMidPoint = midPt,
            });
        }
    }

    // ── Complete wall traversal ───────────────────────────────────────────────

    /// <summary>
    /// Returns true only when the MEP element's axis extends past BOTH faces of the wall.
    /// Prevents reporting clashes where the tray merely touches or partially overlaps.
    /// </summary>
    private static bool IsCompleteWallTraversal(MepInfo info, Wall wall, Transform toWorld)
    {
        var normal    = toWorld.OfVector(wall.Orientation).Normalize();
        var wallPt    = toWorld.OfPoint(((LocationCurve)wall.Location).Curve.GetEndPoint(0));
        double half   = wall.Width / 2.0;

        var pts = new List<XYZ>();
        if (info.Elem.Location is LocationCurve lc)
        {
            pts.Add(lc.Curve.GetEndPoint(0));
            pts.Add(lc.Curve.GetEndPoint(1));
        }
        var bb = info.Elem.get_BoundingBox(null);
        if (bb is not null)
            foreach (double x in new[] { bb.Min.X, bb.Max.X })
            foreach (double y in new[] { bb.Min.Y, bb.Max.Y })
            foreach (double z in new[] { bb.Min.Z, bb.Max.Z })
                pts.Add(new XYZ(x, y, z));

        if (pts.Count == 0) return false;
        var projs = pts.Select(p => normal.DotProduct(p - wallPt)).ToList();
        return projs.Min() < -half + 1e-4 && projs.Max() > half - 1e-4;
    }

    // ── Utilities ─────────────────────────────────────────────────────────────

    private static bool WallMatchesFilter(Wall wall, Transform t, WallFilter filter)
    {
        if (filter == WallFilter.Both) return true;
        bool isVertical = Math.Abs(t.OfVector(wall.Orientation).Z) < 0.1;
        return filter == WallFilter.Vertical ? isVertical : !isVertical;
    }

    private static bool DetectLadderTray(Document doc, CableTray ct)
    {
        var typeElem = doc.GetElement(ct.GetTypeId()) as ElementType;
        var combined = ((typeElem?.FamilyName ?? "") + " " + (typeElem?.Name ?? ct.Name ?? ""))
                       .ToLowerInvariant();
        if (combined.Contains("ladder") || combined.Contains("echel") || combined.Contains("cable l"))
            return true;
        var w = ct.get_Parameter(BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM)?.AsDouble() ?? 0;
        var h = ct.get_Parameter(BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM)?.AsDouble() ?? 0;
        return w > 0 && h > 0 && h > w * 1.5;
    }

    private static Solid? TryIntersect(Element a, Element b)
    {
        try
        {
            var s1 = GeometryHelper.GetSolid(a);
            var s2 = GeometryHelper.GetSolid(b);
            return (s1 is null || s2 is null) ? null : GeometryHelper.Intersect(s1, s2);
        }
        catch { return null; }
    }

    private static XYZ SolidCentroid(Solid solid)
    {
        var pts = solid.Faces.Cast<Face>()
            .SelectMany(f => f.GetEdgesAsCurveLoops()
                .SelectMany(l => l)
                .SelectMany(c => new[] { c.GetEndPoint(0), c.GetEndPoint(1) }))
            .ToList();
        return pts.Count == 0 ? XYZ.Zero
             : new XYZ(pts.Average(p => p.X), pts.Average(p => p.Y), pts.Average(p => p.Z));
    }

    private static XYZ BBMid(Element a, Element b)
    {
        var ba = a.get_BoundingBox(null);
        var bb = b.get_BoundingBox(null);
        return (ba is null || bb is null) ? XYZ.Zero : GeometryHelper.BoundingBoxMidPoint(ba, bb);
    }

    private static XYZ BBMidWorld(Element tray, Element linked, Transform t)
    {
        var bt = tray.get_BoundingBox(null);
        var bl = linked.get_BoundingBox(null);
        if (bt is null || bl is null) return XYZ.Zero;
        var wMin = new XYZ(new[] { t.OfPoint(bl.Min).X, t.OfPoint(bl.Max).X }.Min(),
                           new[] { t.OfPoint(bl.Min).Y, t.OfPoint(bl.Max).Y }.Min(),
                           new[] { t.OfPoint(bl.Min).Z, t.OfPoint(bl.Max).Z }.Min());
        var wMax = new XYZ(new[] { t.OfPoint(bl.Min).X, t.OfPoint(bl.Max).X }.Max(),
                           new[] { t.OfPoint(bl.Min).Y, t.OfPoint(bl.Max).Y }.Max(),
                           new[] { t.OfPoint(bl.Min).Z, t.OfPoint(bl.Max).Z }.Max());
        return GeometryHelper.BoundingBoxMidPoint(bt, new BoundingBoxXYZ { Min = wMin, Max = wMax });
    }
}
