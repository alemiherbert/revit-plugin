using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace StructuralTools.UI;

/// <summary>
/// Main window for the Wall Load Generator.
/// Provides a compact, native Revit-like interface.
/// </summary>
public partial class WallLoadGeneratorWindow : Window
{
    private readonly UIApplication _uiApp;
    private readonly Document _doc;
    private Models.WallLoadSettings _settings = new();

    public WallLoadGeneratorWindow(UIApplication uiApp, Document doc)
    {
        _uiApp = uiApp;
        _doc = doc;
        
        InitializeComponent();
        InitializeUI();
        LoadSettings();
    }

    private void InitializeComponent()
    {
        // Window properties
        Title = "Wall to Line Load Generator";
        Width = 420;
        Height = 580;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        
        // Set font to match Revit
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        FontSize = 12;
        
        BuildUI();
    }

    private void BuildUI()
    {
        var mainGrid = new Grid();
        mainGrid.Margin = new Thickness(12);
        
        var rowDefs = new RowDefinitionCollection
        {
            new RowDefinition { Height = GridLength.Auto },   // Load Case
            new RowDefinition { Height = GridLength.Auto },   // Wall Selection
            new RowDefinition { Height = GridLength.Auto },   // Load Options
            new RowDefinition { Height = GridLength.Auto },   // Density
            new RowDefinition { Height = GridLength.Auto },   // Tolerance
            new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }, // Spacer
            new RowDefinition { Height = GridLength.Auto },   // Buttons
        };
        mainGrid.RowDefinitions = rowDefs;

        int row = 0;

        // Load Case dropdown
        mainGrid.Children.Add(CreateLabel("Load Case", row));
        row++;
        
        _loadCaseCombo = new ComboBox
        {
            Width = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 12)
        };
        _loadCaseCombo.Items.Add("Dead Load");
        _loadCaseCombo.Items.Add("Super Dead Load");
        _loadCaseCombo.Items.Add("Live Load");
        _loadCaseCombo.Items.Add("Partition Load");
        _loadCaseCombo.Items.Add("Wind Load");
        _loadCaseCombo.Items.Add("Seismic Load");
        _loadCaseCombo.SelectedIndex = 0;
        mainGrid.Children.Add(_loadCaseCombo);
        Grid.SetRow(_loadCaseCombo, row);
        Grid.SetColumn(_loadCaseCombo, 0);

        // Wall Selection group
        row++;
        mainGrid.Children.Add(CreateLabel("Wall Selection", row));
        row++;
        
        var wallStack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        _chkStructural = new CheckBox { Content = "Structural", IsChecked = true, Margin = new Thickness(0, 0, 16, 0) };
        _chkArchitectural = new CheckBox { Content = "Architectural", IsChecked = true, Margin = new Thickness(0, 0, 16, 0) };
        _chkLinked = new CheckBox { Content = "Linked Models" };
        wallStack.Children.Add(_chkStructural);
        wallStack.Children.Add(_chkArchitectural);
        wallStack.Children.Add(_chkLinked);
        mainGrid.Children.Add(wallStack);
        Grid.SetRow(wallStack, row);

        // Load Options group
        row++;
        mainGrid.Children.Add(CreateLabel("Load Options", row));
        row++;
        
        var optionsStack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        _chkMerge = new CheckBox { Content = "Merge coincident loads", IsChecked = true, Margin = new Thickness(0, 0, 16, 0) };
        _chkIgnoreCurtain = new CheckBox { Content = "Ignore curtain walls", IsChecked = true, Margin = new Thickness(0, 0, 16, 0) };
        _chkIgnoreDemolished = new CheckBox { Content = "Ignore demolished" };
        optionsStack.Children.Add(_chkMerge);
        optionsStack.Children.Add(_chkIgnoreCurtain);
        optionsStack.Children.Add(_chkIgnoreDemolished);
        mainGrid.Children.Add(optionsStack);
        Grid.SetRow(optionsStack, row);

        // Density section
        row++;
        mainGrid.Children.Add(CreateLabel("Density", row));
        row++;
        
        var densityStack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        _radioMaterialDensity = new RadioButton 
        { 
            Content = "Material density", 
            IsChecked = true, 
            Margin = new Thickness(0, 0, 16, 0),
            GroupName = "DensityGroup"
        };
        _radioOverride = new RadioButton 
        { 
            Content = "Override:", 
            GroupName = "DensityGroup" 
        };
        _txtDensity = new TextBox 
        { 
            Width = 60, 
            Text = "24.0", 
            Margin = new Thickness(8, 0, 4, 0),
            IsEnabled = false
        };
        var lblDensityUnit = new TextBlock { Text = "kN/m³", VerticalAlignment = VerticalAlignment.Center };
        
        densityStack.Children.Add(_radioMaterialDensity);
        densityStack.Children.Add(_radioOverride);
        densityStack.Children.Add(_txtDensity);
        densityStack.Children.Add(lblDensityUnit);
        mainGrid.Children.Add(densityStack);
        Grid.SetRow(densityStack, row);

        // Event handlers for density radio buttons
        _radioMaterialDensity.Checked += (s, e) => _txtDensity.IsEnabled = false;
        _radioOverride.Checked += (s, e) => _txtDensity.IsEnabled = true;

        // Tolerance section
        row++;
        mainGrid.Children.Add(CreateLabel("Tolerance", row));
        row++;
        
        var toleranceStack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        _txtTolerance = new TextBox { Width = 50, Text = "2" };
        var lblToleranceUnit = new TextBlock { Text = "%", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        toleranceStack.Children.Add(_txtTolerance);
        toleranceStack.Children.Add(lblToleranceUnit);
        mainGrid.Children.Add(toleranceStack);
        Grid.SetRow(toleranceStack, row);

        // Buttons
        row = 6;
        var buttonStack = new StackPanel 
        { 
            Orientation = Orientation.Horizontal, 
            HorizontalAlignment = HorizontalAlignment.Right 
        };
        
        _btnGenerate = new Button 
        { 
            Content = "Generate", 
            Width = 100, 
            Height = 30,
            Margin = new Thickness(0, 0, 12, 0),
            IsDefault = true
        };
        _btnGenerate.Click += BtnGenerate_Click;
        
        _btnCancel = new Button 
        { 
            Content = "Cancel", 
            Width = 100, 
            Height = 30,
            IsCancel = true
        };
        _btnCancel.Click += (s, e) => DialogResult = false;
        
        buttonStack.Children.Add(_btnGenerate);
        buttonStack.Children.Add(_btnCancel);
        mainGrid.Children.Add(buttonStack);
        Grid.SetRow(buttonStack, row);

        Content = mainGrid;
    }

    private Label CreateLabel(string text, int row)
    {
        return new Label
        {
            Content = text,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        };
    }

    // UI controls
    private ComboBox _loadCaseCombo = null!;
    private CheckBox _chkStructural = null!;
    private CheckBox _chkArchitectural = null!;
    private CheckBox _chkLinked = null!;
    private CheckBox _chkMerge = null!;
    private CheckBox _chkIgnoreCurtain = null!;
    private CheckBox _chkIgnoreDemolished = null!;
    private RadioButton _radioMaterialDensity = null!;
    private RadioButton _radioOverride = null!;
    private TextBox _txtDensity = null!;
    private TextBox _txtTolerance = null!;
    private Button _btnGenerate = null!;
    private Button _btnCancel = null!;

    private void InitializeUI()
    {
        // Additional initialization if needed
    }

    private void LoadSettings()
    {
        // Load saved settings if available
        // For now, use defaults
    }

    private async void BtnGenerate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Gather settings from UI
            _settings.LoadCaseType = _loadCaseCombo.SelectedIndex switch
            {
                0 => Models.LoadCaseType.DeadLoad,
                1 => Models.LoadCaseType.SuperDeadLoad,
                2 => Models.LoadCaseType.LiveLoad,
                3 => Models.LoadCaseType.PartitionLoad,
                4 => Models.LoadCaseType.WindLoad,
                5 => Models.LoadCaseType.SeismicLoad,
                _ => Models.LoadCaseType.DeadLoad
            };
            
            _settings.IncludeStructural = _chkStructural.IsChecked ?? false;
            _settings.IncludeArchitectural = _chkArchitectural.IsChecked ?? false;
            _settings.IncludeLinkedModels = _chkLinked.IsChecked ?? false;
            _settings.MergeCoincidentLoads = _chkMerge.IsChecked ?? false;
            _settings.IgnoreCurtainWalls = _chkIgnoreCurtain.IsChecked ?? false;
            _settings.IgnoreDemolished = _chkIgnoreDemolished.IsChecked ?? false;
            _settings.UseMaterialDensity = _radioMaterialDensity.IsChecked ?? true;
            
            if (double.TryParse(_txtDensity.Text, out var density))
                _settings.OverrideDensity = density;
            
            if (double.TryParse(_txtTolerance.Text, out var tolerance))
                _settings.TolerancePercent = tolerance;

            // Disable UI during processing
            _btnGenerate.IsEnabled = false;
            _btnCancel.IsEnabled = false;
            
            // Show progress window
            var progressWindow = new ProgressWindow();
            progressWindow.Owner = this;
            progressWindow.Show();
            
            // Execute load generation
            await System.Threading.Tasks.Task.Run(() =>
            {
                var wallService = new Services.WallService(_doc, _settings);
                var walls = wallService.CollectWalls();
                
                progressWindow.UpdateProgress("Finding supporting floors...", 30);
                
                var loadService = new Services.LoadCreationService(_doc, _settings);
                
                // Get load case Id (placeholder - would need to get actual load case)
                var loadCaseId = ElementId.InvalidElementId;
                
                var result = loadService.CreateLoads(walls, loadCaseId);
                
                progressWindow.UpdateProgress("Complete!", 100);
                
                // Show results
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    progressWindow.ShowResults(result, walls.Count);
                });
            });
            
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Error: {ex.Message}", "Wall Load Generator", 
                MessageBoxButton.OK, MessageBoxImage.Error);
            DialogResult = false;
        }
        finally
        {
            _btnGenerate.IsEnabled = true;
            _btnCancel.IsEnabled = true;
        }
    }
}
