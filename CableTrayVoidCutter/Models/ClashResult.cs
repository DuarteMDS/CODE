using System.ComponentModel;
using System.Runtime.CompilerServices;
using Autodesk.Revit.DB;

namespace CableTrayVoidCutter.Models;

public enum ElementSource { Host, Linked }
public enum TrayShape     { Rectangular, Circular }
public enum MepCategory   { CableTray, CableTrayFitting, Conduit }
public enum ClashType     { Wall, Beam, Floor }

/// <summary>
/// Lifecycle status of the opening for a given clash — updated after detection
/// by cross-referencing the placement log.
/// </summary>
public enum OpeningStatus
{
    /// <summary>No opening has been placed for this clash yet.</summary>
    None,
    /// <summary>An opening has been placed and the MEP element hasn't moved since.</summary>
    Placed,
    /// <summary>An opening was placed but the MEP element has since moved (needs update).</summary>
    Outdated
}

/// <summary>
/// A single detected collision between a MEP element and a structural element.
/// </summary>
public class ClashResult : INotifyPropertyChanged
{
    // ── MEP source ────────────────────────────────────────────────────────────
    public ElementId   CableTrayId   { get; set; } = ElementId.InvalidElementId;
    public string      CableTrayName { get; set; } = string.Empty;
    public MepCategory MepCategory   { get; set; } = MepCategory.CableTray;
    public bool        IsLadderTray  { get; set; }

    // ── Clashing element ─────────────────────────────────────────────────────
    public ElementId     ClashingElementId   { get; set; } = ElementId.InvalidElementId;
    public string        ClashingElementName { get; set; } = string.Empty;
    public ClashType     ClashType           { get; set; }
    public ElementSource Source              { get; set; }

    public ElementId? LinkInstanceId { get; set; }
    public string     LinkName       { get; set; } = string.Empty;
    public Transform  LinkTransform  { get; set; } = Transform.Identity;

    // ── MEP geometry ──────────────────────────────────────────────────────────
    public TrayShape TrayShape    { get; set; } = TrayShape.Rectangular;
    public double    TrayWidth    { get; set; }
    public double    TrayHeight   { get; set; }
    public double    TrayDiameter { get; set; }

    // ── Intersection ─────────────────────────────────────────────────────────
    public Solid? IntersectionSolid    { get; set; }
    public XYZ    IntersectionMidPoint { get; set; } = XYZ.Zero;

    // ── Opening lifecycle ─────────────────────────────────────────────────────

    private OpeningStatus _openingStatus = OpeningStatus.None;

    /// <summary>
    /// Lifecycle state computed after each detection run by cross-referencing
    /// the placement log with the current MEP element position.
    /// </summary>
    public OpeningStatus OpeningStatus
    {
        get => _openingStatus;
        set
        {
            _openingStatus = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusDisplay));
        }
    }

    /// <summary>Human-readable status label used in the grid.</summary>
    public string StatusDisplay => _openingStatus switch
    {
        OpeningStatus.Placed   => "✓ Placé",
        OpeningStatus.Outdated => "⚠ Obsolète",
        _                      => "—"
    };

    // ── UI state ──────────────────────────────────────────────────────────────
    private bool _isSelected = true;
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    // ── Display ───────────────────────────────────────────────────────────────
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

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
