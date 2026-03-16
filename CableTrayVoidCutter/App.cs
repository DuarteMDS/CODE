using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using CableTrayVoidCutter.Commands;
using System.Reflection;
using System.Windows.Media.Imaging;

namespace CableTrayVoidCutter;

/// <summary>
/// Revit External Application – builds the ribbon tab and panel.
/// </summary>
[Autodesk.Revit.Attributes.Regeneration(Autodesk.Revit.Attributes.RegenerationOption.Manual)]
public class App : IExternalApplication
{
    internal static readonly string AssemblyPath =
        Assembly.GetExecutingAssembly().Location;

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            CreateRibbonUI(application);
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            TaskDialog.Show("CableTray Void Cutter", $"Startup error:\n{ex.Message}");
            return Result.Failed;
        }
    }

    public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;

    // -------------------------------------------------------------------------
    // Ribbon
    // -------------------------------------------------------------------------
    private static void CreateRibbonUI(UIControlledApplication app)
    {
        const string tabName   = "Gamaco";
        const string panelName = "Void Cutter";

        // Create tab (ignore if already exists)
        try { app.CreateRibbonTab(tabName); }
        catch { /* tab already exists */ }

        var panel = app.CreateRibbonPanel(tabName, panelName);

        // ── Main button: Create Voids ────────────────────────────────────────
        var createVoidsData = new PushButtonData(
            name:        "CreateVoids",
            text:        "Create\nVoids",
            assemblyName: AssemblyPath,
            className:   typeof(CreateVoidsCommand).FullName!)
        {
            ToolTip = "Detect collisions between Cable Trays and Walls/Beams " +
                      "(host + linked models) and place void families.",
            LongDescription =
                "Scans the active document and all linked models for Cable Tray " +
                "elements that collide with Walls or Structural Beams, then " +
                "creates parametric void families at each intersection.",
            LargeImage = LoadImage("create_voids_32.png"),
            Image      = LoadImage("create_voids_16.png"),
        };

        var createVoidsBtn = (PushButton)panel.AddItem(createVoidsData);
        createVoidsBtn.AvailabilityClassName = typeof(CommandAvailability).FullName;

        panel.AddSeparator();

        // ── Settings button ──────────────────────────────────────────────────
        var settingsData = new PushButtonData(
            name:        "VoidCutterSettings",
            text:        "Settings",
            assemblyName: AssemblyPath,
            className:   typeof(SettingsCommand).FullName!)
        {
            ToolTip     = "Configure void families and default margin.",
            LargeImage  = LoadImage("settings_32.png"),
            Image       = LoadImage("settings_16.png"),
        };

        panel.AddItem(settingsData);
    }

    /// <summary>
    /// Tries to load an embedded PNG image; returns null if not found
    /// (Revit accepts null without crashing).
    /// </summary>
    private static BitmapImage? LoadImage(string resourceName)
    {
        try
        {
            var asm    = Assembly.GetExecutingAssembly();
            var stream = asm.GetManifestResourceStream(
                $"CableTrayVoidCutter.Resources.{resourceName}");

            if (stream is null) return null;

            var img = new BitmapImage();
            img.BeginInit();
            img.StreamSource  = stream;
            img.CacheOption   = BitmapCacheOption.OnLoad;
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Availability: button is active only when a project document is open.
// ─────────────────────────────────────────────────────────────────────────────
public class CommandAvailability : IExternalCommandAvailability
{
    public bool IsCommandAvailable(UIApplication app, CategorySet selectedCategories)
        => app.ActiveUIDocument?.Document is { IsFamilyDocument: false };
}
