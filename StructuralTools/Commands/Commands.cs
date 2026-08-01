using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace StructuralTools.Commands;

/// <summary>
/// Main command that generates wall loads. Bound to the "Generate Wall Loads" ribbon button.
/// Enters Revit's native wall-picking mode (green Modify contextual tab with Finish/Cancel),
/// then host-picking mode, then creates the line loads.
/// </summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public class GenerateWallLoadsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIApplication uiApp = commandData.Application;

        if (uiApp.ActiveUIDocument == null)
        {
            TaskDialog.Show("Structural Tools",
                "Open a Revit document before running this command.");
            return Result.Cancelled;
        }

        try
        {
            var engine = new Engine.WallLoadEngine(uiApp);
            return engine.Run();
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            return Result.Cancelled;
        }
        catch (Exception ex)
        {
            message = $"Error generating wall loads: {ex.Message}\n\n{ex.StackTrace}";
            TaskDialog.Show("Structural Tools - Error", message);
            return Result.Failed;
        }
    }
}
