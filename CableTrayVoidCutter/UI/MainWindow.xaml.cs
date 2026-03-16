using System.Windows;
using Autodesk.Revit.DB;
using CableTrayVoidCutter.Models;

namespace CableTrayVoidCutter.UI;

/// <summary>Code-behind for <see cref="MainWindow"/>.</summary>
public partial class MainWindow : Window
{
    public MainWindow(Document doc, AppSettings settings)
    {
        InitializeComponent();
        DataContext = new MainViewModel(doc, settings);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) =>
        Close();
}
