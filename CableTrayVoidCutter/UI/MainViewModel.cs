using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CableTrayVoidCutter.Core;
using CableTrayVoidCutter.Models;

namespace CableTrayVoidCutter.UI;

/// <summary>
/// ViewModel for <see cref="MainWindow"/>.
/// </summary>
public class MainViewModel : INotifyPropertyChanged
{
    private readonly Document         _doc;
    private readonly UIDocument       _uiDoc;
    private readonly AppSettings      _settings;
    private readonly VoidPlacementLog _placementLog;

    // ── Constructor ───────────────────────────────────────────────────────────

    public MainViewModel(UIDocument uiDoc, AppSettings settings)
    {
        _uiDoc        = uiDoc;
        _doc          = uiDoc.Document;
        _settings     = settings;
        _placementLog = VoidPlacementLog.Load();

        MarginMm              = settings.MarginMm;
        ScanCableTrays        = settings.ScanCableTrays;
        ScanCableTrayFittings = settings.ScanCableTrayFittings;
        ScanConduits          = settings.ScanConduits;
        _wallOrientation      = settings.WallOrientation;

        DetectClashesCommand    = new RelayCommand(DetectClashes);
        ApplyCommand            = new RelayCommand(Apply,
                                      () => ClashResults.Any(c => c.IsSelected));
        SelectAllCommand        = new RelayCommand(SelectAll,
                                      () => ClashResults.Count > 0);
        DeselectAllCommand      = new RelayCommand(DeselectAll,
                                      () => ClashResults.Count > 0);
        OpenSettingsCommand     = new RelayCommand(OpenSettings);
        ShowInRevitCommand      = new RelayCommand<ClashResult>(ShowInRevit);
        CheckAlignmentCommand   = new RelayCommand(CheckAlignment);
        ZoomToMisalignedCommand = new RelayCommand<MisalignedVoid>(ZoomToMisaligned);
    }

    // ── Bindable properties ───────────────────────────────────────────────────

    private double _marginMm;
    public double MarginMm
    {
        get => _marginMm;
        set { _marginMm = value; OnPropertyChanged(); }
    }

    private bool _scanCableTrays;
    public bool ScanCableTrays
    {
        get => _scanCableTrays;
        set { _scanCableTrays = value; OnPropertyChanged();
              _settings.ScanCableTrays = value; _settings.Save(); }
    }

    private bool _scanCableTrayFittings;
    public bool ScanCableTrayFittings
    {
        get => _scanCableTrayFittings;
        set { _scanCableTrayFittings = value; OnPropertyChanged();
              _settings.ScanCableTrayFittings = value; _settings.Save(); }
    }

    private bool _scanConduits;
    public bool ScanConduits
    {
        get => _scanConduits;
        set { _scanConduits = value; OnPropertyChanged();
              _settings.ScanConduits = value; _settings.Save(); }
    }

    private WallOrientationFilter _wallOrientation;

    public string WallOrientationLabel
    {
        get => _wallOrientation switch
        {
            WallOrientationFilter.Horizontal => "Horizontal uniquement",
            WallOrientationFilter.Both       => "Verticaux et horizontaux",
            _                                => "Verticaux uniquement",
        };
        set
        {
            _wallOrientation = value switch
            {
                "Horizontal uniquement"    => WallOrientationFilter.Horizontal,
                "Verticaux et horizontaux" => WallOrientationFilter.Both,
                _                          => WallOrientationFilter.Vertical,
            };
            OnPropertyChanged();
            _settings.WallOrientation = _wallOrientation;
            _settings.Save();
        }
    }

    private string _statusMessage = "Prêt. Cliquez sur « Détecter les conflits » pour commencer.";
    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set { _isBusy = value; OnPropertyChanged(); }
    }

    // ── Collections ───────────────────────────────────────────────────────────

    public ObservableCollection<ClashResult>    ClashResults    { get; } = [];
    public ObservableCollection<MisalignedVoid> MisalignedVoids { get; } = [];

    // ── Commands ──────────────────────────────────────────────────────────────

    public ICommand DetectClashesCommand    { get; }
    public ICommand ApplyCommand            { get; }
    public ICommand SelectAllCommand        { get; }
    public ICommand DeselectAllCommand      { get; }
    public ICommand OpenSettingsCommand     { get; }
    public ICommand ShowInRevitCommand      { get; }
    public ICommand CheckAlignmentCommand   { get; }
    public ICommand ZoomToMisalignedCommand { get; }

    // ── Actions ───────────────────────────────────────────────────────────────

    private void DetectClashes()
    {
        IsBusy = true;
        StatusMessage = "Détection des conflits…";

        ClashResults.Clear();

        try
        {
            _settings.MarginMm = MarginMm;
            _settings.Save();

            var results = ClashDetector.Detect(_doc, MarginMm / 304.8,
                ScanCableTrays, ScanCableTrayFittings, ScanConduits, _wallOrientation);

            foreach (var r in results)
                ClashResults.Add(r);

            EnrichWithStatus(ClashResults);

            int pending  = ClashResults.Count(c => c.OpeningStatus == OpeningStatus.None);
            int placed   = ClashResults.Count(c => c.OpeningStatus == OpeningStatus.Placed);
            int outdated = ClashResults.Count(c => c.OpeningStatus == OpeningStatus.Outdated);

            StatusMessage = ClashResults.Count == 0
                ? "Aucun conflit trouvé (traversées complètes uniquement)."
                : $"{ClashResults.Count} conflit(s) — " +
                  $"{pending} en attente · {placed} placé(s) · {outdated} obsolète(s).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Erreur lors de la détection : {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    private void Apply()
    {
        var selected = ClashResults.Where(c => c.IsSelected).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("Aucun conflit sélectionné.", "Cable Tray Void Cutter",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        IsBusy = true;
        StatusMessage = "Placement des réservations…";

        try
        {
            double marginFeet = MarginMm / 304.8;

            FamilySymbol? circleSymbol = null, rectSymbol = null;

            using (var loadTx = new Transaction(_doc, "Charger familles réservations"))
            {
                loadTx.Start();
                circleSymbol = VoidPlacer.LoadFamily(_doc, _settings.CircleFamilyPath);
                rectSymbol   = VoidPlacer.LoadFamily(_doc, _settings.RectangularFamilyPath);
                loadTx.Commit();
            }

            if (circleSymbol is null && rectSymbol is null)
            {
                MessageBox.Show(
                    "Aucune famille de réservation configurée.\n" +
                    "Ouvrez les Paramètres et sélectionnez les familles CEG_Resa.",
                    "Familles manquantes", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string summary;
            using (var tx = new Transaction(_doc, "Insérer réservations câblage"))
            {
                tx.Start();
                summary = VoidPlacer.PlaceVoids(_doc, selected, marginFeet,
                                                circleSymbol, rectSymbol, _placementLog);
                tx.Commit();
            }

            _placementLog.Save();
            StatusMessage = "Terminé.";
            MessageBox.Show(summary, "Cable Tray Void Cutter",
                            MessageBoxButton.OK, MessageBoxImage.Information);

            DetectClashes();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Erreur : {ex.Message}";
            MessageBox.Show($"Une erreur s'est produite :\n{ex.Message}",
                            "Cable Tray Void Cutter", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { IsBusy = false; }
    }

    private void SelectAll()  { foreach (var c in ClashResults) c.IsSelected = true; }
    private void DeselectAll(){ foreach (var c in ClashResults) c.IsSelected = false; }

    private void OpenSettings()
    {
        var win = new SettingsWindow(_settings);
        if (win.ShowDialog() == true)
        {
            _settings.Save();
            MarginMm = _settings.MarginMm;
        }
    }

    // ── Zoom / section box ────────────────────────────────────────────────────

    private void ShowInRevit(ClashResult? clash)
    {
        if (clash is null) return;
        try
        {
            var ids = new List<ElementId> { clash.CableTrayId };
            if (clash.Source == ElementSource.Host) ids.Add(clash.ClashingElementId);
            _uiDoc.Selection.SetElementIds(ids);

            ZoomWithSectionBox(ids, clash.DisplayName);
        }
        catch (Exception ex) { StatusMessage = $"Impossible de zoomer : {ex.Message}"; }
    }

    private void ZoomToMisaligned(MisalignedVoid? mv)
    {
        if (mv is null) return;
        try
        {
            var ids = new List<ElementId>();
            if (mv.VoidElement is not null) ids.Add(mv.VoidElement.Id);
            if (mv.MepElement  is not null) ids.Add(mv.MepElement.Id);
            if (ids.Count > 0) _uiDoc.Selection.SetElementIds(ids);

            ZoomWithSectionBox(ids, $"{mv.VoidName} Δ={mv.DeltaDisplay}");
        }
        catch (Exception ex) { StatusMessage = $"Erreur de zoom : {ex.Message}"; }
    }

    /// <summary>
    /// Calculates a bounding box from the given element ids, applies a section box
    /// if the active view is 3D (keeping the view freely rotatable), and zooms.
    /// Falls back to ZoomAndCenterRectangle for 2D views.
    /// </summary>
    private void ZoomWithSectionBox(IEnumerable<ElementId> ids, string statusSuffix)
    {
        XYZ? minPt = null, maxPt = null;

        foreach (var id in ids)
        {
            var bb = _doc.GetElement(id)?.get_BoundingBox(null);
            if (bb is null) continue;
            if (minPt is null) { minPt = bb.Min; maxPt = bb.Max; }
            else
            {
                minPt = new XYZ(Math.Min(minPt.X, bb.Min.X),
                                Math.Min(minPt.Y, bb.Min.Y),
                                Math.Min(minPt.Z, bb.Min.Z));
                maxPt = new XYZ(Math.Max(maxPt!.X, bb.Max.X),
                                Math.Max(maxPt.Y,  bb.Max.Y),
                                Math.Max(maxPt.Z,  bb.Max.Z));
            }
        }

        const double pad = 2.0; // ~60 cm padding

        if (_uiDoc.ActiveView is View3D view3d && minPt is not null)
        {
            var sbb = new BoundingBoxXYZ
            {
                Min = new XYZ(minPt.X - pad, minPt.Y - pad, minPt.Z - pad),
                Max = new XYZ(maxPt!.X + pad, maxPt.Y + pad, maxPt.Z + pad)
            };

            // Section box must be set inside a transaction
            using var tx = new Transaction(_doc, "Section box – zoom");
            tx.Start();
            view3d.SetSectionBox(sbb);
            view3d.IsSectionBoxActive = true;
            tx.Commit();

            // 3D view stays freely rotatable — section box only clips geometry
            _uiDoc.GetOpenUIViews()
                  .FirstOrDefault(v => v.ViewId == view3d.Id)
                  ?.ZoomAndCenterRectangle(sbb.Min, sbb.Max);
        }
        else
        {
            var uiView = _uiDoc.GetOpenUIViews()
                .FirstOrDefault(v => v.ViewId == _uiDoc.ActiveView.Id);
            if (uiView is not null && minPt is not null)
                uiView.ZoomAndCenterRectangle(
                    new XYZ(minPt.X - pad, minPt.Y - pad, minPt.Z),
                    new XYZ(maxPt!.X + pad, maxPt.Y + pad, maxPt.Z));
            else
                _uiDoc.GetOpenUIViews().FirstOrDefault()?.ZoomToFit();
        }

        StatusMessage = statusSuffix;
    }

    // ── Alignment checker ─────────────────────────────────────────────────────

    private void CheckAlignment()
    {
        MisalignedVoids.Clear();
        var results = VoidAlignmentChecker.Check(_doc, _placementLog);
        foreach (var mv in results) MisalignedVoids.Add(mv);

        StatusMessage = MisalignedVoids.Count == 0
            ? "Toutes les réservations sont alignées."
            : $"{MisalignedVoids.Count} réservation(s) à réaligner.";
    }

    // ── Opening lifecycle enrichment ──────────────────────────────────────────

    /// <summary>
    /// After detection, cross-references each clash against the placement log to set
    /// OpeningStatus: None / Placed / Outdated.
    /// Automatically deselects already-placed (up-to-date) clashes so users only
    /// need to act on new and outdated ones.
    /// </summary>
    private void EnrichWithStatus(IEnumerable<ClashResult> clashes)
    {
        const double ThreshFeet = 5.0 / 304.8; // 5 mm — small moves are noise
        string docPath = _doc.PathName;

        foreach (var clash in clashes)
        {
            var rec = _placementLog.FindByMepHost(
                clash.CableTrayId.Value,
                clash.ClashingElementId.Value,
                docPath);

            if (rec is null)
            {
                clash.OpeningStatus = OpeningStatus.None;
                clash.IsSelected    = true;
                continue;
            }

            // Check whether the MEP element has moved since placement
            var mepBb = _doc.GetElement(clash.CableTrayId)?.get_BoundingBox(null);
            if (mepBb is null)
            {
                clash.OpeningStatus = OpeningStatus.Placed;
                clash.IsSelected    = false;
                continue;
            }

            var current = new XYZ(
                (mepBb.Min.X + mepBb.Max.X) / 2,
                (mepBb.Min.Y + mepBb.Max.Y) / 2,
                (mepBb.Min.Z + mepBb.Max.Z) / 2);
            var stored = new XYZ(rec.MepX, rec.MepY, rec.MepZ);

            if (current.DistanceTo(stored) > ThreshFeet)
            {
                clash.OpeningStatus = OpeningStatus.Outdated;
                clash.IsSelected    = true;   // flag for user to re-run
            }
            else
            {
                clash.OpeningStatus = OpeningStatus.Placed;
                clash.IsSelected    = false;  // already done — skip by default
            }
        }
    }

    // ── INotifyPropertyChanged ────────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
