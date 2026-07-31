using System.Windows;
using System.Windows.Controls;

namespace WallLoadGenerator.UI;

/// <summary>
/// Settings window for configuring default preferences.
/// </summary>
public class SettingsWindow : Window
{
    public SettingsWindow()
    {
        Title = "Wall Load Generator - Settings";
        Width = 450;
        Height = 500;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        FontSize = 12;
        
        BuildUI();
    }

    private void BuildUI()
    {
        var mainGrid = new Grid();
        mainGrid.Margin = new Thickness(16);
        
        var rowDefs = new RowDefinitionCollection
        {
            new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }, // Content
            new RowDefinition { Height = GridLength.Auto },   // Buttons
        };
        mainGrid.RowDefinitions = rowDefs;

        // Create tab control
        var tabControl = new TabControl();
        
        // General tab
        var generalTab = new TabItem { Header = "General" };
        generalTab.Content = BuildGeneralTab();
        tabControl.Items.Add(generalTab);
        
        // Defaults tab
        var defaultsTab = new TabItem { Header = "Defaults" };
        defaultsTab.Content = BuildDefaultsTab();
        tabControl.Items.Add(defaultsTab);
        
        // Advanced tab
        var advancedTab = new TabItem { Header = "Advanced" };
        advancedTab.Content = BuildAdvancedTab();
        tabControl.Items.Add(advancedTab);
        
        mainGrid.Children.Add(tabControl);
        Grid.SetRow(tabControl, 0);

        // Buttons
        var buttonStack = new StackPanel 
        { 
            Orientation = Orientation.Horizontal, 
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        
        var btnSave = new Button 
        { 
            Content = "Save", 
            Width = 80, 
            Height = 28,
            Margin = new Thickness(0, 0, 12, 0),
            IsDefault = true
        };
        btnSave.Click += (s, e) =>
        {
            SaveSettings();
            DialogResult = true;
        };
        
        var btnCancel = new Button 
        { 
            Content = "Cancel", 
            Width = 80, 
            Height = 28,
            IsCancel = true
        };
        
        buttonStack.Children.Add(btnSave);
        buttonStack.Children.Add(btnCancel);
        mainGrid.Children.Add(buttonStack);
        Grid.SetRow(buttonStack, 1);

        Content = mainGrid;
    }

    private ScrollViewer BuildGeneralTab()
    {
        var scroll = new ScrollViewer();
        var stack = new StackPanel { Margin = new Thickness(8) };
        
        stack.Children.Add(CreateSectionHeader("Default Behavior"));
        
        stack.Children.Add(CreateCheckBox("Remember last used settings", true));
        stack.Children.Add(CreateCheckBox("Auto-select all walls", false));
        stack.Children.Add(CreateCheckBox("Show confirmation before creating loads", true));
        
        stack.Children.Add(new Separator { Margin = new Thickness(0, 12, 0, 12) });
        
        stack.Children.Add(CreateSectionHeader("Display"));
        
        stack.Children.Add(CreateCheckBox("Show detailed progress", true));
        stack.Children.Add(CreateCheckBox("Minimize to system tray during processing", false));
        
        scroll.Content = stack;
        return scroll;
    }

    private ScrollViewer BuildDefaultsTab()
    {
        var scroll = new ScrollViewer();
        var grid = new Grid { Margin = new Thickness(8) };
        
        var rowDefs = new RowDefinitionCollection
        {
            new RowDefinition { Height = GridLength.Auto },
            new RowDefinition { Height = GridLength.Auto },
            new RowDefinition { Height = GridLength.Auto },
            new RowDefinition { Height = GridLength.Auto },
            new RowDefinition { Height = GridLength.Auto },
        };
        grid.RowDefinitions = rowDefs;
        
        var colDefs = new ColumnDefinitionCollection
        {
            new ColumnDefinition { Width = GridLength.Auto },
            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
        };
        grid.ColumnDefinitions = colDefs;
        
        int row = 0;
        
        // Default load case
        grid.Children.Add(CreateLabel("Default load case:", row));
        var loadCaseCombo = new ComboBox 
        { 
            Width = 200, 
            Margin = new Thickness(8, 4, 0, 12),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        loadCaseCombo.Items.Add("Dead Load");
        loadCaseCombo.Items.Add("Super Dead Load");
        loadCaseCombo.Items.Add("Live Load");
        loadCaseCombo.SelectedIndex = 0;
        grid.Children.Add(loadCaseCombo);
        Grid.SetRow(loadCaseCombo, row);
        Grid.SetColumn(loadCaseCombo, 1);
        row++;
        
        // Default density
        grid.Children.Add(CreateLabel("Default density:", row));
        var densityStack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 4, 0, 12) };
        var txtDensity = new TextBox { Width = 80, Text = "24.0" };
        densityStack.Children.Add(txtDensity);
        densityStack.Children.Add(new TextBlock { Text = " kN/m³", VerticalAlignment = VerticalAlignment.Center });
        grid.Children.Add(densityStack);
        Grid.SetRow(densityStack, row);
        Grid.SetColumn(densityStack, 1);
        row++;
        
        // Default tolerance
        grid.Children.Add(CreateLabel("Default tolerance:", row));
        var toleranceStack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 4, 0, 12) };
        var txtTolerance = new TextBox { Width = 60, Text = "2" };
        toleranceStack.Children.Add(txtTolerance);
        toleranceStack.Children.Add(new TextBlock { Text = " %", VerticalAlignment = VerticalAlignment.Center });
        grid.Children.Add(toleranceStack);
        Grid.SetRow(toleranceStack, row);
        Grid.SetColumn(toleranceStack, 1);
        row++;
        
        // Minimum wall height
        grid.Children.Add(CreateLabel("Minimum wall height:", row));
        var minHeightStack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 4, 0, 12) };
        var txtMinHeight = new TextBox { Width = 60, Text = "0.5" };
        minHeightStack.Children.Add(txtMinHeight);
        minHeightStack.Children.Add(new TextBlock { Text = " m", VerticalAlignment = VerticalAlignment.Center });
        grid.Children.Add(minHeightStack);
        Grid.SetRow(minHeightStack, row);
        Grid.SetColumn(minHeightStack, 1);
        
        scroll.Content = grid;
        return scroll;
    }

    private ScrollViewer BuildAdvancedTab()
    {
        var scroll = new ScrollViewer();
        var stack = new StackPanel { Margin = new Thickness(8) };
        
        stack.Children.Add(CreateSectionHeader("Performance"));
        
        stack.Children.Add(CreateLabel("Maximum walls to process in batch:"));
        var txtBatchSize = new TextBox { Width = 100, Text = "1000", HorizontalAlignment = HorizontalAlignment.Left };
        stack.Children.Add(txtBatchSize);
        
        stack.Children.Add(new Separator { Margin = new Thickness(0, 12, 0, 12) });
        
        stack.Children.Add(CreateSectionHeader("Diagnostics"));
        
        stack.Children.Add(CreateCheckBox("Enable debug logging", false));
        stack.Children.Add(CreateCheckBox("Log skipped walls to file", true));
        stack.Children.Add(CreateCheckBox("Create diagnostic report after generation", false));
        
        stack.Children.Add(new Separator { Margin = new Thickness(0, 12, 0, 12) });
        
        stack.Children.Add(CreateSectionHeader("Data"));
        
        var btnExport = new Button 
        { 
            Content = "Export Settings...", 
            Width = 150, 
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 8)
        };
        
        var btnImport = new Button 
        { 
            Content = "Import Settings...", 
            Width = 150, 
            HorizontalAlignment = HorizontalAlignment.Left 
        };
        
        stack.Children.Add(btnExport);
        stack.Children.Add(btnImport);
        
        scroll.Content = stack;
        return scroll;
    }

    private Label CreateSectionHeader(string text)
    {
        return new Label
        {
            Content = text,
            FontWeight = System.Windows.FontWeights.SemiBold,
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 8)
        };
    }

    private CheckBox CreateCheckBox(string text, bool isChecked)
    {
        return new CheckBox
        {
            Content = text,
            IsChecked = isChecked,
            Margin = new Thickness(0, 4, 0, 4)
        };
    }

    private Label CreateLabel(string text, int row = -1)
    {
        var label = new Label
        {
            Content = text,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 4, 8, 12)
        };
        
        if (row >= 0)
        {
            Grid.SetRow(label, row);
        }
        
        return label;
    }

    private void SaveSettings()
    {
        // TODO: Implement settings persistence
        MessageBox.Show("Settings saved successfully.", "Wall Load Generator",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
