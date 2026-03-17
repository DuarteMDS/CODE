using System.ComponentModel;
using System.Runtime.CompilerServices;
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

/// <summary>Cross-section shape of the MEP element (cable tray or conduit).</summary>
public enum TrayShape
{
    Rectangular,
    Circular
}

/// <summary>Type of the MEP source element.</summary>
public enum MepCategory
{
    CableTray,
    CableTrayFitting,
    Conduit
}

/// <summary>
/// Represents a single detected collision between a Cable Tray and a
/// Wall or Structural Beam (in the host model or a linked model).
/// </summary>
public class ClashResult : INotifyPropertyChanged
{
    // ── MEP source element (always in host document) ──────────────────────────
    public ElementId    CableTrayId       { get; set; } = ElementId.InvalidElementId;
    public string       CableTrayName     { get; set; } = string.Empty;
    public MepCategory  MepCategory       { get; set; } = MepCategory.CableTray;

    /// <summary>
    /// True when the cable tray belongs to a ladder-tray family
    /// (detected by family/type name keywords: "ladder", "échelle", "echelon").
    /// </summary>
    public bool IsLadderTray { get; set; }

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

    // ── MEP element shape & dimensions (in Revit internal feet) ─────────────
    /// <summary>Cross-section shape: Rectangular (cable tray) or Circular (conduit).</summary>
    public TrayShape TrayShape    { get; set; } = TrayShape.Rectangular;

    /// <summary>Tray width (feet) – for rectangular trays.</summary>
    public double    TrayWidth    { get; set; }

    /// <summary>Tray height (feet) – for rectangular trays.</summary>
    public double    TrayHeight   { get; set; }

    /// <summary>Outer diameter (feet) – for circular conduits.</summary>
    public double    TrayDiameter { get; set; }

    // ── Computed intersection geometry ───────────────────────────────────────
    /// <summary>Intersection solid (in host world coordinates).</summary>
    public Solid?      IntersectionSolid   { get; set; }

    /// <summary>Centre of the intersection bounding box (host coords).</summary>
    public XYZ         IntersectionMidPoint { get; set; } = XYZ.Zero;

    // ── UI helpers ────────────────────────────────────────────────────────────
    private bool _isSelected = true;
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public string DisplayName =>
        $"{CableTrayName}  ↔  {ClashingElementName}" +
        (Source == ElementSource.Linked ? $"  [{LinkName}]" : "  [Host]");

    public string ClashTypeDisplay => ClashType switch
    {
        ClashType.Wall  => "Wall",
        ClashType.Beam  => "Beam",
        _               => "Unknown"
    };

    public string TrayShapeDisplay => TrayShape == TrayShape.Circular
        ? $"⬤ Ø{TrayDiameter * 304.8:F0} mm"
        : $"▬ {TrayWidth * 304.8:F0}×{TrayHeight * 304.8:F0} mm";

    public string MepCategoryDisplay => MepCategory switch
    {
        MepCategory.CableTray        => IsLadderTray ? "Ladder Tray" : "Cable Tray",
        MepCategory.CableTrayFitting => "CT Fitting",
        MepCategory.Conduit          => "Conduit",
        _                            => "MEP"
    };

    // ── INotifyPropertyChanged ────────────────────────────────────────────────
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public enum ClashType
{
    Wall,
    Beam
}
