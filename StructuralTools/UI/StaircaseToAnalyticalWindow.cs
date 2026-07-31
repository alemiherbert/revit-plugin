using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace StructuralTools.UI;

/// <summary>
/// Main window for the Staircase to Analytical Model converter.
/// </summary>
public class StaircaseToAnalyticalWindow : Window
{
    private readonly UIApplication _uiApp;
    private readonly Document _doc;
    private Models.StaircaseSettings _settings = new();

    public StaircaseToAnalyticalWindow(UIApplication uiApp, Document doc)
    {
        _uiApp = uiApp;
        _doc = doc;
        
        InitializeComponent();
        BuildUI();
    }

    private void InitializeComponent()
    {
        Title = "Staircase to Analytical Model";
        Width = 450;
        Height = 500;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        FontSize = 12;
    }

    private void BuildUI()
    {
        var mainGrid = new Grid { Margin = new Thickness(16) };
        var rowDefs = new RowDefinitionCollection
        {
            new RowDefinition { Height = GridLength.Auto },   // Selection
            new RowDefinition { Height = GridLength.Auto },   // Components
            new RowDefinition { Height = GridLength.Auto },   // Material
            new RowDefinition { Height = GridLength.Auto },   // Options
            new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }, // Spacer
            new RowDefinition { Height = GridLength.Auto },   // Buttons
        };
        mainGrid.RowDefinitions = rowDefs;

        int row = 0;

        // Staircase Selection
        mainGrid.Children.Add(CreateLabel("Staircase Selection", row));
        row++;
        
        var selectionStack = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        _radioSelectedStairs = new RadioButton 
        { 
            Content = "Selected stairs only", 
            IsChecked = true,
            GroupName = "SelectionGroup",
            Margin = new Thickness(0, 0, 0, 8)
        };
        _radioAllStairs = new RadioButton 
        { 
            Content = "All stairs in model",
            GroupName = "SelectionGroup"
        };
        selectionStack.Children.Add(_radioSelectedStairs);
        selectionStack.Children.Add(_radioAllStairs);
        mainGrid.Children.Add(selectionStack);
        Grid.SetRow(selectionStack, row++);

        // Components to convert
        mainGrid.Children.Add(CreateLabel("Components to Convert", row));
        row++;
        
        var componentsStack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        _chkStringers = new CheckBox { Content = "Stringers", IsChecked = true, Margin = new Thickness(0, 0, 16, 0) };
        _chkTreads = new CheckBox { Content = "Treads", IsChecked = true, Margin = new Thickness(0, 0, 16, 0) };
        _chkLandings = new CheckBox { Content = "Landings", IsChecked = true };
        componentsStack.Children.Add(_chkStringers);
        componentsStack.Children.Add(_chkTreads);
        componentsStack.Children.Add(_chkLandings);
        mainGrid.Children.Add(componentsStack);
        Grid.SetRow(componentsStack, row++);

        // Material
        mainGrid.Children.Add(CreateLabel("Analytical Material", row));
        row++;
        
        var materialStack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        _materialCombo = new ComboBox { Width = 200 };
        _materialCombo.Items.Add("Structural Steel");
        _materialCombo.Items.Add("Concrete");
        _materialCombo.Items.Add("Wood");
        _materialCombo.SelectedIndex = 0;
        materialStack.Children.Add(_materialCombo);
        mainGrid.Children.Add(materialStack);
        Grid.SetRow(materialStack, row++);

        // Options
        mainGrid.Children.Add(CreateLabel("Options", row));
        row++;
        
        var optionsStack = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        _chkCreateMembers = new CheckBox 
        { 
            Content = "Create analytical members", 
            IsChecked = true,
            Margin = new Thickness(0, 0, 0, 8)
        };
        _chkPreserveGeometry = new CheckBox 
        { 
            Content = "Preserve original geometry",
            IsChecked = true
        };
        optionsStack.Children.Add(_chkCreateMembers);
        optionsStack.Children.Add(_chkPreserveGeometry);
        mainGrid.Children.Add(optionsStack);
        Grid.SetRow(optionsStack, row++);

        // Spacer (row++)

        // Buttons
        row = 5;
        var buttonStack = new StackPanel 
        { 
            Orientation = Orientation.Horizontal, 
            HorizontalAlignment = HorizontalAlignment.Right 
        };
        
        _btnConvert = new Button 
        { 
            Content = "Convert", 
            Width = 100, 
            Height = 30,
            Margin = new Thickness(0, 0, 12, 0),
            IsDefault = true
        };
        _btnConvert.Click += BtnConvert_Click;
        
        _btnCancel = new Button 
        { 
            Content = "Cancel", 
            Width = 100, 
            Height = 30,
            IsCancel = true
        };
        _btnCancel.Click += (s, e) => DialogResult = false;
        
        buttonStack.Children.Add(_btnConvert);
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
    private RadioButton _radioSelectedStairs = null!;
    private RadioButton _radioAllStairs = null!;
    private CheckBox _chkStringers = null!;
    private CheckBox _chkTreads = null!;
    private CheckBox _chkLandings = null!;
    private ComboBox _materialCombo = null!;
    private CheckBox _chkCreateMembers = null!;
    private CheckBox _chkPreserveGeometry = null!;
    private Button _btnConvert = null!;
    private Button _btnCancel = null!;

    private async void BtnConvert_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Gather settings
            _settings.IncludeStringers = _chkStringers.IsChecked ?? false;
            _settings.IncludeTreads = _chkTreads.IsChecked ?? false;
            _settings.IncludeLandings = _chkLandings.IsChecked ?? false;
            _settings.CreateAnalyticalMembers = _chkCreateMembers.IsChecked ?? false;
            _settings.PreserveGeometry = _chkPreserveGeometry.IsChecked ?? false;
            _settings.AnalyticalMaterial = _materialCombo.SelectedItem?.ToString() ?? "Structural Steel";

            bool processAllStairs = _radioAllStairs.IsChecked ?? false;

            // Disable UI
            _btnConvert.IsEnabled = false;
            _btnCancel.IsEnabled = false;

            // Show progress
            var progressWindow = new ProgressWindow();
            progressWindow.Owner = this;
            progressWindow.Show();
            progressWindow.UpdateProgress("Collecting staircases...", 10);

            await System.Threading.Tasks.Task.Run(() =>
            {
                // Placeholder for actual staircase processing
                // In a real implementation, this would:
                // 1. Collect stairs based on selection
                // 2. Extract geometry (stringers, treads, landings)
                // 3. Create analytical members
                
                System.Threading.Thread.Sleep(500); // Simulate processing
                
                progressWindow.UpdateProgress("Creating analytical members...", 60);
                
                System.Threading.Thread.Sleep(500);
                
                var result = new Services.LoadCreationResult
                {
                    CreatedCount = 0, // Would be actual count
                    TotalWalls = 0,
                    ElapsedTime = TimeSpan.FromSeconds(1.2)
                };
                
                progressWindow.UpdateProgress("Complete!", 100);
                
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    progressWindow.ShowResults(result, 0);
                });
            });

            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Error: {ex.Message}", "Staircase Converter", 
                MessageBoxButton.OK, MessageBoxImage.Error);
            DialogResult = false;
        }
        finally
        {
            _btnConvert.IsEnabled = true;
            _btnCancel.IsEnabled = true;
        }
    }
}
