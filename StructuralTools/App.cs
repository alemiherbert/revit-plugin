using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace StructuralTools;

/// <summary>
/// Main application entry point for the Structural Tools add-in.
/// Implements <see cref="IExternalApplication"/> to integrate with Revit's ribbon interface.
/// Adds a panel called "Alemi's Tools" to Revit's built-in Analyze tab.
/// </summary>
public class App : IExternalApplication
{
    private const string PanelName = "Alemi's Tools";

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            // Create a panel on Revit's built-in Analyze tab.
            RibbonPanel panel = application.CreateRibbonPanel(Tab.Analyze, PanelName);

            string assemblyPath = Assembly.GetExecutingAssembly().Location;
            string assemblyName = Assembly.GetExecutingAssembly().GetName().Name ?? "StructuralTools";

            // --- Generate Wall Loads button ---
            var generateBtn = panel.AddItem(
                new PushButtonData(
                    "GenerateWallLoads",
                    "Generate\nWall Loads",
                    assemblyPath,
                    "StructuralTools.GenerateWallLoadsCommand"
                )
            ) as PushButton;

            if (generateBtn != null)
            {
                generateBtn.ToolTip = "Pick walls and generate line loads on a host beam or floor";
                generateBtn.LongDescription =
                    "Click to enter Revit's native wall-selection mode (only Wall elements are clickable). " +
                    "Press Finish (✓) when done, then pick the host beam or floor. " +
                    "Line loads are created in the current load case.";
                generateBtn.LargeImage = LoadPackImage(assemblyName, "Generate32.png");
                generateBtn.Image      = LoadPackImage(assemblyName, "Generate16.png");
            }

            panel.AddSeparator();

            // --- Staircase to Analytical (stub) ---
            var staircaseBtn = panel.AddItem(
                new PushButtonData(
                    "StaircaseToAnalytical",
                    "Staircase\nTo Analytical",
                    assemblyPath,
                    "StructuralTools.StaircaseToAnalyticalCommand"
                )
            ) as PushButton;

            if (staircaseBtn != null)
            {
                staircaseBtn.ToolTip = "Convert staircases to analytical panels";
                staircaseBtn.LongDescription =
                    "Pick stairs (any type — straight, L-shaped, U-shaped/dogleg, Z-shaped, spiral, winder, " +
                    "split, three-quarter turn). Each run becomes one slanted analytical panel; each landing " +
                    "becomes one flat analytical panel. Concrete-stair idealisation — no analytical members. " +
                    "The original stair geometry is preserved.";

                staircaseBtn.LargeImage = LoadPackImage(assemblyName, "Staircase32.png");
                staircaseBtn.Image      = LoadPackImage(assemblyName, "Staircase16.png");
            }

            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            TaskDialog.Show("Structural Tools - Error",
                $"Failed to initialize add-in:\n{ex.Message}\n\n{ex.StackTrace}");
            return Result.Failed;
        }
    }

    public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;

    /// <summary>
    /// Loads an embedded PNG resource via Pack URI. Returns null if the resource is missing
    /// or cannot be decoded — never throws, so the ribbon still loads without icons.
    /// </summary>
    private static BitmapImage? LoadPackImage(string assemblyName, string resourceName)
    {
        try
        {
            var uri = new Uri(
                $"pack://application:,,,/{assemblyName};component/Resources/{resourceName}",
                UriKind.Absolute);

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption  = BitmapCacheOption.OnLoad;
            bmp.UriSource    = uri;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[StructuralTools] Failed to load icon '{resourceName}': {ex.Message}");
            return null;
        }
    }
}
