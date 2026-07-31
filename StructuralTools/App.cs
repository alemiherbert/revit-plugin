using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using System.Windows.Media.Imaging;
using System.IO;
using System.Reflection;

namespace StructuralTools;

/// <summary>
/// Main application entry point for the Structural Tools add-in.
/// Implements IExternalApplication to integrate with Revit's ribbon interface.
/// </summary>
public class App : IExternalApplication
{
    private const string TabName = "Structural Tools";
    
    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            // Create custom tab
            application.CreateRibbonTab(TabName);
            
            // Get assembly location for loading resources
            string assemblyPath = Assembly.GetExecutingAssembly().Location;
            string resourcePath = Path.GetDirectoryName(assemblyPath) ?? "";
            
            // ============================================
            // Wall Loads Panel
            // ============================================
            RibbonPanel wallLoadsPanel = application.CreateRibbonPanel(TabName, "Wall Loads");
            
            // Generate Wall Loads button
            PushButton generateBtn = wallLoadsPanel.AddItem(
                new PushButtonData(
                    "GenerateWallLoads",
                    "Generate",
                    assemblyPath,
                    "StructuralTools.Commands.GenerateWallLoadsCommand"
                )
            ) as PushButton;
            
            if (generateBtn != null)
            {
                generateBtn.ToolTip = "Generate line loads from walls";
                generateBtn.LongDescription = "Scans selected walls and creates analytical line loads on supporting beams or floors based on material density and wall geometry.";
                
                string icon32Path = Path.Combine(resourcePath, "Resources", "Generate32.png");
                string icon16Path = Path.Combine(resourcePath, "Resources", "Generate16.png");
                
                if (File.Exists(icon32Path))
                {
                    var largeImage = new BitmapImage(new Uri(icon32Path));
                    generateBtn.LargeImage = largeImage;
                }
                if (File.Exists(icon16Path))
                    generateBtn.Image = new BitmapImage(new Uri(icon16Path));
            }
            
            // Settings button
            PushButton settingsBtn = wallLoadsPanel.AddItem(
                new PushButtonData(
                    "WallLoadSettings",
                    "Settings",
                    assemblyPath,
                    "StructuralTools.Commands.WallLoadSettingsCommand"
                )
            ) as PushButton;
            
            if (settingsBtn != null)
            {
                settingsBtn.ToolTip = "Configure wall load generation settings";
                string iconPath = Path.Combine(resourcePath, "Resources", "Logo.png");
                if (File.Exists(iconPath))
                    settingsBtn.Image = new BitmapImage(new Uri(iconPath));
            }
            
            // Add separator
            wallLoadsPanel.AddSeparator();
            
            // ============================================
            // Staircase Panel
            // ============================================
            RibbonPanel staircasePanel = application.CreateRibbonPanel(TabName, "Staircase");
            
            // Staircase to Analytical Model button
            PushButton staircaseBtn = staircasePanel.AddItem(
                new PushButtonData(
                    "StaircaseToAnalytical",
                    "To Analytical",
                    assemblyPath,
                    "StructuralTools.Commands.StaircaseToAnalyticalCommand"
                )
            ) as PushButton;
            
            if (staircaseBtn != null)
            {
                staircaseBtn.ToolTip = "Convert staircase to analytical model";
                staircaseBtn.LongDescription = "Converts selected staircase elements into analytical model components for structural analysis.";
                
                string icon32Path = Path.Combine(resourcePath, "Resources", "Staircase32.png");
                string icon16Path = Path.Combine(resourcePath, "Resources", "Staircase16.png");
                
                if (File.Exists(icon32Path))
                {
                    var largeImage = new BitmapImage(new Uri(icon32Path));
                    staircaseBtn.LargeImage = largeImage;
                }
                if (File.Exists(icon16Path))
                    staircaseBtn.Image = new BitmapImage(new Uri(icon16Path));
            }
            
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            TaskDialog.Show("Structural Tools - Error", 
                $"Failed to initialize add-in:\n{ex.Message}");
            return Result.Failed;
        }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        return Result.Succeeded;
    }
}
