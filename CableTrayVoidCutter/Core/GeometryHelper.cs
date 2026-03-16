using Autodesk.Revit.DB;

namespace CableTrayVoidCutter.Core;

/// <summary>
/// Geometry utilities shared by ClashDetector and VoidPlacer.
/// </summary>
internal static class GeometryHelper
{
    // ── Solid extraction ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns the first non-empty solid of an element, or null.
    /// Handles GeometryInstance (family instances, linked elements).
    /// </summary>
    public static Solid? GetSolid(Element element, Options? geomOptions = null)
    {
        geomOptions ??= new Options
        {
            ComputeReferences = true,
            DetailLevel       = ViewDetailLevel.Fine
        };

        var geomElem = element.get_Geometry(geomOptions);
        if (geomElem is null) return null;

        return ExtractSolid(geomElem);
    }

    private static Solid? ExtractSolid(GeometryElement geomElem)
    {
        foreach (var obj in geomElem)
        {
            switch (obj)
            {
                case Solid s when s.Volume > 1e-9:
                    return s;

                case GeometryInstance gi:
                    var s2 = ExtractSolid(gi.GetInstanceGeometry());
                    if (s2 is not null) return s2;
                    break;
            }
        }
        return null;
    }

    // ── Bounding-box solid ────────────────────────────────────────────────────

    /// <summary>
    /// Builds a box solid from a bounding box, optionally inflated by <paramref name="margin"/>
    /// on all six faces (in feet).
    /// </summary>
    public static Solid? BoundingBoxToSolid(BoundingBoxXYZ bb, double margin = 0.0)
    {
        var min = bb.Min - new XYZ(margin, margin, margin);
        var max = bb.Max + new XYZ(margin, margin, margin);

        var dx = max.X - min.X;
        var dy = max.Y - min.Y;
        var dz = max.Z - min.Z;

        if (dx <= 0 || dy <= 0 || dz <= 0) return null;

        // Build the six faces of the box
        var profiles = new List<CurveLoop>
        {
            RectLoop(
                min,
                min + new XYZ(dx, 0, 0),
                min + new XYZ(dx, dy, 0),
                min + new XYZ(0,  dy, 0))
        };

        return GeometryCreationUtilities.CreateExtrusionGeometry(
            profiles,
            XYZ.BasisZ,
            dz);
    }

    private static CurveLoop RectLoop(XYZ p0, XYZ p1, XYZ p2, XYZ p3)
    {
        var loop = new CurveLoop();
        loop.Append(Line.CreateBound(p0, p1));
        loop.Append(Line.CreateBound(p1, p2));
        loop.Append(Line.CreateBound(p2, p3));
        loop.Append(Line.CreateBound(p3, p0));
        return loop;
    }

    // ── Intersection geometry ─────────────────────────────────────────────────

    /// <summary>
    /// Computes the intersection solid between two solids.
    /// Returns null if they don't intersect or the result is empty.
    /// </summary>
    public static Solid? Intersect(Solid a, Solid b)
    {
        try
        {
            var result = BooleanOperationsUtils.ExecuteBooleanOperation(
                a, b, BooleanOperationsType.Intersect);

            return result?.Volume > 1e-12 ? result : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Returns the centre of the intersection bounding box between two bounding boxes.
    /// </summary>
    public static XYZ BoundingBoxMidPoint(BoundingBoxXYZ a, BoundingBoxXYZ b)
    {
        var minX = Math.Max(a.Min.X, b.Min.X);
        var minY = Math.Max(a.Min.Y, b.Min.Y);
        var minZ = Math.Max(a.Min.Z, b.Min.Z);

        var maxX = Math.Min(a.Max.X, b.Max.X);
        var maxY = Math.Min(a.Max.Y, b.Max.Y);
        var maxZ = Math.Min(a.Max.Z, b.Max.Z);

        return new XYZ(
            (minX + maxX) / 2.0,
            (minY + maxY) / 2.0,
            (minZ + maxZ) / 2.0);
    }

    // ── Cable-tray inflated solid ─────────────────────────────────────────────

    /// <summary>
    /// Builds an inflated solid around a cable tray for clash detection.
    /// Uses the element's bounding box expanded by <paramref name="marginFeet"/>.
    /// </summary>
    public static Solid? GetInflatedCableTray(Element cableTray, double marginFeet)
    {
        var bb = cableTray.get_BoundingBox(null);
        if (bb is null) return null;

        // Try to get the real solid first (more accurate)
        var real = GetSolid(cableTray);
        if (real is not null)
        {
            return InflateSolid(real, marginFeet) ?? BoundingBoxToSolid(bb, marginFeet);
        }

        return BoundingBoxToSolid(bb, marginFeet);
    }

    /// <summary>
    /// Inflates a solid by <paramref name="margin"/> feet on all sides
    /// using an offset solid approximation (bounding box of the solid).
    /// </summary>
    private static Solid? InflateSolid(Solid solid, double margin)
    {
        // Get AABB of the solid
        var pts = solid.Faces
            .Cast<Face>()
            .SelectMany(f => f.GetEdgesAsCurveLoops()
                .SelectMany(l => l)
                .SelectMany(c => new[] { c.GetEndPoint(0), c.GetEndPoint(1) }))
            .ToList();

        if (pts.Count == 0) return null;

        var minX = pts.Min(p => p.X) - margin;
        var minY = pts.Min(p => p.Y) - margin;
        var minZ = pts.Min(p => p.Z) - margin;
        var maxX = pts.Max(p => p.X) + margin;
        var maxY = pts.Max(p => p.Y) + margin;
        var maxZ = pts.Max(p => p.Z) + margin;

        var loop = RectLoop(
            new XYZ(minX, minY, minZ),
            new XYZ(maxX, minY, minZ),
            new XYZ(maxX, maxY, minZ),
            new XYZ(minX, maxY, minZ));

        return GeometryCreationUtilities.CreateExtrusionGeometry(
            [loop], XYZ.BasisZ, maxZ - minZ);
    }

    // ── Element name helper ───────────────────────────────────────────────────

    public static string GetElementDisplayName(Element e)
    {
        var name = e.Name;
        if (string.IsNullOrEmpty(name))
            name = e.GetType().Name;

        return $"{name} [{e.Id.Value}]";
    }
}
