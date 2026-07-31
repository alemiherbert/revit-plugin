using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using Autodesk.Revit.Attributes;
using System.Windows;

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
            UIDocument uiDoc = uiApp.ActiveUIDocument;
            Document doc = uiDoc.Document;

            // Show main window for wall load generation
            var mainWindow = new UI.WallLoadGeneratorWindow(uiApp, doc);
            mainWindow.Owner = Application.Current.MainWindow;
            
            if (mainWindow.ShowDialog() == true)
            {
                return Result.Succeeded;
            }
            
            return Result.Cancelled;
        }
        catch (Exception ex)
        {
            message = $"Error generating wall loads: {ex.Message}\n\n{ex.StackTrace}";
            Services.LoggingService.Error(message);
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
        var settingsWindow = new UI.WallLoadSettingsWindow();
        settingsWindow.Owner = Application.Current.MainWindow;
        settingsWindow.ShowDialog();
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
        try
        {
            UIApplication uiApp = commandData.Application;
            UIDocument uiDoc = uiApp.ActiveUIDocument;
            Document doc = uiDoc.Document;

            // Show main window for staircase conversion
            var staircaseWindow = new UI.StaircaseToAnalyticalWindow(uiApp, doc);
            staircaseWindow.Owner = Application.Current.MainWindow;
            
            if (staircaseWindow.ShowDialog() == true)
            {
                return Result.Succeeded;
            }
            
            return Result.Cancelled;
        }
        catch (Exception ex)
        {
            message = $"Error converting staircase: {ex.Message}\n\n{ex.StackTrace}";
            Services.LoggingService.Error(message);
            TaskDialog.Show("Structural Tools - Error", message);
            return Result.Failed;
        }
    }
}
