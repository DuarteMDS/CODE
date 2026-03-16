using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CableTrayVoidCutter.Models;
using CableTrayVoidCutter.UI;

namespace CableTrayVoidCutter.Commands;

/// <summary>
/// Main command: opens the clash-detection / void-placement dialog.
/// Triggered from the ribbon button and the context menu item.
/// </summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public class CreateVoidsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData,
                          ref string message,
                          ElementSet elements)
    {
        var uiApp  = commandData.Application;
        var uiDoc  = uiApp.ActiveUIDocument;
        var doc    = uiDoc.Document;

        try
        {
            // Load persisted settings
            var settings = AppSettings.Load();

            // Show the main WPF window
            var window = new MainWindow(doc, settings);
            window.ShowDialog();

            return Result.Succeeded;
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            return Result.Cancelled;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return Result.Failed;
        }
    }
}
