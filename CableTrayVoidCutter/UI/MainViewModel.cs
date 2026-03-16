using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using Autodesk.Revit.DB;
using CableTrayVoidCutter.Core;
using CableTrayVoidCutter.Models;

namespace CableTrayVoidCutter.UI;

/// <summary>
/// ViewModel for <see cref="MainWindow"/>.
/// Drives clash detection, void placement and settings persistence.
/// </summary>
public class MainViewModel : INotifyPropertyChanged
{
    private readonly Document  _doc;
    private readonly AppSettings _settings;

    // ── Constructor ───────────────────────────────────────────────────────────

    public MainViewModel(Document doc, AppSettings settings)
    {
        _doc      = doc;
        _settings = settings;

        MarginMm = settings.MarginMm;

        // Rebuild family lists from settings
        RefreshFamilyLists();

        // Restore last-used families
        SelectedWallFamily = WallFamilies.FirstOrDefault(
            f => f.FilePath == settings.LastWallFamilyPath);
        SelectedBeamFamily = BeamFamilies.FirstOrDefault(
            f => f.FilePath == settings.LastBeamFamilyPath);

        DetectClashesCommand = new RelayCommand(DetectClashes);
        ApplyCommand         = new RelayCommand(Apply,       () => ClashResults.Any(c => c.IsSelected));
        SelectAllCommand     = new RelayCommand(SelectAll,   () => ClashResults.Count > 0);
        DeselectAllCommand   = new RelayCommand(DeselectAll, () => ClashResults.Count > 0);
        OpenSettingsCommand  = new RelayCommand(OpenSettings);
    }

    // ── Observable properties ─────────────────────────────────────────────────

    private double _marginMm;
    public double MarginMm
    {
        get => _marginMm;
        set { _marginMm = value; OnPropertyChanged(); }
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

    public ObservableCollection<ClashResult>    ClashResults    { get; } = [];
    public ObservableCollection<VoidFamilyEntry> WallFamilies   { get; } = [];
    public ObservableCollection<VoidFamilyEntry> BeamFamilies   { get; } = [];

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

    public ICommand DetectClashesCommand { get; }
    public ICommand ApplyCommand         { get; }
    public ICommand SelectAllCommand     { get; }
    public ICommand DeselectAllCommand   { get; }
    public ICommand OpenSettingsCommand  { get; }

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

            var results = ClashDetector.Detect(_doc, marginFeet);

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

            // Load families
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

            // Re-run detection so the list stays up to date
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
