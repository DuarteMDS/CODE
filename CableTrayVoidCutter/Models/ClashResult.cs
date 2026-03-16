using Autodesk.Revit.DB;

namespace CableTrayVoidCutter.Models;

/// <summary>
/// Identifies whether the clashing host element lives in the active document
/// or inside a linked Revit model.
/// </summary>
public enum ElementSource
{
    Host,
    Linked
}

/// <summary>
/// Represents a single detected collision between a Cable Tray and a
/// Wall or Structural Beam (in the host model or a linked model).
/// </summary>
public class ClashResult
{
    // ── Cable tray (always in host document) ─────────────────────────────────
    public ElementId CableTrayId   { get; set; } = ElementId.InvalidElementId;
    public string    CableTrayName { get; set; } = string.Empty;

    // ── Clashing element ─────────────────────────────────────────────────────
    public ElementId   ClashingElementId   { get; set; } = ElementId.InvalidElementId;
    public string      ClashingElementName { get; set; } = string.Empty;
    public ClashType   ClashType           { get; set; }
    public ElementSource Source            { get; set; }

    /// <summary>
    /// Populated when <see cref="Source"/> == <see cref="ElementSource.Linked"/>.
    /// </summary>
    public ElementId?  LinkInstanceId      { get; set; }
    public string      LinkName            { get; set; } = string.Empty;

    /// <summary>
    /// World-space transform of the link (identity for host elements).
    /// Stored so the placer doesn't need to re-query it.
    /// </summary>
    public Transform   LinkTransform       { get; set; } = Transform.Identity;

    // ── Computed intersection geometry ───────────────────────────────────────
    /// <summary>Intersection solid (in host world coordinates).</summary>
    public Solid?      IntersectionSolid   { get; set; }

    /// <summary>Centre of the intersection bounding box (host coords).</summary>
    public XYZ         IntersectionMidPoint { get; set; } = XYZ.Zero;

    // ── UI helpers ────────────────────────────────────────────────────────────
    public bool IsSelected { get; set; } = true;

    public string DisplayName =>
        $"{CableTrayName}  ↔  {ClashingElementName}" +
        (Source == ElementSource.Linked ? $"  [{LinkName}]" : "  [Host]");

    public string ClashTypeDisplay => ClashType switch
    {
        ClashType.Wall  => "Wall",
        ClashType.Beam  => "Beam",
        _               => "Unknown"
    };
}

public enum ClashType
{
    Wall,
    Beam
}
