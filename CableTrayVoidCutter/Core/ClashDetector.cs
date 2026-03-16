using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using CableTrayVoidCutter.Models;

namespace CableTrayVoidCutter.Core;

/// <summary>
/// Detects collisions between Cable Trays / CT Fittings / Conduits (host document)
/// and Walls / Structural Beams (host + all linked models).
/// </summary>
public static class ClashDetector
{
    /// <summary>
    /// Runs the full clash detection and returns the list of results.
    /// </summary>
    /// <param name="doc">Active (host) Revit document.</param>
    /// <param name="marginFeet">Extra clearance in feet for broad-phase detection.</param>
    /// <param name="scanCableTrays">Include cable tray straight runs.</param>
    /// <param name="scanFittings">Include cable tray fittings (elbows, tees…).</param>
    /// <param name="scanConduits">Include conduit runs.</param>
    public static List<ClashResult> Detect(
        Document doc,
        double   marginFeet,
        bool     scanCableTrays = true,
        bool     scanFittings   = true,
        bool     scanConduits   = true)
    {
        var results = new List<ClashResult>();

        // ── 1. Collect MEP elements according to filter flags ─────────────────
        var mepElements = CollectMepElements(doc, scanCableTrays, scanFittings, scanConduits);
        if (mepElements.Count == 0) return results;

        // ── 2. Collect all linked-model instances ─────────────────────────────
        var linkInstances = new FilteredElementCollector(doc)
            .OfClass(typeof(RevitLinkInstance))
            .Cast<RevitLinkInstance>()
            .Where(li => li.GetLinkDocument() is not null)
            .ToList();

        // ── 3. Process each MEP element ───────────────────────────────────────
        foreach (var info in mepElements)
        {
            var traySolid = GeometryHelper.GetInflatedCableTray(info.Elem, marginFeet);
            if (traySolid is null) continue;

            // ── Host walls ────────────────────────────────────────────────────
            CheckHostElements(doc, info, traySolid,
                              BuiltInCategory.OST_Walls, ClashType.Wall, results);

            // ── Host structural beams ─────────────────────────────────────────
            CheckHostElements(doc, info, traySolid,
                              BuiltInCategory.OST_StructuralFraming, ClashType.Beam, results);

            // ── Linked models ─────────────────────────────────────────────────
            foreach (var link in linkInstances)
            {
                var linkDoc       = link.GetLinkDocument();
                var linkTransform = link.GetTotalTransform();

                Solid? trayInLink;
                try { trayInLink = SolidUtils.CreateTransformed(traySolid, linkTransform.Inverse); }
                catch { continue; }

                CheckLinkedElements(doc, link, linkDoc, info,
                                    trayInLink, linkTransform,
                                    BuiltInCategory.OST_Walls, ClashType.Wall, results);

                CheckLinkedElements(doc, link, linkDoc, info,
                                    trayInLink, linkTransform,
                                    BuiltInCategory.OST_StructuralFraming, ClashType.Beam, results);
            }
        }

        return results;
    }

    // ── MEP element collection ────────────────────────────────────────────────

    private record MepInfo(
        Element     Elem,
        MepCategory Category,
        TrayShape   Shape,
        double      Width,
        double      Height,
        double      Diameter);

    private static List<MepInfo> CollectMepElements(
        Document doc,
        bool     scanCableTrays,
        bool     scanFittings,
        bool     scanConduits)
    {
        var list = new List<MepInfo>();

        // ── Rectangular cable trays ───────────────────────────────────────────
        if (scanCableTrays)
        {
            foreach (var ct in new FilteredElementCollector(doc)
                .OfClass(typeof(CableTray))
                .WhereElementIsNotElementType()
                .Cast<CableTray>())
            {
                var w = ct.get_Parameter(BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM)?.AsDouble()
                     ?? ct.LookupParameter("Width")?.AsDouble()
                     ?? 0;
                var h = ct.get_Parameter(BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM)?.AsDouble()
                     ?? ct.LookupParameter("Height")?.AsDouble()
                     ?? 0;
                list.Add(new MepInfo(ct, MepCategory.CableTray, TrayShape.Rectangular, w, h, 0));
            }
        }

        // ── Cable tray fittings (elbows, tees, crosses, …) ────────────────────
        if (scanFittings)
        {
            foreach (var fi in new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_CableTrayFitting)
                .WhereElementIsNotElementType()
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>())
            {
                // No standard width/height params → derive from bounding box
                var bb = fi.get_BoundingBox(null);
                double w = 0, h = 0;
                if (bb is not null)
                {
                    w = bb.Max.X - bb.Min.X;
                    h = bb.Max.Z - bb.Min.Z;
                }
                list.Add(new MepInfo(fi, MepCategory.CableTrayFitting, TrayShape.Rectangular, w, h, 0));
            }
        }

        // ── Circular conduits ─────────────────────────────────────────────────
        if (scanConduits)
        {
            foreach (var cond in new FilteredElementCollector(doc)
                .OfClass(typeof(Conduit))
                .WhereElementIsNotElementType()
                .Cast<Conduit>())
            {
                var diam = cond.get_Parameter(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM)?.AsDouble()
                        ?? cond.LookupParameter("Diameter")?.AsDouble()
                        ?? 0;
                list.Add(new MepInfo(cond, MepCategory.Conduit, TrayShape.Circular, 0, 0, diam));
            }
        }

        return list;
    }

    // ── Host element check ────────────────────────────────────────────────────

    private static void CheckHostElements(
        Document          doc,
        MepInfo           info,
        Solid             traySolid,
        BuiltInCategory   cat,
        ClashType         clashType,
        List<ClashResult> results)
    {
        IList<Element> candidates;
        try
        {
            candidates = new FilteredElementCollector(doc)
                .OfCategory(cat)
                .WhereElementIsNotElementType()
                .WherePasses(new ElementIntersectsSolidFilter(traySolid))
                .ToElements();
        }
        catch { return; }

        foreach (var elem in candidates)
        {
            if (elem.Id == info.Elem.Id) continue;

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
        Document          hostDoc,
        RevitLinkInstance link,
        Document          linkDoc,
        MepInfo           info,
        Solid             trayInLink,
        Transform         linkTransform,
        BuiltInCategory   cat,
        ClashType         clashType,
        List<ClashResult> results)
    {
        IList<Element> candidates;
        try
        {
            candidates = new FilteredElementCollector(linkDoc)
                .OfCategory(cat)
                .WhereElementIsNotElementType()
                .WherePasses(new ElementIntersectsSolidFilter(trayInLink))
                .ToElements();
        }
        catch { return; }

        foreach (var elem in candidates)
        {
            var elemSolid = GeometryHelper.GetSolid(elem);
            Solid? worldSolid = null;
            if (elemSolid is not null)
            {
                try { worldSolid = SolidUtils.CreateTransformed(elemSolid, linkTransform); }
                catch { /* ignore */ }
            }

            var traySolidHost = GeometryHelper.GetInflatedCableTray(info.Elem, 0);
            Solid? intersectionSolid = null;
            if (traySolidHost is not null && worldSolid is not null)
                intersectionSolid = GeometryHelper.Intersect(traySolidHost, worldSolid);

            var midPt = intersectionSolid is not null
                ? GetSolidCentroid(intersectionSolid)
                : GetBBMidPoint(info.Elem, hostDoc, elem, linkTransform);

            results.Add(BuildResult(info, elem, clashType,
                                    ElementSource.Linked, link.Id, link.Name, linkTransform,
                                    intersectionSolid, midPt));
        }
    }

    // ── ClashResult factory ───────────────────────────────────────────────────

    private static ClashResult BuildResult(
        MepInfo       info,
        Element       clashingElem,
        ClashType     clashType,
        ElementSource source,
        ElementId?    linkId,
        string?       linkName,
        Transform     linkTransform,
        Solid?        intersectionSolid,
        XYZ           midPt) => new()
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
        IntersectionSolid    = intersectionSolid,
        IntersectionMidPoint = midPt,
    };

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

        return new XYZ(
            pts.Average(p => p.X),
            pts.Average(p => p.Y),
            pts.Average(p => p.Z));
    }

    private static XYZ GetBBMidPoint(Element a, Element b)
    {
        var bbA = a.get_BoundingBox(null);
        var bbB = b.get_BoundingBox(null);
        if (bbA is null || bbB is null) return XYZ.Zero;
        return GeometryHelper.BoundingBoxMidPoint(bbA, bbB);
    }

    private static XYZ GetBBMidPoint(
        Element tray, Document hostDoc,
        Element linkedElem, Transform linkTransform)
    {
        var bbTray = tray.get_BoundingBox(null);
        var bbLink = linkedElem.get_BoundingBox(null);

        if (bbTray is null || bbLink is null) return XYZ.Zero;

        var corners = new[]
        {
            linkTransform.OfPoint(bbLink.Min),
            linkTransform.OfPoint(bbLink.Max),
        };

        var worldMin = new XYZ(corners.Min(p => p.X), corners.Min(p => p.Y), corners.Min(p => p.Z));
        var worldMax = new XYZ(corners.Max(p => p.X), corners.Max(p => p.Y), corners.Max(p => p.Z));

        var worldBb = new BoundingBoxXYZ { Min = worldMin, Max = worldMax };
        return GeometryHelper.BoundingBoxMidPoint(bbTray, worldBb);
    }
}
