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
    private int _lastClickedIndex = -1;

    public MainWindow(UIDocument uiDoc, AppSettings settings)
    {
        InitializeComponent();
        DataContext = new MainViewModel(uiDoc, settings);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    // ── Ctrl / Shift + click multi-select ─────────────────────────────────────

    /// <summary>
    /// Ctrl+Click → toggle individual row.
    /// Shift+Click → range-select from last clicked.
    /// Plain click is ignored (checkbox handles it).
    /// Propagation of AssignedFamily changes to all selected rows is handled in
    /// <see cref="MainViewModel.OnClashPropertyChanged"/>.
    /// </summary>
    private void ClashGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        bool ctrl  = Keyboard.IsKeyDown(Key.LeftCtrl)  || Keyboard.IsKeyDown(Key.RightCtrl);
        bool shift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
        if (!ctrl && !shift) return;

        var row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row?.DataContext is not ClashResult clash) return;

        var vm = DataContext as MainViewModel;
        if (vm is null) return;

        int idx = vm.ClashResults.IndexOf(clash);
        if (idx < 0) return;

        if (ctrl)
        {
            clash.IsSelected  = !clash.IsSelected;
            _lastClickedIndex = idx;
        }
        else // shift
        {
            int anchor = _lastClickedIndex >= 0 ? _lastClickedIndex : 0;
            int start  = Math.Min(anchor, idx);
            int end    = Math.Max(anchor, idx);
            for (int i = start; i <= end; i++)
                vm.ClashResults[i].IsSelected = true;
        }

        e.Handled = true; // prevent DataGrid from changing row selection highlight
    }

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
