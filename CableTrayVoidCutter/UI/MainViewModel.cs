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
/// Drives clash detection, void placement, settings persistence, in-Revit selection,
/// and alignment checking for previously inserted voids.
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

        RefreshFamilyLists();

        SelectedCableTrayFamily  = CableTrayFamilies.FirstOrDefault(
            f => f.FilePath == settings.LastCableTrayFamilyPath);
        SelectedLadderTrayFamily = LadderTrayFamilies.FirstOrDefault(
            f => f.FilePath == settings.LastLadderTrayFamilyPath);
        SelectedConduitFamily    = ConduitFamilies.FirstOrDefault(
            f => f.FilePath == settings.LastConduitFamilyPath);
        SelectedBeamFamily       = BeamFamilies.FirstOrDefault(
            f => f.FilePath == settings.LastBeamFamilyPath);

        DetectClashesCommand  = new RelayCommand(DetectClashes);
        ApplyCommand          = new RelayCommand(Apply,
                                    () => ClashResults.Any(c => c.IsSelected));
        SelectAllCommand      = new RelayCommand(SelectAll,
                                    () => ClashResults.Count > 0);
        DeselectAllCommand    = new RelayCommand(DeselectAll,
                                    () => ClashResults.Count > 0);
        OpenSettingsCommand   = new RelayCommand(OpenSettings);
        ShowInRevitCommand    = new RelayCommand<ClashResult>(ShowInRevit);
        CheckAlignmentCommand = new RelayCommand(CheckAlignment);
        ZoomToMisalignedCommand = new RelayCommand<MisalignedVoid>(ZoomToMisaligned);
    }

    // ── Observable properties ─────────────────────────────────────────────────

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
                _                         => WallOrientationFilter.Vertical,
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

    public ObservableCollection<ClashResult>     ClashResults         { get; } = [];
    public ObservableCollection<MisalignedVoid>  MisalignedVoids      { get; } = [];

    public ObservableCollection<VoidFamilyEntry> CableTrayFamilies    { get; } = [];
    public ObservableCollection<VoidFamilyEntry> LadderTrayFamilies   { get; } = [];
    public ObservableCollection<VoidFamilyEntry> ConduitFamilies      { get; } = [];
    public ObservableCollection<VoidFamilyEntry> BeamFamilies         { get; } = [];

    // ── Family selection ──────────────────────────────────────────────────────

    private VoidFamilyEntry? _selectedCableTrayFamily;
    public VoidFamilyEntry? SelectedCableTrayFamily
    {
        get => _selectedCableTrayFamily;
        set { _selectedCableTrayFamily = value; OnPropertyChanged();
              _settings.LastCableTrayFamilyPath = value?.FilePath; _settings.Save(); }
    }

    private VoidFamilyEntry? _selectedLadderTrayFamily;
    public VoidFamilyEntry? SelectedLadderTrayFamily
    {
        get => _selectedLadderTrayFamily;
        set { _selectedLadderTrayFamily = value; OnPropertyChanged();
              _settings.LastLadderTrayFamilyPath = value?.FilePath; _settings.Save(); }
    }

    private VoidFamilyEntry? _selectedConduitFamily;
    public VoidFamilyEntry? SelectedConduitFamily
    {
        get => _selectedConduitFamily;
        set { _selectedConduitFamily = value; OnPropertyChanged();
              _settings.LastConduitFamilyPath = value?.FilePath; _settings.Save(); }
    }

    private VoidFamilyEntry? _selectedBeamFamily;
    public VoidFamilyEntry? SelectedBeamFamily
    {
        get => _selectedBeamFamily;
        set { _selectedBeamFamily = value; OnPropertyChanged();
              _settings.LastBeamFamilyPath = value?.FilePath; _settings.Save(); }
    }

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

            double marginFeet = MarginMm / 304.8;

            var results = ClashDetector.Detect(_doc, marginFeet,
                              ScanCableTrays, ScanCableTrayFittings, ScanConduits,
                              _wallOrientation);

            foreach (var r in results)
                ClashResults.Add(r);

            StatusMessage = ClashResults.Count == 0
                ? "Aucun conflit trouvé."
                : $"{ClashResults.Count} conflit(s) trouvé(s). Sélectionnez ceux à traiter.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Erreur lors de la détection : {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Apply()
    {
        if (!ClashResults.Any(c => c.IsSelected))
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

            FamilySymbol? ctSym   = null;
            FamilySymbol? ltSym   = null;
            FamilySymbol? condSym = null;
            FamilySymbol? beamSym = null;

            // Load families in their own committed transaction
            using (var loadTx = new Transaction(_doc, "Charger familles réservations"))
            {
                loadTx.Start();
                if (SelectedCableTrayFamily  is not null)
                    ctSym   = VoidPlacer.LoadFamily(_doc, SelectedCableTrayFamily.FilePath);
                if (SelectedLadderTrayFamily is not null)
                    ltSym   = VoidPlacer.LoadFamily(_doc, SelectedLadderTrayFamily.FilePath);
                if (SelectedConduitFamily    is not null)
                    condSym = VoidPlacer.LoadFamily(_doc, SelectedConduitFamily.FilePath);
                if (SelectedBeamFamily       is not null)
                    beamSym = VoidPlacer.LoadFamily(_doc, SelectedBeamFamily.FilePath);
                loadTx.Commit();
            }

            string summary;
            using (var tx = new Transaction(_doc, "CableTray Void Cutter – Insérer réservations"))
            {
                tx.Start();
                summary = VoidPlacer.PlaceVoids(
                    _doc, ClashResults, marginFeet,
                    ctSym, ltSym, condSym, beamSym,
                    _placementLog);
                tx.Commit();
            }

            // Persist placement log after successful commit
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
                            "Cable Tray Void Cutter",
                            MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SelectAll()
    {
        foreach (var c in ClashResults) c.IsSelected = true;
    }

    private void DeselectAll()
    {
        foreach (var c in ClashResults) c.IsSelected = false;
    }

    private void OpenSettings()
    {
        var win = new SettingsWindow(_settings);
        if (win.ShowDialog() == true)
        {
            _settings.Save();
            MarginMm = _settings.MarginMm;
            RefreshFamilyLists();
        }
    }

    /// <summary>
    /// Selects elements in Revit, applies a section box if the active view is 3D,
    /// and zooms to the clash. The 3D view stays rotatable.
    /// </summary>
    private void ShowInRevit(ClashResult? clash)
    {
        if (clash is null) return;

        try
        {
            var ids = new List<ElementId> { clash.CableTrayId };

            if (clash.Source == ElementSource.Host)
                ids.Add(clash.ClashingElementId);

            _uiDoc.Selection.SetElementIds(ids);

            // Build tight bounding box around the involved elements
            XYZ? minPt = null;
            XYZ? maxPt = null;

            foreach (var id in ids)
            {
                var el = _doc.GetElement(id);
                var bb = el?.get_BoundingBox(null);
                if (bb is null) continue;

                if (minPt is null)
                {
                    minPt = bb.Min;
                    maxPt = bb.Max;
                }
                else
                {
                    minPt = new XYZ(Math.Min(minPt.X, bb.Min.X),
                                    Math.Min(minPt.Y, bb.Min.Y),
                                    Math.Min(minPt.Z, bb.Min.Z));
                    maxPt = new XYZ(Math.Max(maxPt!.X, bb.Max.X),
                                    Math.Max(maxPt.Y, bb.Max.Y),
                                    Math.Max(maxPt.Z, bb.Max.Z));
                }
            }

            const double pad = 2.0; // ~60 cm

            // ── 3D section box ────────────────────────────────────────────────
            if (_uiDoc.ActiveView is View3D view3d && minPt is not null && maxPt is not null)
            {
                // Build section box with padding; rotation is still freely available
                var sectionBb = new BoundingBoxXYZ
                {
                    Min = new XYZ(minPt.X - pad, minPt.Y - pad, minPt.Z - pad),
                    Max = new XYZ(maxPt.X + pad, maxPt.Y + pad, maxPt.Z + pad)
                };

                // Must be inside a transaction to modify the view
                using var tx = new Transaction(_doc, "Section box – zoom conflit");
                tx.Start();
                view3d.SetSectionBox(sectionBb);
                view3d.IsSectionBoxActive = true;
                tx.Commit();

                // Zoom the UI to the section box area
                var uiView3d = _uiDoc.GetOpenUIViews()
                    .FirstOrDefault(v => v.ViewId == view3d.Id);
                uiView3d?.ZoomAndCenterRectangle(sectionBb.Min, sectionBb.Max);
            }
            else
            {
                // ── 2D / plan view zoom ───────────────────────────────────────
                var uiView = _uiDoc.GetOpenUIViews()
                    .FirstOrDefault(v => v.ViewId == _uiDoc.ActiveView.Id);

                if (uiView is not null && minPt is not null && maxPt is not null)
                {
                    uiView.ZoomAndCenterRectangle(
                        new XYZ(minPt.X - pad, minPt.Y - pad, minPt.Z),
                        new XYZ(maxPt.X + pad, maxPt.Y + pad, maxPt.Z));
                }
                else
                {
                    _uiDoc.GetOpenUIViews().FirstOrDefault()?.ZoomToFit();
                }
            }

            StatusMessage = clash.Source == ElementSource.Linked
                ? $"Sélectionné : {clash.CableTrayName} (élément lié non sélectionnable)"
                : $"Sélectionné : {clash.DisplayName}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Impossible de sélectionner les éléments : {ex.Message}";
        }
    }

    /// <summary>Compares stored MEP positions with current Revit model positions.</summary>
    private void CheckAlignment()
    {
        MisalignedVoids.Clear();

        var results = VoidAlignmentChecker.Check(_doc, _placementLog);
        foreach (var mv in results)
            MisalignedVoids.Add(mv);

        StatusMessage = MisalignedVoids.Count == 0
            ? "Toutes les réservations sont alignées."
            : $"{MisalignedVoids.Count} réservation(s) à réaligner.";
    }

    /// <summary>Zooms to a misaligned void in the active view.</summary>
    private void ZoomToMisaligned(MisalignedVoid? mv)
    {
        if (mv is null) return;

        try
        {
            var ids = new List<ElementId>();
            if (mv.VoidElement  is not null) ids.Add(mv.VoidElement.Id);
            if (mv.MepElement   is not null) ids.Add(mv.MepElement.Id);
            if (ids.Count > 0) _uiDoc.Selection.SetElementIds(ids);

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
                                    Math.Max(maxPt.Y, bb.Max.Y),
                                    Math.Max(maxPt.Z, bb.Max.Z));
                }
            }

            const double pad = 2.0;

            if (_uiDoc.ActiveView is View3D view3d && minPt is not null && maxPt is not null)
            {
                var sectionBb = new BoundingBoxXYZ
                {
                    Min = new XYZ(minPt.X - pad, minPt.Y - pad, minPt.Z - pad),
                    Max = new XYZ(maxPt.X + pad, maxPt.Y + pad, maxPt.Z + pad)
                };

                using var tx = new Transaction(_doc, "Section box – réservation désalignée");
                tx.Start();
                view3d.SetSectionBox(sectionBb);
                view3d.IsSectionBoxActive = true;
                tx.Commit();

                _uiDoc.GetOpenUIViews()
                    .FirstOrDefault(v => v.ViewId == view3d.Id)
                    ?.ZoomAndCenterRectangle(sectionBb.Min, sectionBb.Max);
            }
            else
            {
                var uiView = _uiDoc.GetOpenUIViews()
                    .FirstOrDefault(v => v.ViewId == _uiDoc.ActiveView.Id);
                if (uiView is not null && minPt is not null && maxPt is not null)
                    uiView.ZoomAndCenterRectangle(
                        new XYZ(minPt.X - pad, minPt.Y - pad, minPt.Z),
                        new XYZ(maxPt.X + pad, maxPt.Y + pad, maxPt.Z));
            }

            StatusMessage = $"Désalignement : {mv.VoidName}  Δ = {mv.DeltaDisplay}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Erreur de zoom : {ex.Message}";
        }
    }

    // ── Family list refresh ───────────────────────────────────────────────────

    private void RefreshFamilyLists()
    {
        CableTrayFamilies.Clear();
        LadderTrayFamilies.Clear();
        ConduitFamilies.Clear();
        BeamFamilies.Clear();

        foreach (var f in _settings.VoidFamilies)
        {
            switch (f.TargetType)
            {
                case "CableTray":
                    CableTrayFamilies.Add(f);
                    break;
                case "LadderTray":
                    LadderTrayFamilies.Add(f);
                    break;
                case "Conduit":
                    ConduitFamilies.Add(f);
                    break;
                case "Beam":
                    BeamFamilies.Add(f);
                    break;
                case "Wall":
                    // Legacy: wall families appear in all three wall-type combos
                    CableTrayFamilies.Add(f);
                    LadderTrayFamilies.Add(f);
                    ConduitFamilies.Add(f);
                    break;
                case "Both":
                default:
                    CableTrayFamilies.Add(f);
                    LadderTrayFamilies.Add(f);
                    ConduitFamilies.Add(f);
                    BeamFamilies.Add(f);
                    break;
            }
        }
    }

    // ── INotifyPropertyChanged ────────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
