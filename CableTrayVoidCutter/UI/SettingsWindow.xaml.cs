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

        MarginBox.Text    = settings.MarginMm.ToString("F1");
        CirclePathBox.Text = settings.CircleFamilyPath ?? string.Empty;
        RectPathBox.Text   = settings.RectangularFamilyPath ?? string.Empty;
    }

    // ── Browse buttons ────────────────────────────────────────────────────────

    private void BrowseCircle_Click(object sender, RoutedEventArgs e)
    {
        var path = BrowseRfa("Sélectionner la famille circulaire (CEG_Resa Wall Circle)");
        if (path is not null) CirclePathBox.Text = path;
    }

    private void BrowseRect_Click(object sender, RoutedEventArgs e)
    {
        var path = BrowseRfa("Sélectionner la famille rectangulaire (CEG_Resa Wall Rectangular)");
        if (path is not null) RectPathBox.Text = path;
    }

    // ── OK / Cancel ───────────────────────────────────────────────────────────

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(MarginBox.Text, out var margin) || margin < 0)
        {
            MessageBox.Show("Veuillez saisir un nombre positif pour la marge.",
                            "Valeur invalide", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.MarginMm              = margin;
        _settings.CircleFamilyPath      = NullIfEmpty(CirclePathBox.Text);
        _settings.RectangularFamilyPath = NullIfEmpty(RectPathBox.Text);

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string? BrowseRfa(string title)
    {
        var dlg = new OpenFileDialog
        {
            Title           = title,
            Filter          = "Revit Family (*.rfa)|*.rfa",
            CheckFileExists = true
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    private static string? NullIfEmpty(string s) =>
        string.IsNullOrWhiteSpace(s) ? null : s;
}
