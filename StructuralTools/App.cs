using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Autodesk.Windows;
using RevitRibbonPanel = Autodesk.Revit.UI.RibbonPanel;
using RevitTaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace StructuralTools;

/// <summary>
/// Simple add-in entry point. The Analyze tab keeps the model-wide actions, and the
/// Modify tab hosts a compact custom panel with one wall button and one line-load button.
/// </summary>
public class App : IExternalApplication
{
    private const string PanelName = "Alemi";
    private ModifyTabManager? _modifyTabManager;

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            RevitRibbonPanel panel = application.CreateRibbonPanel(Tab.Analyze, PanelName);

            string assemblyPath = Assembly.GetExecutingAssembly().Location;
            string assemblyName = Assembly.GetExecutingAssembly().GetName().Name ?? "StructuralTools";

            var generateData = new PushButtonData(
                "GenerateWallLoads",
                "Generate Wall Loads",
                assemblyPath,
                "StructuralTools.GenerateWallLoadsCommand");

            var highlightData = new PushButtonData(
                "HighlightProblematicLoads",
                "Highlight Wall Loads",
                assemblyPath,
                "StructuralTools.HighlightProblematicLoadsCommand");

            var stackedItems = panel.AddStackedItems(generateData, highlightData);

            if (stackedItems[0] is PushButton generateBtn)
            {
                generateBtn.ToolTip = "Generate Wall Loads";
                generateBtn.LongDescription = "Generate wall loads on the host beam or floor.";
                generateBtn.Image = LoadPackImage(assemblyName, "Generate32.png");
                generateBtn.LargeImage = LoadPackImage(assemblyName, "Generate32.png");
            }

            if (stackedItems[1] is PushButton highlightBtn)
            {
                highlightBtn.ToolTip = "Highlight Problematic Loads";
                highlightBtn.LongDescription = "Highlight line loads that are incompatible with the analytical model in red.";
                highlightBtn.Image = LoadPackImage(assemblyName, "Generate32.png");
                highlightBtn.LargeImage = LoadPackImage(assemblyName, "Generate32.png");
            }

            _modifyTabManager = new ModifyTabManager(application);
            _modifyTabManager.Install();
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            RevitTaskDialog.Show("Structural Tools - Error",
                $"Failed to initialize add-in:\n{ex.Message}\n\n{ex.StackTrace}");
            return Result.Failed;
        }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        _modifyTabManager?.Shutdown();
        return Result.Succeeded;
    }

    public static Result GenerateSelectedWallLoads(UIApplication uiApp, ICollection<ElementId> selectedIds, out string message)
    {
        message = string.Empty;

        if (uiApp.ActiveUIDocument == null)
        {
            message = "Open a Revit document before generating line loads.";
            return Result.Cancelled;
        }

        var walls = selectedIds
            .Select(id => uiApp.ActiveUIDocument.Document.GetElement(id))
            .OfType<Wall>()
            .Where(WallLoadEngine.CanGenerateLineLoadForWall)
            .ToList();

        if (walls.Count == 0)
        {
            message = "Select one or more valid walls that sit on a beam or slab with an analytical association.";
            return Result.Cancelled;
        }

        var engine = new WallLoadEngine(uiApp);
        return engine.GenerateForSelectedWalls(walls);
    }

    public static Result RepairSelectedProblematicLineLoads(UIApplication uiApp, ICollection<ElementId> selectedIds, out string message)
    {
        message = string.Empty;

        if (uiApp.ActiveUIDocument == null)
        {
            message = "Open a Revit document before repairing line loads.";
            return Result.Cancelled;
        }

        var doc = uiApp.ActiveUIDocument.Document;
        var selectedLoads = selectedIds
            .Select(id => doc.GetElement(id))
            .OfType<LineLoad>()
            .ToList();

        if (selectedLoads.Count == 0)
        {
            message = "Select one or more problematic line loads to repair.";
            return Result.Cancelled;
        }

        var problemMap = RevitLoadUtils.GetPreviouslyIdentifiedProblemLoads(doc);
        var filtered = selectedLoads
            .Where(load => problemMap.ContainsKey(load.Id))
            .ToList();

        if (filtered.Count == 0)
        {
            message = "The selected line loads do not have any previously identified problems. Run Highlight Problematic Loads first.";
            return Result.Cancelled;
        }

        List<RepairOutcome> repaired, failed, flagged;
        using (var tx = new Transaction(doc, "Repair Selected Line Loads"))
        {
            tx.Start();
            (repaired, failed, flagged) = RepairEngine.RepairIdentifiedLoads(doc, filtered, problemMap);
            tx.Commit();
        }

        string detail = failed.Count == 0 && flagged.Count == 0
            ? "No validation failures were reported."
            : string.Join("\n",
                failed.Select(f => $"FAILED: {f.OriginalLoadId} — {f.Reason}").Concat(
                flagged.Select(f => $"FLAGGED: {f.OriginalLoadId} — {f.Reason}")).Take(10));

        string summary = $"Repaired: {repaired.Count}\n" +
            $"Flagged for manual review: {flagged.Count}\n" +
            $"Failed: {failed.Count}\n\n" +
            "Selected problem loads were repaired within the active transaction.";

        if (!string.IsNullOrWhiteSpace(detail))
            summary += $"\n\nDetails:\n{detail}";

        RevitTaskDialog.Show("Repair Selected Line Loads", summary);
        return Result.Succeeded;
    }

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
                return null;

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

