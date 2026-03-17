using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using CableTrayVoidCutter.Models;
using WallFilter = CableTrayVoidCutter.Models.WallOrientationFilter;

namespace CableTrayVoidCutter.Core;

/// <summary>
/// Detects collisions between Cable Trays / CT Fittings / Conduits (host document)
/// and Walls / Structural Beams / Floors (host + all linked models).
///
/// Only clashes where the MEP element COMPLETELY traverses the structural element are
/// reported: for walls the tray must extend past both faces; for floors/slabs the tray
/// must extend above and below the slab.  Beams always pass (complex 3D geometry).
/// </summary>
public static class ClashDetector
{
    public static List<ClashResult> Detect(
        Document   doc,
        double     marginFeet,
        bool       scanCableTrays  = true,
        bool       scanFittings    = true,
        bool       scanConduits    = true,
        WallFilter wallOrientation = WallFilter.Vertical)
    {
        var results      = new List<ClashResult>();
        var mepElements  = CollectMepElements(doc, scanCableTrays, scanFittings, scanConduits);
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

            // ── Host: walls, beams ────────────────────────────────────────────
            CheckHostElements(doc, info, traySolid,
                              BuiltInCategory.OST_Walls,
                              ClashType.Wall, wallOrientation, results);

            CheckHostElements(doc, info, traySolid,
                              BuiltInCategory.OST_StructuralFraming,
                              ClashType.Beam, WallFilter.Both, results);

            // ── Linked: walls, beams, floors ──────────────────────────────────
            foreach (var link in linkInstances)
            {
                var linkDoc       = link.GetLinkDocument();
                var linkTransform = link.GetTotalTransform();

                Solid? trayInLink;
                try { trayInLink = SolidUtils.CreateTransformed(traySolid, linkTransform.Inverse); }
                catch { continue; }

                CheckLinkedElements(doc, link, linkDoc, info, trayInLink, linkTransform,
                                    BuiltInCategory.OST_Walls,
                                    ClashType.Wall, wallOrientation, results);

                CheckLinkedElements(doc, link, linkDoc, info, trayInLink, linkTransform,
                                    BuiltInCategory.OST_StructuralFraming,
                                    ClashType.Beam, WallFilter.Both, results);

                // Floors / slabs in linked models
                foreach (var floorCat in FloorCategories)
                {
                    CheckLinkedElements(doc, link, linkDoc, info, trayInLink, linkTransform,
                                        floorCat, ClashType.Floor, WallFilter.Both, results);
                }
            }
        }

        return results;
    }

    private static readonly BuiltInCategory[] FloorCategories =
    [
        BuiltInCategory.OST_Floors,
        BuiltInCategory.OST_Ceilings,
        BuiltInCategory.OST_Roofs,
        BuiltInCategory.OST_StructuralFoundation,
    ];

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
        Document doc, bool scanCableTrays, bool scanFittings, bool scanConduits)
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

        if (scanFittings)
        {
            foreach (var fi in new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_CableTrayFitting)
                .WhereElementIsNotElementType().OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>())
            {
                var bb = fi.get_BoundingBox(null);
                double w = bb is not null ? bb.Max.X - bb.Min.X : 0;
                double h = bb is not null ? bb.Max.Z - bb.Min.Z : 0;
                list.Add(new MepInfo(fi, MepCategory.CableTrayFitting, TrayShape.Rectangular, w, h, 0));
            }
        }

        if (scanConduits)
        {
            foreach (var cond in new FilteredElementCollector(doc)
                .OfClass(typeof(Conduit)).WhereElementIsNotElementType().Cast<Conduit>())
            {
                var diam = cond.get_Parameter(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM)?.AsDouble()
                        ?? cond.LookupParameter("Diameter")?.AsDouble() ?? 0;
                list.Add(new MepInfo(cond, MepCategory.Conduit, TrayShape.Circular, 0, 0, diam));
            }
        }

        return list;
    }

    // ── Host element check ────────────────────────────────────────────────────

    private static void CheckHostElements(
        Document doc, MepInfo info, Solid traySolid,
        BuiltInCategory cat, ClashType clashType, WallFilter wallOrientation,
        List<ClashResult> results)
    {
        IList<Element> candidates;
        try
        {
            candidates = new FilteredElementCollector(doc)
                .OfCategory(cat).WhereElementIsNotElementType()
                .WherePasses(new ElementIntersectsSolidFilter(traySolid))
                .ToElements();
        }
        catch { return; }

        foreach (var elem in candidates)
        {
            if (elem.Id == info.Elem.Id) continue;

            if (elem is Wall w && !WallMatchesFilter(w, Transform.Identity, wallOrientation))
                continue;

            // ── Complete traversal gate ────────────────────────────────────
            if (!IsCompleteTraversal(info, elem, Transform.Identity, clashType))
                continue;

            var intersectionSolid = TryGetIntersection(info.Elem, elem);
            var midPt = intersectionSolid is not null
                ? GetSolidCentroid(intersectionSolid)
                : GetBBMidPoint(info.Elem, elem);

            results.Add(BuildResult(info, elem, clashType,
                                    ElementSource.Host, null, null, Transform.Identity,
                                    intersectionSolid, midPt));
        }
    }

    // ── Linked element check ──────────────────────────────────────────────────

    private static void CheckLinkedElements(
        Document hostDoc, RevitLinkInstance link, Document linkDoc,
        MepInfo info, Solid trayInLink, Transform linkTransform,
        BuiltInCategory cat, ClashType clashType, WallFilter wallOrientation,
        List<ClashResult> results)
    {
        IList<Element> candidates;
        try
        {
            candidates = new FilteredElementCollector(linkDoc)
                .OfCategory(cat).WhereElementIsNotElementType()
                .WherePasses(new ElementIntersectsSolidFilter(trayInLink))
                .ToElements();
        }
        catch { return; }

        foreach (var elem in candidates)
        {
            if (elem is Wall lw && !WallMatchesFilter(lw, linkTransform, wallOrientation))
                continue;

            // ── Complete traversal gate ────────────────────────────────────
            if (!IsCompleteTraversal(info, elem, linkTransform, clashType))
                continue;

            var elemSolid = GeometryHelper.GetSolid(elem);
            Solid? worldSolid = null;
            if (elemSolid is not null)
                try { worldSolid = SolidUtils.CreateTransformed(elemSolid, linkTransform); }
                catch { /* ignore */ }

            var traySolidHost    = GeometryHelper.GetInflatedCableTray(info.Elem, 0);
            Solid? intersection  = null;
            if (traySolidHost is not null && worldSolid is not null)
                intersection = GeometryHelper.Intersect(traySolidHost, worldSolid);

            var midPt = intersection is not null
                ? GetSolidCentroid(intersection)
                : GetBBMidPoint(info.Elem, hostDoc, elem, linkTransform);

            results.Add(BuildResult(info, elem, clashType,
                                    ElementSource.Linked, link.Id, link.Name, linkTransform,
                                    intersection, midPt));
        }
    }

    // ── Complete traversal check ──────────────────────────────────────────────

    /// <summary>
    /// Returns true only when the MEP element's geometry completely spans through
    /// the structural element (both wall faces for walls; both slab faces for floors).
    /// Beams are always accepted (complex 3D geometry, no simple traversal axis).
    /// </summary>
    private static bool IsCompleteTraversal(
        MepInfo info, Element structElem, Transform toWorld, ClashType clashType)
    {
        if (clashType == ClashType.Beam) return true;

        if (clashType == ClashType.Wall && structElem is Wall wall)
            return IsCompleteWallTraversal(info, wall, toWorld);

        if (clashType == ClashType.Floor)
            return IsCompleteFloorTraversal(info, structElem, toWorld);

        return true;
    }

    /// <summary>
    /// Wall traversal: MEP must extend past both face planes of the wall.
    /// Projections in the wall-normal direction must straddle ±halfThickness.
    /// </summary>
    private static bool IsCompleteWallTraversal(MepInfo info, Wall wall, Transform toWorld)
    {
        var wallNormal = toWorld.OfVector(wall.Orientation).Normalize();
        var wallCurve  = ((LocationCurve)wall.Location).Curve;
        var wallPt     = toWorld.OfPoint(wallCurve.GetEndPoint(0));
        double halfThick = wall.Width / 2.0;

        var (pMin, pMax) = GetMepNormalExtent(info.Elem, wallNormal, wallPt);

        // MEP must reach beyond both faces (not just touch them)
        return pMin < -halfThick + 1e-4 && pMax > halfThick - 1e-4;
    }

    /// <summary>
    /// Floor/slab traversal: MEP must extend above AND below the slab.
    /// </summary>
    private static bool IsCompleteFloorTraversal(MepInfo info, Element floor, Transform toWorld)
    {
        var bb = floor.get_BoundingBox(null);
        if (bb is null) return false;

        // Transform slab BB corners to world Z values
        var corners = new[] { toWorld.OfPoint(bb.Min), toWorld.OfPoint(bb.Max) };
        double slabMinZ = corners.Min(p => p.Z);
        double slabMaxZ = corners.Max(p => p.Z);

        var mepBb = info.Elem.get_BoundingBox(null);
        if (mepBb is null) return false;

        return mepBb.Min.Z < slabMinZ - 1e-4 && mepBb.Max.Z > slabMaxZ + 1e-4;
    }

    /// <summary>
    /// Projects all key points of the MEP element onto <paramref name="normal"/> and
    /// returns the min/max signed distances from <paramref name="origin"/>.
    /// Uses the location curve endpoints (if available) plus all 8 bounding-box corners.
    /// </summary>
    private static (double min, double max) GetMepNormalExtent(
        Element elem, XYZ normal, XYZ origin)
    {
        var pts = new List<XYZ>();

        // Location curve endpoints (straight runs have meaningful start/end)
        if (elem.Location is LocationCurve lc)
        {
            pts.Add(lc.Curve.GetEndPoint(0));
            pts.Add(lc.Curve.GetEndPoint(1));
        }

        // 8 bounding-box corners (handles fittings and non-straight runs)
        var bb = elem.get_BoundingBox(null);
        if (bb is not null)
        {
            foreach (double x in new[] { bb.Min.X, bb.Max.X })
            foreach (double y in new[] { bb.Min.Y, bb.Max.Y })
            foreach (double z in new[] { bb.Min.Z, bb.Max.Z })
                pts.Add(new XYZ(x, y, z));
        }

        if (pts.Count == 0) return (0, 0);

        var projs = pts.Select(p => normal.DotProduct(p - origin)).ToList();
        return (projs.Min(), projs.Max());
    }

    // ── Result factory ────────────────────────────────────────────────────────

    private static ClashResult BuildResult(
        MepInfo info, Element clashingElem, ClashType clashType,
        ElementSource source, ElementId? linkId, string? linkName,
        Transform linkTransform, Solid? intersectionSolid, XYZ midPt) => new()
    {
        CableTrayId          = info.Elem.Id,
        CableTrayName        = GeometryHelper.GetElementDisplayName(info.Elem),
        MepCategory          = info.Category,
        ClashingElementId    = clashingElem.Id,
        ClashingElementName  = GeometryHelper.GetElementDisplayName(clashingElem),
        ClashType            = clashType,
        Source               = source,
        LinkInstanceId       = linkId,
        LinkName             = linkName ?? string.Empty,
        LinkTransform        = linkTransform,
        TrayShape            = info.Shape,
        TrayWidth            = info.Width,
        TrayHeight           = info.Height,
        TrayDiameter         = info.Diameter,
        IsLadderTray         = info.IsLadder,
        IntersectionSolid    = intersectionSolid,
        IntersectionMidPoint = midPt,
    };

    // ── Ladder-tray detection ─────────────────────────────────────────────────

    private static bool DetectLadderTray(Document doc, CableTray ct)
    {
        var typeElem   = doc.GetElement(ct.GetTypeId()) as ElementType;
        var familyName = typeElem?.FamilyName ?? string.Empty;
        var typeName   = typeElem?.Name       ?? ct.Name ?? string.Empty;
        var combined   = (familyName + " " + typeName).ToLowerInvariant();

        if (combined.Contains("ladder") || combined.Contains("echel") ||
            combined.Contains("câble l") || combined.Contains("cable l"))
            return true;

        var w = ct.get_Parameter(BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM)?.AsDouble() ?? 0;
        var h = ct.get_Parameter(BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM)?.AsDouble() ?? 0;
        return w > 0 && h > 0 && h > w * 1.5;
    }

    // ── Geometry utilities ────────────────────────────────────────────────────

    private static Solid? TryGetIntersection(Element tray, Element other)
    {
        try
        {
            var s1 = GeometryHelper.GetSolid(tray);
            var s2 = GeometryHelper.GetSolid(other);
            if (s1 is null || s2 is null) return null;
            return GeometryHelper.Intersect(s1, s2);
        }
        catch { return null; }
    }

    private static XYZ GetSolidCentroid(Solid solid)
    {
        var pts = solid.Faces
            .Cast<Face>()
            .SelectMany(f => f.GetEdgesAsCurveLoops()
                .SelectMany(l => l)
                .SelectMany(c => new[] { c.GetEndPoint(0), c.GetEndPoint(1) }))
            .ToList();

        if (pts.Count == 0) return XYZ.Zero;
        return new XYZ(pts.Average(p => p.X), pts.Average(p => p.Y), pts.Average(p => p.Z));
    }

    private static XYZ GetBBMidPoint(Element a, Element b)
    {
        var bbA = a.get_BoundingBox(null);
        var bbB = b.get_BoundingBox(null);
        if (bbA is null || bbB is null) return XYZ.Zero;
        return GeometryHelper.BoundingBoxMidPoint(bbA, bbB);
    }

    private static bool WallMatchesFilter(Wall wall, Transform transform, WallFilter filter)
    {
        if (filter == WallFilter.Both) return true;
        var worldNormal = transform.OfVector(wall.Orientation);
        bool isVertical = Math.Abs(worldNormal.Z) < 0.1;
        return filter == WallFilter.Vertical ? isVertical : !isVertical;
    }

    private static XYZ GetBBMidPoint(
        Element tray, Document hostDoc, Element linkedElem, Transform linkTransform)
    {
        var bbTray = tray.get_BoundingBox(null);
        var bbLink = linkedElem.get_BoundingBox(null);
        if (bbTray is null || bbLink is null) return XYZ.Zero;

        var corners  = new[] { linkTransform.OfPoint(bbLink.Min), linkTransform.OfPoint(bbLink.Max) };
        var worldMin = new XYZ(corners.Min(p => p.X), corners.Min(p => p.Y), corners.Min(p => p.Z));
        var worldMax = new XYZ(corners.Max(p => p.X), corners.Max(p => p.Y), corners.Max(p => p.Z));
        var worldBb  = new BoundingBoxXYZ { Min = worldMin, Max = worldMax };
        return GeometryHelper.BoundingBoxMidPoint(bbTray, worldBb);
    }
}
