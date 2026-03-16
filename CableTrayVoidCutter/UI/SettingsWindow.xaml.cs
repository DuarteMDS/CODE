using System.Windows;
using Microsoft.Win32;
using CableTrayVoidCutter.Models;

namespace CableTrayVoidCutter.UI;

/// <summary>Code-behind for <see cref="SettingsWindow"/>.</summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        // Populate controls
        MarginBox.Text = settings.MarginMm.ToString("F1");

        FamilyGrid.ItemsSource = settings.VoidFamilies;
    }

    // ── Add family ────────────────────────────────────────────────────────────

    private void AddFamily_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title            = "Select Revit Void Family",
            Filter           = "Revit Family (*.rfa)|*.rfa",
            Multiselect      = true,
            CheckFileExists  = true,
        };

        if (dlg.ShowDialog() != true) return;

        foreach (var path in dlg.FileNames)
        {
            // Avoid duplicates
            if (_settings.VoidFamilies.Any(f => f.FilePath == path)) continue;

            var entry = new VoidFamilyEntry
            {
                Name       = System.IO.Path.GetFileNameWithoutExtension(path),
                FilePath   = path,
                TargetType = "Both"
            };

            _settings.VoidFamilies.Add(entry);
        }

        // Refresh the DataGrid
        FamilyGrid.Items.Refresh();
    }

    // ── Remove family ─────────────────────────────────────────────────────────

    private void RemoveFamily_Click(object sender, RoutedEventArgs e)
    {
        if (FamilyGrid.SelectedItem is VoidFamilyEntry selected)
        {
            _settings.VoidFamilies.Remove(selected);
            FamilyGrid.Items.Refresh();
        }
    }

    // ── OK / Cancel ───────────────────────────────────────────────────────────

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        if (double.TryParse(MarginBox.Text, out var margin) && margin >= 0)
            _settings.MarginMm = margin;
        else
        {
            MessageBox.Show("Please enter a valid non-negative number for the margin.",
                            "Invalid input", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
