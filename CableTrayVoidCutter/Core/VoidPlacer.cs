using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Structure;
using CableTrayVoidCutter.Models;

namespace CableTrayVoidCutter.Core;

/// <summary>
/// Creates void openings / reservation families for each <see cref="ClashResult"/>.
///
/// Void family is selected automatically from <paramref name="families"/> based on:
///   Conduit   → TargetType == "Conduit"
///   LadderTray → TargetType == "LadderTray"
///   CableTray  → TargetType == "CableTray"
///   Beam clash → TargetType == "Beam" (overrides above)
///   Fallback   → first "Both" / "Wall" entry, then first entry
///
/// Insertion point is computed with a LINE-PLANE INTERSECTION so the family
/// origin is always exactly where the MEP axis crosses the structural centre-plane.
///
/// Level placement:
///   Three-parameter overload is used so XYZ is always absolute world-space.
///   No level parameters are touched afterwards to prevent Revit from relocating
///   the element after placement.
/// </summary>
public static class VoidPlacer
{
    // ── Public entry point ────────────────────────────────────────────────────

    /// <param name="families">All configured families from settings.</param>
    /// <param name="symbolCache">Pre-loaded symbols keyed by .rfa file path (committed tx).</param>
    public static string PlaceVoids(
        Document                          doc,
        IEnumerable<ClashResult>          clashes,
        double                            marginFeet,
        IList<VoidFamilyEntry>            families,
        IDictionary<string, FamilySymbol> symbolCache,
        VoidPlacementLog?                 placementLog = null)
    {
        int wallsOk = 0, beamsOk = 0, linked = 0, failed = 0;
        string docPath = doc.PathName;

        foreach (var clash in clashes.Where(c => c.IsSelected))
        {
            try
            {
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
                        if (beam is null) { failed++; continue; }
                        var sym = SelectSymbol(clash, families, symbolCache);
                        if (sym is null) { failed++; continue; }
                        placedId = PlaceVoidOnBeam(doc, beam, clash, sym, marginFeet);
                        beamsOk++;
                    }
                }
                else // Linked
                {
                    var sym = SelectSymbol(clash, families, symbolCache);
                    if (sym is null) { failed++; continue; }
                    placedId = PlaceReservationFamily(doc, clash, sym, marginFeet);
                    linked++;
                }

                // Track placement for alignment checker
                if (placedId is not null && placementLog is not null)
                {
                    var mepBb = doc.GetElement(clash.CableTrayId)?.get_BoundingBox(null);
                    if (mepBb is not null)
                    {
                        placementLog.Upsert(new VoidPlacementRecord
                        {
                            VoidElementId = placedId.Value,
                            MepElementId  = clash.CableTrayId.Value,
                            MepX          = (mepBb.Min.X + mepBb.Max.X) / 2.0,
                            MepY          = (mepBb.Min.Y + mepBb.Max.Y) / 2.0,
                            MepZ          = (mepBb.Min.Z + mepBb.Max.Z) / 2.0,
                            DocumentPath  = docPath,
                            Timestamp     = DateTime.UtcNow
                        });
                    }
                }
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

    // ── Auto family selection ─────────────────────────────────────────────────

    /// <summary>
    /// Picks the best family symbol for a clash from the configured families list.
    /// Priority: exact MEP-type match → "Both"/"Wall" → first available.
    /// For beam clashes, "Beam" target overrides.
    /// </summary>
    public static FamilySymbol? SelectSymbol(
        ClashResult clash,
        IList<VoidFamilyEntry> families,
        IDictionary<string, FamilySymbol> symbolCache)
    {
        if (families.Count == 0) return null;

        string preferredTarget;
        if (clash.ClashType == ClashType.Beam)
        {
            preferredTarget = "Beam";
        }
        else
        {
            preferredTarget = clash.MepCategory switch
            {
                MepCategory.Conduit => "Conduit",
                _                   => clash.IsLadderTray ? "LadderTray" : "CableTray"
            };
        }

        // Try exact match first
        var entry = families.FirstOrDefault(f => f.TargetType == preferredTarget);

        // LadderTray with no dedicated entry → fall back to CableTray (e.g. CEG_Resa Wall Rectangular)
        if (entry is null && preferredTarget == "LadderTray")
            entry = families.FirstOrDefault(f => f.TargetType == "CableTray");

        // Generic fallback → "Both" / "Wall" (legacy) → first entry
        entry ??= families.FirstOrDefault(f => f.TargetType is "Both" or "Wall")
               ?? families[0];

        return entry.FilePath is not null &&
               symbolCache.TryGetValue(entry.FilePath, out var sym)
            ? sym : null;
    }

    // ── Host wall opening (native Revit opening) ──────────────────────────────

    private static ElementId? CreateWallOpening(
        Document doc, Wall wall, ClashResult clash, double margin)
    {
        var mep   = doc.GetElement(clash.CableTrayId);
        var mepBb = mep?.get_BoundingBox(null);

        // Exact crossing point: where MEP axis crosses wall centre-plane
        var center = ComputeWallCrossing(mep, mepBb, wall, Transform.Identity)
                  ?? clash.IntersectionMidPoint;

        if (center.IsAlmostEqualTo(XYZ.Zero))
        {
            if (mepBb is null) return null;
            center = GetCenter(mepBb);
        }

        var wallCurve = ((LocationCurve)wall.Location).Curve;
        var wallDir   = (wallCurve.GetEndPoint(1) - wallCurve.GetEndPoint(0)).Normalize();
        var upDir     = wallDir.CrossProduct(wall.Orientation).Normalize();

        CurveArray curveArray;

        if (clash.TrayShape == TrayShape.Circular)
        {
            double radius = clash.TrayDiameter / 2.0 + margin;
            if (radius <= 0 && mepBb is not null)
                radius = Math.Max(mepBb.Max.X - mepBb.Min.X, mepBb.Max.Z - mepBb.Min.Z) / 2.0 + margin;
            if (radius <= 0) return null;

            var p0 = center + wallDir * radius; var p1 = center - wallDir * radius;
            curveArray = new CurveArray();
            curveArray.Append(Arc.Create(p0, p1, center + upDir * radius));
            curveArray.Append(Arc.Create(p1, p0, center - upDir * radius));
        }
        else
        {
            double halfW = (clash.TrayWidth  > 0 ? clash.TrayWidth  / 2.0 : 0.25) + margin;
            double halfH = (clash.TrayHeight > 0 ? clash.TrayHeight / 2.0 : 0.25) + margin;
            if (clash.TrayWidth  <= 0 && mepBb is not null)
                halfW = (mepBb.Max.X - mepBb.Min.X) / 2.0 + margin;
            if (clash.TrayHeight <= 0 && mepBb is not null)
                halfH = (mepBb.Max.Z - mepBb.Min.Z) / 2.0 + margin;

            var p0 = center - wallDir * halfW - upDir * halfH;
            curveArray = new CurveArray();
            curveArray.Append(Line.CreateBound(p0,                               center + wallDir * halfW - upDir * halfH));
            curveArray.Append(Line.CreateBound(center + wallDir * halfW - upDir * halfH, center + wallDir * halfW + upDir * halfH));
            curveArray.Append(Line.CreateBound(center + wallDir * halfW + upDir * halfH, center - wallDir * halfW + upDir * halfH));
            curveArray.Append(Line.CreateBound(center - wallDir * halfW + upDir * halfH, p0));
        }

        return doc.Create.NewOpening(wall, curveArray, true)?.Id;
    }

    // ── Beam void (host) ─────────────────────────────────────────────────────

    private static ElementId? PlaceVoidOnBeam(
        Document doc, FamilyInstance beam,
        ClashResult clash, FamilySymbol sym, double margin)
    {
        EnsureActive(doc, sym);

        var mepBb    = doc.GetElement(clash.CableTrayId)?.get_BoundingBox(null);
        var insertPt = ComputeBeamCrossing(
                           doc.GetElement(clash.CableTrayId), mepBb, beam)
                    ?? clash.IntersectionMidPoint;
        if (insertPt is null || insertPt.IsAlmostEqualTo(XYZ.Zero))
        {
            if (mepBb is null) return null;
            insertPt = GetCenter(mepBb);
        }

        var instance = doc.Create.NewFamilyInstance(insertPt, sym, StructuralType.NonStructural);

        var beamDir = GetElementDirection(beam);
        if (beamDir is not null) AlignToDirection(doc, instance, insertPt, beamDir);

        var beamBb   = beam.get_BoundingBox(null);
        double depth = beamBb is not null ? Math.Abs(beamBb.Max.Y - beamBb.Min.Y) : 0.5;

        SetMepDimensions(instance, clash, margin);
        SetDim(instance, depth, "GA_Reservation Profondeur", "Profondeur", "Epaisseur", "Épaisseur", "Depth");

        try { InstanceVoidCutUtils.AddInstanceVoidCut(doc, beam, instance); } catch { }

        return instance.Id;
    }

    // ── Linked reservation (GA_Reservation family) ────────────────────────────

    private static ElementId? PlaceReservationFamily(
        Document doc, ClashResult clash, FamilySymbol sym, double margin)
    {
        EnsureActive(doc, sym);

        var mepElem  = doc.GetElement(clash.CableTrayId);
        var mepBb    = mepElem?.get_BoundingBox(null);

        // Get the actual structural element from the linked document
        var linkInst   = clash.LinkInstanceId is not null
                       ? doc.GetElement(clash.LinkInstanceId) as RevitLinkInstance : null;
        var linkDoc    = linkInst?.GetLinkDocument();
        var structElem = linkDoc?.GetElement(clash.ClashingElementId);

        XYZ? insertPt = null;

        if (clash.ClashType == ClashType.Wall && structElem is Wall linkedWall)
        {
            // Geometrically exact: MEP axis ∩ wall centre-plane (in world coordinates)
            insertPt = ComputeWallCrossing(mepElem, mepBb, linkedWall, clash.LinkTransform);
        }
        else if (clash.ClashType == ClashType.Floor && structElem is not null)
        {
            // Floor/slab: XY = intersection mid, Z = slab centre in world space
            var raw    = clash.IntersectionMidPoint;
            var elemBb = structElem.get_BoundingBox(null);
            if (elemBb is not null)
            {
                var c0 = clash.LinkTransform.OfPoint(elemBb.Min);
                var c1 = clash.LinkTransform.OfPoint(elemBb.Max);
                double slabZ = (c0.Z + c1.Z) / 2.0;
                insertPt = new XYZ(
                    raw.IsAlmostEqualTo(XYZ.Zero) && mepBb is not null ? GetCenter(mepBb).X : raw.X,
                    raw.IsAlmostEqualTo(XYZ.Zero) && mepBb is not null ? GetCenter(mepBb).Y : raw.Y,
                    slabZ);
            }
        }

        // Final fallback: raw intersection midpoint
        insertPt ??= clash.IntersectionMidPoint;
        if (insertPt is null || insertPt.IsAlmostEqualTo(XYZ.Zero))
        {
            if (mepBb is null) return null;
            insertPt = GetCenter(mepBb);
        }

        // Place at absolute world-space XYZ (3-param overload guarantees no level offset)
        var instance = doc.Create.NewFamilyInstance(insertPt, sym, StructuralType.NonStructural);

        // Associate with the nearest level BELOW (for schedules/tags only — does NOT move element)
        SetLevelAssociation(doc, instance, insertPt.Z);

        SetMepDimensions(instance, clash, margin);

        // Depth = actual structural thickness
        double depth = GetStructuralDepth(structElem, clash.ClashType, clash.LinkTransform);
        SetDim(instance, depth, "GA_Reservation Profondeur", "Profondeur", "Epaisseur", "Épaisseur", "Depth");

        SetText(instance, "Comments", $"RESERVATION – lien: {clash.LinkName}");

        return instance.Id;
    }

    // ── Exact insertion point: line-plane intersection ────────────────────────

    /// <summary>
    /// Computes the point where the MEP element's axis crosses the wall's
    /// centre-plane.  Works in world coordinates.
    /// <para>
    /// For straight MEP runs (LocationCurve): solves P(t) = start + t·dir on the
    /// wall's centre-plane  wallNormal·(P−wallPt) = 0.
    /// </para>
    /// <para>
    /// For fittings (no LocationCurve): projects the MEP bounding-box centre onto
    /// the wall's centre-plane.
    /// </para>
    /// </summary>
    private static XYZ? ComputeWallCrossing(
        Element? mepElem, BoundingBoxXYZ? mepBb,
        Wall wall, Transform wallToWorld)
    {
        // Wall centre-plane in world space
        var wallNormal = wallToWorld.OfVector(wall.Orientation).Normalize();
        var wallLc     = ((LocationCurve)wall.Location).Curve;
        // Reference point on wall centre-plane (any point on the centreline)
        var wallRefPt  = wallToWorld.OfPoint(wallLc.GetEndPoint(0));

        if (mepElem?.Location is LocationCurve mepLc)
        {
            // Straight run: find t where tray axis crosses the plane
            var start = mepLc.Curve.GetEndPoint(0); // host doc = world
            var end   = mepLc.Curve.GetEndPoint(1);
            var dir   = end - start;

            double denom = wallNormal.DotProduct(dir);
            if (Math.Abs(denom) < 1e-9) goto fallback; // tray parallel to wall – shouldn't happen after traversal filter

            double t     = wallNormal.DotProduct(wallRefPt - start) / denom;
            var crossing = start + dir * t;

            // Validate: crossing must be within the tray's extents (t ∈ [0,1])
            // Allow slight overshoot for very oblique angles
            if (t < -0.1 || t > 1.1) goto fallback;

            return crossing;
        }

        fallback:
        // Fittings / fallback: project MEP BB centre onto wall centre-plane
        if (mepBb is null) return null;
        var bbCenter = GetCenter(mepBb);
        double dist  = wallNormal.DotProduct(bbCenter - wallRefPt);
        return bbCenter - wallNormal * dist; // projected point on wall plane
    }

    /// <summary>
    /// Projects the MEP element's centre onto the beam axis and returns
    /// the closest point on that axis at MEP Z height.
    /// </summary>
    private static XYZ? ComputeBeamCrossing(
        Element? mepElem, BoundingBoxXYZ? mepBb, FamilyInstance beam)
    {
        if (beam.Location is not LocationCurve beamLc) return null;

        var refPt = mepBb is not null ? GetCenter(mepBb) : null;
        if (mepElem?.Location is LocationCurve mepLc)
            refPt = mepLc.Curve.GetEndPoint(0) +
                    (mepLc.Curve.GetEndPoint(1) - mepLc.Curve.GetEndPoint(0)) * 0.5;
        if (refPt is null) return null;

        var beamStart = beamLc.Curve.GetEndPoint(0);
        var beamDir   = (beamLc.Curve.GetEndPoint(1) - beamStart).Normalize();
        double t      = beamDir.DotProduct(refPt - beamStart);
        var beamPt    = beamStart + beamDir * t;

        double z = mepBb is not null ? (mepBb.Min.Z + mepBb.Max.Z) / 2.0 : beamPt.Z;
        return new XYZ(beamPt.X, beamPt.Y, z);
    }

    // ── Level association (schedule/tag only – does NOT move element) ─────────

    /// <summary>
    /// Associates the instance with the nearest level below <paramref name="z"/>
    /// by setting the level parameter directly.
    /// The 3-param <c>NewFamilyInstance</c> places at absolute XYZ; changing the
    /// level parameter afterwards sets ONLY the association, NOT the position,
    /// because we do NOT modify the elevation-from-level parameter.
    /// </summary>
    private static void SetLevelAssociation(Document doc, FamilyInstance inst, double z)
    {
        var level = GetNearestLevel(doc, z);
        if (level is null) return;

        var p = inst.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM)
             ?? inst.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM);

        if (p is not null && !p.IsReadOnly)
            p.Set(level.Id);
        // Intentionally NOT setting INSTANCE_ELEVATION_PARAM or any offset
        // to avoid Revit recalculating and moving the element.
    }

    private static Level? GetNearestLevel(Document doc, double z)
    {
        var levels = new FilteredElementCollector(doc)
            .OfClass(typeof(Level)).Cast<Level>()
            .OrderBy(l => l.Elevation).ToList();
        if (levels.Count == 0) return null;
        return levels.LastOrDefault(l => l.Elevation <= z + 1e-4) ?? levels[0];
    }

    // ── Structural depth ──────────────────────────────────────────────────────

    private static double GetStructuralDepth(Element? elem, ClashType clashType, Transform toWorld)
    {
        const double fallback = 0.5;
        if (elem is null) return fallback;

        if (clashType == ClashType.Wall && elem is Wall w) return w.Width;

        var bb = elem.get_BoundingBox(null);
        if (bb is null) return fallback;

        if (clashType == ClashType.Floor)
        {
            var c0 = toWorld.OfPoint(bb.Min);
            var c1 = toWorld.OfPoint(bb.Max);
            return Math.Abs(c1.Z - c0.Z);
        }

        return Math.Abs(bb.Max.Y - bb.Min.Y); // beam depth
    }

    // ── Family loader (call in a separate committed transaction) ─────────────

    public static FamilySymbol? LoadFamily(Document doc, string rfaPath)
    {
        if (!File.Exists(rfaPath)) return null;

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

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void EnsureActive(Document doc, FamilySymbol sym)
    {
        if (!sym.IsActive) { sym.Activate(); doc.Regenerate(); }
    }

    private static XYZ GetCenter(BoundingBoxXYZ bb) =>
        new((bb.Min.X + bb.Max.X) / 2,
            (bb.Min.Y + bb.Max.Y) / 2,
            (bb.Min.Z + bb.Max.Z) / 2);

    private static XYZ? GetElementDirection(FamilyInstance fi) =>
        fi.Location is LocationCurve lc
            ? (lc.Curve.GetEndPoint(1) - lc.Curve.GetEndPoint(0)).Normalize()
            : null;

    private static void AlignToDirection(
        Document doc, FamilyInstance inst, XYZ origin, XYZ dir)
    {
        if (dir.IsAlmostEqualTo(XYZ.BasisX)) return;
        var axis = Line.CreateUnbound(origin, XYZ.BasisZ);
        double angle = XYZ.BasisX.AngleTo(dir);
        if (XYZ.BasisX.CrossProduct(dir).Z < 0) angle = -angle;
        ElementTransformUtils.RotateElement(doc, inst.Id, axis, angle);
    }

    private static void SetMepDimensions(FamilyInstance inst, ClashResult clash, double margin)
    {
        if (clash.TrayShape == TrayShape.Circular)
        {
            double d = clash.TrayDiameter + 2 * margin;
            // Circle families: try diameter param first, then width/height equivalents
            SetDim(inst, d,
                "GA_Reservation Largeur", "Largeur",
                "Diametre", "Diamètre", "Diameter",
                "Width");
            SetDim(inst, d,
                "GA_Reservation Longueur", "Longueur",
                "Diametre", "Diamètre", "Diameter",
                "Height");
        }
        else
        {
            SetDim(inst, clash.TrayWidth  + 2 * margin,
                "GA_Reservation Largeur", "Largeur", "Width");
            SetDim(inst, clash.TrayHeight + 2 * margin,
                "GA_Reservation Longueur", "Longueur", "Height");
        }
    }

    private static void SetDim(FamilyInstance inst, double v, params string[] names)
    {
        foreach (var name in names)
        {
            var p = inst.LookupParameter(name);
            if (p is not null && !p.IsReadOnly && p.StorageType == StorageType.Double)
            {
                p.Set(v);
                return;
            }
        }
    }

    private static void SetText(FamilyInstance inst, string name, string value)
    {
        var p = inst.LookupParameter(name);
        if (p is not null && !p.IsReadOnly && p.StorageType == StorageType.String)
            p.Set(value);
    }
}
