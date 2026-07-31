using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using Autodesk.Revit.Attributes;

namespace StructuralTools.Commands;

/// <summary>
/// Main command that generates wall loads.
/// Executed when user clicks "Generate" button on the ribbon.
/// </summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public class GenerateWallLoadsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            UIApplication uiApp = commandData.Application;
            
            // Create and run the wall load engine
            var engine = new Engine.WallLoadEngine(uiApp);
            engine.Run();
            
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = $"Error generating wall loads: {ex.Message}\n\n{ex.StackTrace}";
            TaskDialog.Show("Structural Tools - Error", message);
            return Result.Failed;
        }
    }
}

/// <summary>
/// Settings command for wall load generator
/// </summary>
[Transaction(TransactionMode.Manual)]
public class WallLoadSettingsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        TaskDialog.Show("Settings", "Settings dialog coming soon.");
        return Result.Succeeded;
    }
}

/// <summary>
/// Command to convert staircase to analytical model
/// </summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public class StaircaseToAnalyticalCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        TaskDialog.Show("Staircase Tool", "Staircase to analytical model tool coming soon.");
        return Result.Succeeded;
    }
}
