using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using CableTrayVoidCutter.Models;

namespace CableTrayVoidCutter.Core;

/// <summary>
/// Detects collisions between Cable Trays (host document) and
/// Walls / Structural Beams (host + all linked models).
/// </summary>
public static class ClashDetector
{
    /// <summary>
    /// Runs the full clash detection and returns the list of results.
    /// </summary>
    /// <param name="doc">Active (host) Revit document.</param>
    /// <param name="marginFeet">
    ///   Extra clearance in feet applied to every cable-tray bounding box
    ///   for the initial broad-phase filter.
    /// </param>
    public static List<ClashResult> Detect(Document doc, double marginFeet)
    {
        var results = new List<ClashResult>();

        // ── 1. Collect all cable trays in the host document ──────────────────
        var cableTrays = new FilteredElementCollector(doc)
            .OfClass(typeof(CableTray))
            .WhereElementIsNotElementType()
            .Cast<CableTray>()
            .ToList();

        if (cableTrays.Count == 0) return results;

        // ── 2. Collect all linked-model instances ─────────────────────────────
        var linkInstances = new FilteredElementCollector(doc)
            .OfClass(typeof(RevitLinkInstance))
            .Cast<RevitLinkInstance>()
            .Where(li => li.GetLinkDocument() is not null)
            .ToList();

        var geomOptions = new Options
        {
            ComputeReferences = true,
            DetailLevel       = ViewDetailLevel.Fine
        };

        // ── 3. Process each cable tray ────────────────────────────────────────
        foreach (var tray in cableTrays)
        {
            var trayName  = GeometryHelper.GetElementDisplayName(tray);
            var traySolid = GeometryHelper.GetInflatedCableTray(tray, marginFeet);
            if (traySolid is null) continue;

            // ── 3a. Host-document walls ───────────────────────────────────────
            CheckHostElements(doc, tray, trayName, traySolid,
                              BuiltInCategory.OST_Walls,
                              ClashType.Wall, results);

            // ── 3b. Host-document structural beams ────────────────────────────
            CheckHostElements(doc, tray, trayName, traySolid,
                              BuiltInCategory.OST_StructuralFraming,
                              ClashType.Beam, results);

            // ── 3c. Linked models ─────────────────────────────────────────────
            foreach (var link in linkInstances)
            {
                var linkDoc       = link.GetLinkDocument();
                var linkTransform = link.GetTotalTransform();

                // Transform the tray solid into the link's local coordinate space
                Solid? trayInLink;
                try
                {
                    trayInLink = SolidUtils.CreateTransformed(
                        traySolid, linkTransform.Inverse);
                }
                catch { continue; }

                // Walls in link
                CheckLinkedElements(doc, link, linkDoc, tray, trayName,
                                    trayInLink, linkTransform,
                                    BuiltInCategory.OST_Walls,
                                    ClashType.Wall, results);

                // Beams in link
                CheckLinkedElements(doc, link, linkDoc, tray, trayName,
                                    trayInLink, linkTransform,
                                    BuiltInCategory.OST_StructuralFraming,
                                    ClashType.Beam, results);
            }
        }

        return results;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void CheckHostElements(
        Document      doc,
        Element       tray,
        string        trayName,
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
            // Exclude the tray itself (shouldn't happen, but guard anyway)
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
            // Transform element solid back to host world space for the intersection
            var elemSolid = GeometryHelper.GetSolid(elem);
            Solid? worldSolid = null;
            if (elemSolid is not null)
            {
                try { worldSolid = SolidUtils.CreateTransformed(elemSolid, linkTransform); }
                catch { /* ignore */ }
            }

            var traySolidHost = GeometryHelper.GetInflatedCableTray(tray, 0); // exact solid
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
        // Revit doesn't expose centroid directly; use bounding box centre
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

        // Transform bbLink corners to host space
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
