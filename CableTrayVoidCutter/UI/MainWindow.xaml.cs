using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Autodesk.Revit.UI;
using CableTrayVoidCutter.Models;

namespace CableTrayVoidCutter.UI;

/// <summary>Code-behind for <see cref="MainWindow"/>.</summary>
public partial class MainWindow : Window
{
    // Index of the last row clicked with Ctrl or Shift (for range-select)
    private int _lastClickedIndex = -1;

    public MainWindow(UIDocument uiDoc, AppSettings settings)
    {
        InitializeComponent();
        DataContext = new MainViewModel(uiDoc, settings);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) =>
        Close();

    // ── Ctrl / Shift + click multi-select ─────────────────────────────────────

    /// <summary>
    /// Handles Ctrl+Click (toggle individual row) and Shift+Click (range select)
    /// on the clash DataGrid, updating the IsSelected flag on each ClashResult.
    /// Standard click (no modifier) is ignored — the built-in checkbox handles it.
    /// </summary>
    private void ClashGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        bool ctrl  = Keyboard.IsKeyDown(Key.LeftCtrl)  || Keyboard.IsKeyDown(Key.RightCtrl);
        bool shift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
        if (!ctrl && !shift) return;

        // Walk up the visual tree from the clicked element to find the DataGridRow
        var row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row?.DataContext is not ClashResult clash) return;

        var vm = DataContext as MainViewModel;
        if (vm is null) return;

        int clickedIndex = vm.ClashResults.IndexOf(clash);
        if (clickedIndex < 0) return;

        if (ctrl)
        {
            clash.IsSelected  = !clash.IsSelected;
            _lastClickedIndex = clickedIndex;
        }
        else // shift
        {
            int anchor = _lastClickedIndex >= 0 ? _lastClickedIndex : 0;
            int start  = Math.Min(anchor, clickedIndex);
            int end    = Math.Max(anchor, clickedIndex);

            for (int i = start; i <= end; i++)
                vm.ClashResults[i].IsSelected = true;
        }

        // Prevent the DataGrid from changing its row-selection state
        e.Handled = true;
    }

    // ── Visual tree helper ────────────────────────────────────────────────────

    private static T? FindAncestor<T>(DependencyObject? obj) where T : DependencyObject
    {
        while (obj is not null)
        {
            if (obj is T t) return t;
            obj = VisualTreeHelper.GetParent(obj);
        }
        return null;
    }
}
