using System.IO;
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
    private const string PanelName = "Alemi";

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            // Create a compact panel on the Analyze tab.
            RibbonPanel panel = application.CreateRibbonPanel(Tab.Analyze, PanelName);

            string assemblyPath = Assembly.GetExecutingAssembly().Location;
            string assemblyName = Assembly.GetExecutingAssembly().GetName().Name ?? "StructuralTools";

            // Revit stacked buttons display the label to the right of the icon in a compact layout.
            var generateData = new PushButtonData(
                "GenerateWallLoads",
                "Generate",
                assemblyPath,
                "StructuralTools.GenerateWallLoadsCommand"
            );

            var highlightData = new PushButtonData(
                "HighlightProblematicLoads",
                "Highlight",
                assemblyPath,
                "StructuralTools.HighlightProblematicLoadsCommand"
            );

            var repairData = new PushButtonData(
                "RepairIdentifiedLoads",
                "Repair",
                assemblyPath,
                "StructuralTools.RepairIdentifiedLoadsCommand"
            );

            var stackedItems = panel.AddStackedItems(generateData, highlightData, repairData);

            if (stackedItems[0] is PushButton generateBtn)
            {
                generateBtn.ToolTip = "Generate Wall Loads";
                generateBtn.LongDescription = "Generate wall loads on the host beam or floor.";
                generateBtn.Image = LoadPackImage(assemblyName, "Generate16.png");
                generateBtn.LargeImage = LoadPackImage(assemblyName, "Generate16.png");
            }

            if (stackedItems[1] is PushButton highlightBtn)
            {
                highlightBtn.ToolTip = "Highlight Problematic Loads";
                highlightBtn.LongDescription = "Highlight line loads that are incompatible with the analytical model in red.";
                highlightBtn.Image = LoadPackImage(assemblyName, "Generate16.png");
                highlightBtn.LargeImage = LoadPackImage(assemblyName, "Generate16.png");
            }

            if (stackedItems[2] is PushButton repairBtn)
            {
                repairBtn.ToolTip = "Repair Identified Loads";
                repairBtn.LongDescription = "Apply the case-based repair for warning-diagnosed line loads.";
                repairBtn.Image = LoadPackImage(assemblyName, "Generate16.png");
                repairBtn.LargeImage = LoadPackImage(assemblyName, "Generate16.png");
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
    /// Loads an embedded PNG resource directly from the assembly manifest so it works reliably
    /// in Revit add-ins regardless of whether WPF pack URIs resolve in the hosting process.
    /// </summary>
    private static BitmapImage? LoadPackImage(string assemblyName, string resourceName)
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            string[] resourceNames = assembly.GetManifestResourceNames();
            string? fullName = resourceNames.FirstOrDefault(n =>
                n.EndsWith($".{resourceName}", StringComparison.OrdinalIgnoreCase) ||
                n.EndsWith($"Resources.{resourceName}", StringComparison.OrdinalIgnoreCase));

            if (string.IsNullOrEmpty(fullName))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[StructuralTools] Missing embedded resource '{resourceName}' in assembly '{assemblyName}'.");
                return null;
            }

            using var stream = assembly.GetManifestResourceStream(fullName);
            if (stream == null)
                return null;

            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            memory.Position = 0;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = memory;
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
