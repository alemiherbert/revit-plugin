using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using Autodesk.Revit.Attributes;
using System.Windows;

namespace WallLoadGenerator;

/// <summary>
/// Main command that generates wall loads.
/// Executed when user clicks "Generate Loads" button on the ribbon.
/// </summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public class GenerateWallLoads : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            UIApplication uiApp = commandData.Application;
            UIDocument uiDoc = uiApp.ActiveUIDocument;
            Document doc = uiDoc.Document;

            // Show main window
            var mainWindow = new MainWindow(uiApp, doc);
            mainWindow.Owner = System.Windows.Application.Current.MainWindow;
            
            if (mainWindow.ShowDialog() == true)
            {
                // User clicked Generate
                return Result.Succeeded;
            }
            
            return Result.Cancelled;
        }
        catch (Exception ex)
        {
            message = $"Error generating wall loads: {ex.Message}\n\n{ex.StackTrace}";
            LoggingService.Error(message);
            TaskDialog.Show("Wall Load Generator - Error", message);
            return Result.Failed;
        }
    }
}

/// <summary>
/// Placeholder settings command
/// </summary>
[Transaction(TransactionMode.Manual)]
public class SettingsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var settingsWindow = new SettingsWindow();
        settingsWindow.Owner = System.Windows.Application.Current.MainWindow;
        settingsWindow.ShowDialog();
        return Result.Succeeded;
    }
}

/// <summary>
/// Placeholder about command
/// </summary>
[Transaction(TransactionMode.ReadOnly)]
public class AboutCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var aboutWindow = new AboutWindow();
        aboutWindow.Owner = System.Windows.Application.Current.MainWindow;
        aboutWindow.ShowDialog();
        return Result.Succeeded;
    }
}
