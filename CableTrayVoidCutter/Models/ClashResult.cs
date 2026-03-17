using System.ComponentModel;
using System.Runtime.CompilerServices;
using Autodesk.Revit.DB;

namespace CableTrayVoidCutter.Models;

/// <summary>
/// Identifies whether the clashing host element lives in the active document
/// or inside a linked Revit model.
/// </summary>
public enum ElementSource { Host, Linked }

/// <summary>Cross-section shape of the MEP element.</summary>
public enum TrayShape { Rectangular, Circular }

/// <summary>Type of the MEP source element.</summary>
public enum MepCategory { CableTray, CableTrayFitting, Conduit }

/// <summary>
/// Represents a single detected collision between a MEP element and a
/// Wall / Structural Beam / Floor (host or linked model).
/// </summary>
public class ClashResult : INotifyPropertyChanged
{
    // ── MEP source element ────────────────────────────────────────────────────
    public ElementId   CableTrayId   { get; set; } = ElementId.InvalidElementId;
    public string      CableTrayName { get; set; } = string.Empty;
    public MepCategory MepCategory   { get; set; } = MepCategory.CableTray;

    /// <summary>True when the tray family name/type indicates an échelle à câbles.</summary>
    public bool IsLadderTray { get; set; }

    // ── Clashing element ─────────────────────────────────────────────────────
    public ElementId     ClashingElementId   { get; set; } = ElementId.InvalidElementId;
    public string        ClashingElementName { get; set; } = string.Empty;
    public ClashType     ClashType           { get; set; }
    public ElementSource Source              { get; set; }

    public ElementId? LinkInstanceId { get; set; }
    public string     LinkName       { get; set; } = string.Empty;
    public Transform  LinkTransform  { get; set; } = Transform.Identity;

    // ── MEP geometry (internal feet) ─────────────────────────────────────────
    public TrayShape TrayShape    { get; set; } = TrayShape.Rectangular;
    public double    TrayWidth    { get; set; }
    public double    TrayHeight   { get; set; }
    public double    TrayDiameter { get; set; }

    // ── Intersection ─────────────────────────────────────────────────────────
    public Solid? IntersectionSolid    { get; set; }
    public XYZ    IntersectionMidPoint { get; set; } = XYZ.Zero;

    // ── UI state (with INotifyPropertyChanged) ────────────────────────────────

    private bool _isSelected = true;
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Void family entry assigned to this clash (auto-selected or overridden per row).
    /// When changed on a selected row the ViewModel propagates it to all other selected rows.
    /// </summary>
    private VoidFamilyEntry? _assignedFamily;
    public VoidFamilyEntry? AssignedFamily
    {
        get => _assignedFamily;
        set { _assignedFamily = value; OnPropertyChanged(); }
    }

    // ── Display helpers ───────────────────────────────────────────────────────

    public string DisplayName =>
        $"{CableTrayName}  ↔  {ClashingElementName}" +
        (Source == ElementSource.Linked ? $"  [{LinkName}]" : "  [Host]");

    public string ClashTypeDisplay => ClashType switch
    {
        ClashType.Wall  => "Mur",
        ClashType.Beam  => "Poutre",
        ClashType.Floor => "Dalle",
        _               => "?"
    };

    public string TrayShapeDisplay => TrayShape == TrayShape.Circular
        ? $"⬤ Ø{TrayDiameter * 304.8:F0} mm"
        : $"▬ {TrayWidth * 304.8:F0}×{TrayHeight * 304.8:F0} mm";

    public string MepCategoryDisplay => MepCategory switch
    {
        MepCategory.CableTray        => IsLadderTray ? "Échelle câbles" : "Chemin câbles",
        MepCategory.CableTrayFitting => "Raccord CT",
        MepCategory.Conduit          => "Conduit",
        _                            => "MEP"
    };

    // ── INotifyPropertyChanged ────────────────────────────────────────────────
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public enum ClashType { Wall, Beam, Floor }
