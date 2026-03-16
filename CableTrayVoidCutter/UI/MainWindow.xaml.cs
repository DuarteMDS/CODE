using System.Windows;
using Autodesk.Revit.UI;
using CableTrayVoidCutter.Models;

namespace CableTrayVoidCutter.UI;

/// <summary>Code-behind for <see cref="MainWindow"/>.</summary>
public partial class MainWindow : Window
{
    public MainWindow(UIDocument uiDoc, AppSettings settings)
    {
        InitializeComponent();
        DataContext = new MainViewModel(uiDoc, settings);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) =>
        Close();
}
