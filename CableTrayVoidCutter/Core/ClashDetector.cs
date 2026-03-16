using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using CableTrayVoidCutter.Models;

namespace CableTrayVoidCutter.Core;

/// <summary>
/// Detects collisions between Cable Trays / Conduits (host document) and
/// Walls / Structural Beams (host + all linked models).
/// </summary>
public static class ClashDetector
{
    /// <summary>
    /// Runs the full clash detection and returns the list of results.
    /// </summary>
    public static List<ClashResult> Detect(Document doc, double marginFeet)
    {
        var results = new List<ClashResult>();

        // ── 1. Collect all cable trays and conduits in the host document ──────
        var mepElements = CollectMepElements(doc);
        if (mepElements.Count == 0) return results;

        // ── 2. Collect all linked-model instances ─────────────────────────────
        var linkInstances = new FilteredElementCollector(doc)
            .OfClass(typeof(RevitLinkInstance))
            .Cast<RevitLinkInstance>()
            .Where(li => li.GetLinkDocument() is not null)
            .ToList();

        // ── 3. Process each MEP element ───────────────────────────────────────
        foreach (var (mep, shape, w, h, diam) in mepElements)
        {
            var trayName  = GeometryHelper.GetElementDisplayName(mep);
            var traySolid = GeometryHelper.GetInflatedCableTray(mep, marginFeet);
            if (traySolid is null) continue;

            // ── 3a. Host-document walls ───────────────────────────────────────
            CheckHostElements(doc, mep, trayName, shape, w, h, diam,
                              traySolid, BuiltInCategory.OST_Walls,
                              ClashType.Wall, results);

            // ── 3b. Host-document structural beams ────────────────────────────
            CheckHostElements(doc, mep, trayName, shape, w, h, diam,
                              traySolid, BuiltInCategory.OST_StructuralFraming,
                              ClashType.Beam, results);

            // ── 3c. Linked models ─────────────────────────────────────────────
            foreach (var link in linkInstances)
            {
                var linkDoc       = link.GetLinkDocument();
                var linkTransform = link.GetTotalTransform();

                Solid? trayInLink;
                try { trayInLink = SolidUtils.CreateTransformed(traySolid, linkTransform.Inverse); }
                catch { continue; }

                CheckLinkedElements(doc, link, linkDoc, mep, trayName, shape, w, h, diam,
                                    trayInLink, linkTransform,
                                    BuiltInCategory.OST_Walls,
                                    ClashType.Wall, results);

                CheckLinkedElements(doc, link, linkDoc, mep, trayName, shape, w, h, diam,
                                    trayInLink, linkTransform,
                                    BuiltInCategory.OST_StructuralFraming,
                                    ClashType.Beam, results);
            }
        }

        return results;
    }

    // ── MEP element collection ────────────────────────────────────────────────

    /// <summary>
    /// Returns all cable trays and conduits with their shape and dimensions.
    /// Dimensions in Revit internal feet.
    /// </summary>
    private static List<(Element elem, TrayShape shape, double width, double height, double diameter)>
        CollectMepElements(Document doc)
    {
        var list = new List<(Element, TrayShape, double, double, double)>();

        // Rectangular cable trays
        foreach (var ct in new FilteredElementCollector(doc)
            .OfClass(typeof(CableTray))
            .WhereElementIsNotElementType()
            .Cast<CableTray>())
        {
            var w = ct.LookupParameter("Width")?.AsDouble()
                 ?? ct.get_Parameter(BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM)?.AsDouble()
                 ?? 0;
            var h = ct.LookupParameter("Height")?.AsDouble()
                 ?? ct.get_Parameter(BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM)?.AsDouble()
                 ?? 0;
            list.Add((ct, TrayShape.Rectangular, w, h, 0));
        }

        // Circular conduits
        foreach (var cond in new FilteredElementCollector(doc)
            .OfClass(typeof(Conduit))
            .WhereElementIsNotElementType()
            .Cast<Conduit>())
        {
            var diam = cond.get_Parameter(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM)?.AsDouble()
                    ?? cond.LookupParameter("Diameter")?.AsDouble()
                    ?? 0;
            list.Add((cond, TrayShape.Circular, 0, 0, diam));
        }

        return list;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void CheckHostElements(
        Document      doc,
        Element       tray,
        string        trayName,
        TrayShape     trayShape,
        double        trayW, double trayH, double trayDiam,
        Solid         traySolid,
        BuiltInCategory cat,
        ClashType     clashType,
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
            if (elem.Id == tray.Id) continue;

            var intersectionSolid = TryGetIntersection(tray, elem);
            var midPt = intersectionSolid is not null
                ? GetSolidCentroid(intersectionSolid)
                : GetBBMidPoint(tray, elem);

            results.Add(new ClashResult
            {
                CableTrayId          = tray.Id,
                CableTrayName        = trayName,
                ClashingElementId    = elem.Id,
                ClashingElementName  = GeometryHelper.GetElementDisplayName(elem),
                ClashType            = clashType,
                Source               = ElementSource.Host,
                LinkTransform        = Transform.Identity,
                TrayShape            = trayShape,
                TrayWidth            = trayW,
                TrayHeight           = trayH,
                TrayDiameter         = trayDiam,
                IntersectionSolid    = intersectionSolid,
                IntersectionMidPoint = midPt,
            });
        }
    }

    private static void CheckLinkedElements(
        Document          hostDoc,
        RevitLinkInstance link,
        Document          linkDoc,
        Element           tray,
        string            trayName,
        TrayShape         trayShape,
        double            trayW, double trayH, double trayDiam,
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

        var linkName = link.Name;

        foreach (var elem in candidates)
        {
            var elemSolid = GeometryHelper.GetSolid(elem);
            Solid? worldSolid = null;
            if (elemSolid is not null)
            {
                try { worldSolid = SolidUtils.CreateTransformed(elemSolid, linkTransform); }
                catch { /* ignore */ }
            }

            var traySolidHost = GeometryHelper.GetInflatedCableTray(tray, 0);
            Solid? intersectionSolid = null;
            if (traySolidHost is not null && worldSolid is not null)
                intersectionSolid = GeometryHelper.Intersect(traySolidHost, worldSolid);

            var midPt = intersectionSolid is not null
                ? GetSolidCentroid(intersectionSolid)
                : GetBBMidPoint(tray, hostDoc, elem, linkTransform);

            results.Add(new ClashResult
            {
                CableTrayId          = tray.Id,
                CableTrayName        = trayName,
                ClashingElementId    = elem.Id,
                ClashingElementName  = GeometryHelper.GetElementDisplayName(elem),
                ClashType            = clashType,
                Source               = ElementSource.Linked,
                LinkInstanceId       = link.Id,
                LinkName             = linkName,
                LinkTransform        = linkTransform,
                TrayShape            = trayShape,
                TrayWidth            = trayW,
                TrayHeight           = trayH,
                TrayDiameter         = trayDiam,
                IntersectionSolid    = intersectionSolid,
                IntersectionMidPoint = midPt,
            });
        }
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
