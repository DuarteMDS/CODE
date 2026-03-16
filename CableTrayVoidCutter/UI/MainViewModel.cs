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
/// Drives clash detection, void placement, settings persistence, and in-Revit selection.
/// </summary>
public class MainViewModel : INotifyPropertyChanged
{
    private readonly Document    _doc;
    private readonly UIDocument  _uiDoc;
    private readonly AppSettings _settings;

    // ── Constructor ───────────────────────────────────────────────────────────

    public MainViewModel(UIDocument uiDoc, AppSettings settings)
    {
        _uiDoc    = uiDoc;
        _doc      = uiDoc.Document;
        _settings = settings;

        MarginMm             = settings.MarginMm;
        ScanCableTrays        = settings.ScanCableTrays;
        ScanCableTrayFittings = settings.ScanCableTrayFittings;
        ScanConduits          = settings.ScanConduits;

        RefreshFamilyLists();

        SelectedWallFamily = WallFamilies.FirstOrDefault(
            f => f.FilePath == settings.LastWallFamilyPath);
        SelectedBeamFamily = BeamFamilies.FirstOrDefault(
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

    private string _statusMessage = "Ready. Click \"Detect Clashes\" to begin.";
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

    public ObservableCollection<ClashResult>     ClashResults    { get; } = [];
    public ObservableCollection<VoidFamilyEntry> WallFamilies    { get; } = [];
    public ObservableCollection<VoidFamilyEntry> BeamFamilies    { get; } = [];

    private VoidFamilyEntry? _selectedWallFamily;
    public VoidFamilyEntry? SelectedWallFamily
    {
        get => _selectedWallFamily;
        set
        {
            _selectedWallFamily = value;
            OnPropertyChanged();
            _settings.LastWallFamilyPath = value?.FilePath;
            _settings.Save();
        }
    }

    private VoidFamilyEntry? _selectedBeamFamily;
    public VoidFamilyEntry? SelectedBeamFamily
    {
        get => _selectedBeamFamily;
        set
        {
            _selectedBeamFamily = value;
            OnPropertyChanged();
            _settings.LastBeamFamilyPath = value?.FilePath;
            _settings.Save();
        }
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    public ICommand DetectClashesCommand  { get; }
    public ICommand ApplyCommand          { get; }
    public ICommand SelectAllCommand      { get; }
    public ICommand DeselectAllCommand    { get; }
    public ICommand OpenSettingsCommand   { get; }

    /// <summary>Zooms to and selects the elements of a clash in the active Revit view.</summary>
    public ICommand ShowInRevitCommand    { get; }

    // ── Actions ───────────────────────────────────────────────────────────────

    private void DetectClashes()
    {
        IsBusy = true;
        StatusMessage = "Detecting clashes…";
        ClashResults.Clear();

        try
        {
            _settings.MarginMm = MarginMm;
            _settings.Save();

            double marginFeet = MarginMm / 304.8;

            var results = ClashDetector.Detect(_doc, marginFeet,
                              ScanCableTrays, ScanCableTrayFittings, ScanConduits);

            foreach (var r in results)
                ClashResults.Add(r);

            StatusMessage = ClashResults.Count == 0
                ? "No clashes found."
                : $"{ClashResults.Count} clash(es) found. Select the ones to process.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error during detection: {ex.Message}";
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
            MessageBox.Show("No clashes selected.", "Cable Tray Void Cutter",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        IsBusy = true;
        StatusMessage = "Placing voids…";

        try
        {
            double marginFeet = MarginMm / 304.8;

            FamilySymbol? wallSym = null;
            FamilySymbol? beamSym = null;

            using var tx = new Transaction(_doc, "CableTray Void Cutter – Place Voids");
            tx.Start();

            if (SelectedWallFamily is not null)
                wallSym = VoidPlacer.LoadFamily(_doc, SelectedWallFamily.FilePath);
            if (SelectedBeamFamily is not null)
                beamSym = VoidPlacer.LoadFamily(_doc, SelectedBeamFamily.FilePath);

            var summary = VoidPlacer.PlaceVoids(
                _doc, ClashResults, marginFeet, wallSym, beamSym);

            tx.Commit();

            StatusMessage = "Done.";
            MessageBox.Show(summary, "Cable Tray Void Cutter",
                            MessageBoxButton.OK, MessageBoxImage.Information);

            DetectClashes();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            MessageBox.Show($"An error occurred:\n{ex.Message}",
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
    /// Selects the cable tray and the clashing element in Revit and fits the view to them.
    /// For linked elements only the cable tray (in host) can be selected.
    /// </summary>
    private void ShowInRevit(ClashResult? clash)
    {
        if (clash is null) return;

        try
        {
            var ids = new List<ElementId> { clash.CableTrayId };

            // Only add the clashing element if it's in the host document
            if (clash.Source == ElementSource.Host)
                ids.Add(clash.ClashingElementId);

            _uiDoc.Selection.SetElementIds(ids);

            // Zoom the active view to the selected elements
            var uiView = _uiDoc.GetOpenUIViews()
                .FirstOrDefault(v => v.ViewId == _uiDoc.ActiveView.Id);
            uiView?.ZoomToFit();

            StatusMessage = clash.Source == ElementSource.Linked
                ? $"Selected: {clash.CableTrayName} (linked element cannot be selected)"
                : $"Selected: {clash.DisplayName}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not select elements: {ex.Message}";
        }
    }

    // ── Family list refresh ───────────────────────────────────────────────────

    private void RefreshFamilyLists()
    {
        WallFamilies.Clear();
        BeamFamilies.Clear();

        foreach (var f in _settings.VoidFamilies)
        {
            if (f.TargetType is "Wall" or "Both")  WallFamilies.Add(f);
            if (f.TargetType is "Beam" or "Both")  BeamFamilies.Add(f);
        }
    }

    // ── INotifyPropertyChanged ────────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
