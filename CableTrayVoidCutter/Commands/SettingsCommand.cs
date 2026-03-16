using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CableTrayVoidCutter.Models;
using CableTrayVoidCutter.UI;

namespace CableTrayVoidCutter.Commands;

/// <summary>
/// Opens the Settings dialog directly (also accessible from the ribbon).
/// </summary>
[Transaction(TransactionMode.ReadOnly)]
[Regeneration(RegenerationOption.Manual)]
public class SettingsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData,
                          ref string message,
                          ElementSet elements)
    {
        try
        {
            var settings = AppSettings.Load();
            var win      = new SettingsWindow(settings);
            if (win.ShowDialog() == true)
                settings.Save();

            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return Result.Failed;
        }
    }
}
