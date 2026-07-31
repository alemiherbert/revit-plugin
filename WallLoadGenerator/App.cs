using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using System.Windows.Media.Imaging;
using System.IO;
using System.Reflection;

namespace WallLoadGenerator;

/// <summary>
/// Main application entry point for the Wall Load Generator add-in.
/// Implements IExternalApplication to integrate with Revit's ribbon interface.
/// </summary>
public class App : IExternalApplication
{
    private const string TabName = "Wall Tools";
    private const string PanelName = "Generate Loads";
    
    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            // Create custom tab
            application.CreateRibbonTab(TabName);
            
            // Create panel on the custom tab
            RibbonPanel panel = application.CreateRibbonPanel(TabName, PanelName);
            
            // Get assembly location for loading resources
            string assemblyPath = Assembly.GetExecutingAssembly().Location;
            string resourcePath = Path.GetDirectoryName(assemblyPath) ?? "";
            
            // Create Generate Loads button
            PushButton generateBtn = panel.AddItem(
                new PushButtonData(
                    "GenerateWallLoads",
                    "Generate\nLoads",
                    assemblyPath,
                    "WallLoadGenerator.GenerateWallLoads"
                )
            ) as PushButton;
            
            if (generateBtn != null)
            {
                generateBtn.ToolTip = "Generate line loads from walls to floors";
                generateBtn.LongDescription = "Scans selected walls and creates analytical line loads on supporting floors based on material density and wall geometry.";
                
                // Set icons if available
                string icon32Path = Path.Combine(resourcePath, "Resources", "Generate32.png");
                string icon16Path = Path.Combine(resourcePath, "Resources", "Generate16.png");
                
                if (File.Exists(icon32Path))
                    generateBtn.SetLargeImage(new BitmapImage(new Uri(icon32Path)));
                if (File.Exists(icon16Path))
                    generateBtn.Image = new BitmapImage(new Uri(icon16Path));
            }
            
            // Create Settings button
            PushButton settingsBtn = panel.AddItem(
                new PushButtonData(
                    "Settings",
                    "Settings",
                    assemblyPath,
                    "WallLoadGenerator.SettingsCommand"
                )
            ) as PushButton;
            
            if (settingsBtn != null)
            {
                settingsBtn.ToolTip = "Configure wall load generation settings";
                string iconPath = Path.Combine(resourcePath, "Resources", "Logo.png");
                if (File.Exists(iconPath))
                    settingsBtn.Image = new BitmapImage(new Uri(iconPath));
            }
            
            // Create About button
            PushButton aboutBtn = panel.AddItem(
                new PushButtonData(
                    "About",
                    "About",
                    assemblyPath,
                    "WallLoadGenerator.AboutCommand"
                )
            ) as PushButton;
            
            if (aboutBtn != null)
            {
                aboutBtn.ToolTip = "About Wall Load Generator";
            }
            
            // Register logging service
            LoggingService.Initialize();
            LoggingService.Info("Wall Load Generator started successfully");
            
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            LoggingService.Error($"Failed to start Wall Load Generator: {ex.Message}");
            TaskDialog.Show("Wall Load Generator - Error", 
                $"Failed to initialize add-in:\n{ex.Message}");
            return Result.Failed;
        }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        LoggingService.Info("Wall Load Generator shutting down");
        LoggingService.Dispose();
        return Result.Succeeded;
    }
}
