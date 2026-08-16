using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Autodesk.Windows;
using RibbonButton = Autodesk.Windows.RibbonButton;
using RibbonPanel = Autodesk.Windows.RibbonPanel;
using RibbonPanelSource = Autodesk.Windows.RibbonPanelSource;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace StructuralTools;

public sealed class ModifyTabManager
{
    private readonly UIControlledApplication _application;
    private UIApplication? _uiApp;
    private readonly RibbonButton _generateButton;
    private readonly RibbonButton _repairButton;
    private RibbonPanel? _wallPanel;
    private RibbonPanel? _repairPanel;

    public ModifyTabManager(UIControlledApplication application)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));

        _generateButton = new RibbonButton
        {
            Name = "StructuralTools.GenerateLineLoad",
            Id = "StructuralTools.GenerateLineLoad",
            Text = "Generate\nLine Load",
            ToolTip = "Generate a line load for the selected wall.",
            IsVisible = true,
            IsEnabled = false,
            ShowText = true,
            ShowImage = true,
            ShowToolTipOnDisabled = true,
            Size = RibbonItemSize.Large,
            Orientation = System.Windows.Controls.Orientation.Vertical
        };

        _repairButton = new RibbonButton
        {
            Name = "StructuralTools.RepairLineLoad",
            Id = "StructuralTools.RepairLineLoad",
            Text = "Repair",
            ToolTip = "Repair the selected problematic wall load.",
            IsVisible = true,
            IsEnabled = false,
            ShowText = true,
            ShowImage = true,
            ShowToolTipOnDisabled = true,
            Size = RibbonItemSize.Large,
            Orientation = System.Windows.Controls.Orientation.Vertical
        };

        _generateButton.Image = LoadPackImage("Generate32.png");
        _generateButton.LargeImage = LoadPackImage("Generate32.png");
        _repairButton.Image = LoadPackImage("Generate32.png");
        _repairButton.LargeImage = LoadPackImage("Generate32.png");
    }

    public void Install()
    {
        var ribbon = ComponentManager.Ribbon;
        if (ribbon == null)
            return;

        var tab = ribbon.Tabs.FirstOrDefault(t => t.Id == "Modify");
        if (tab == null)
            return;

        _wallPanel = new RibbonPanel
        {
            IsVisible = false,
            FloatingOrientation = System.Windows.Controls.Orientation.Vertical
        };
        var wallSource = new RibbonPanelSource
        {
            Name = "StructuralToolsModifyWall",
            Id = "StructuralToolsModifyWall",
            Title = "Wall Load"
        };
        _wallPanel.Source = wallSource;
        wallSource.Items.Add(_generateButton);

        _repairPanel = new RibbonPanel
        {
            IsVisible = false,
            FloatingOrientation = System.Windows.Controls.Orientation.Vertical
        };
        var repairSource = new RibbonPanelSource
        {
            Name = "StructuralToolsModifyRepair",
            Id = "StructuralToolsModifyRepair",
            Title = "Line Load Repair"
        };
        _repairPanel.Source = repairSource;
        repairSource.Items.Add(_repairButton);

        tab.Panels.Add(_wallPanel);
        tab.Panels.Add(_repairPanel);

        _wallPanel.IsVisible = true;
        _repairPanel.IsVisible = true;

        _application.Idling += OnIdling;

        _generateButton.CommandHandler = new RelayCommandHandler(() =>
        {
            try
            {
                var uidoc = _uiApp?.ActiveUIDocument;
                if (uidoc == null)
                {
                    TaskDialog.Show("Structural Tools", "No active document.");
                    return;
                }

                var selectedIds = uidoc.Selection.GetElementIds();
                if (selectedIds.Count == 0)
                {
                    TaskDialog.Show("Structural Tools", "Select one or more walls.");
                    return;
                }

                var result = App.GenerateSelectedWallLoads(_uiApp!, selectedIds, out var message);
                if (!string.IsNullOrEmpty(message))
                    TaskDialog.Show("Structural Tools", message);
                if (result == Result.Succeeded)
                    UpdateButtonState();
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Structural Tools - Error", 
                    $"Generation failed with exception:\n{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}");
            }
        });

        _repairButton.CommandHandler = new RelayCommandHandler(() =>
        {
            try
            {
                var uidoc = _uiApp?.ActiveUIDocument;
                if (uidoc == null)
                {
                    TaskDialog.Show("Structural Tools", "No active document.");
                    return;
                }

                var selectedIds = uidoc.Selection.GetElementIds();
                if (selectedIds.Count == 0)
                {
                    TaskDialog.Show("Structural Tools", "Select one or more line loads to repair.");
                    return;
                }

                var result = App.RepairSelectedProblematicLineLoads(_uiApp!, selectedIds, out var message);
                if (!string.IsNullOrEmpty(message))
                    TaskDialog.Show("Structural Tools", message);
                if (result == Result.Succeeded)
                    UpdateButtonState();
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Structural Tools - Error", 
                    $"Repair failed with exception:\n{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}");
            }
        });

        UpdateButtonState();
    }

    public void Shutdown()
    {
        _application.Idling -= OnIdling;
    }

    private void OnIdling(object? sender, IdlingEventArgs e)
    {
        if (sender is UIApplication uiApp)
            _uiApp = uiApp;

        UpdateButtonState();
    }

    private void UpdateButtonState()
    {
        try
        {
            var uidoc = _uiApp?.ActiveUIDocument;
            if (uidoc == null || _wallPanel == null || _repairPanel == null)
            {
                _wallPanel!.IsVisible = false;
                _repairPanel!.IsVisible = false;
                _generateButton.IsEnabled = false;
                _repairButton.IsEnabled = false;
                return;
            }

            var selectedIds = uidoc.Selection.GetElementIds();
            if (selectedIds.Count == 0)
            {
                _wallPanel.IsVisible = false;
                _repairPanel.IsVisible = false;
                _generateButton.IsEnabled = false;
                _repairButton.IsEnabled = false;
                return;
            }

            var selectedElements = selectedIds
                .Select(id => uidoc.Document.GetElement(id))
                .Where(e => e != null && e.IsValidObject)
                .ToList();

            var walls = selectedElements.OfType<Wall>().ToList();
            var lineLoads = selectedElements.OfType<LineLoad>().ToList();

            // Wall panel visible only when walls are selected
            _wallPanel.IsVisible = walls.Count > 0;
            
            // Repair panel visible only when line loads are selected
            _repairPanel.IsVisible = lineLoads.Count > 0;

            // ====== WALL PANEL: Enable only for valid walls with analytical association ======
            if (walls.Count > 0)
            {
                bool hasValidWall = false;
                try
                {
                    // Check if at least one selected wall has an analytical association
                    hasValidWall = walls.Any(w => 
                    {
                        try 
                        { 
                            return WallLoadEngine.CanGenerateLineLoadForWall(w); 
                        }
                        catch (Exception ex)
                        { 
                            System.Diagnostics.Debug.WriteLine($"[StructuralTools] Wall validation error for {w.Id}: {ex.Message}");
                            return false; 
                        }
                    });
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[StructuralTools] Wall panel enable check failed: {ex.Message}");
                    hasValidWall = false;
                }
                
                _generateButton.IsEnabled = hasValidWall;
            }
            else
            {
                _generateButton.IsEnabled = false;
            }

            // ====== REPAIR PANEL: Enable only for diagnosed problematic line loads ======
            if (lineLoads.Count > 0)
            {
                bool hasProblematicLoad = false;
                try
                {
                    var problemMap = RevitLoadUtils.GetPreviouslyIdentifiedProblemLoads(uidoc.Document);
                    if (problemMap != null && problemMap.Count > 0)
                    {
                        hasProblematicLoad = lineLoads.Any(load => problemMap.ContainsKey(load.Id));
                        
                        if (!hasProblematicLoad)
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"[StructuralTools] Line loads selected but none in problem map. " +
                                $"Map has {problemMap.Count} diagnosed loads, selection has {lineLoads.Count} loads.");
                        }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("[StructuralTools] No diagnosed loads found. Run Highlight first.");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[StructuralTools] Repair panel enable check failed: {ex.Message}");
                    hasProblematicLoad = false;
                }
                
                _repairButton.IsEnabled = hasProblematicLoad;
            }
            else
            {
                _repairButton.IsEnabled = false;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StructuralTools] UpdateButtonState failed: {ex.Message}\n{ex.StackTrace}");
            _generateButton.IsEnabled = false;
            _repairButton.IsEnabled = false;
        }
    }

    private sealed class RelayCommandHandler : ICommand
    {
        private readonly Action _execute;

        public RelayCommandHandler(Action execute)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        }

        public bool CanExecute(object? parameter) => true;

        public event EventHandler? CanExecuteChanged;

        public void Execute(object? parameter)
        {
            _execute();
        }
    }

    private static BitmapImage? LoadPackImage(string resourceName)
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
            System.Diagnostics.Debug.WriteLine($"[StructuralTools] Failed to load icon '{resourceName}': {ex.Message}");
            return null;
        }
    }
}
