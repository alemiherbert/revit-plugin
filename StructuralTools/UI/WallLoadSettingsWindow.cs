using System.Windows;
using System.Windows.Controls;

namespace StructuralTools.UI;

/// <summary>
/// Settings window for wall load generator configuration.
/// </summary>
public class WallLoadSettingsWindow : Window
{
    public WallLoadSettingsWindow()
    {
        Title = "Wall Load Settings";
        Width = 450;
        Height = 400;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        FontSize = 12;
        
        BuildUI();
    }

    private void BuildUI()
    {
        var mainGrid = new Grid { Margin = new Thickness(16) };
        var rowDefs = new RowDefinitionCollection
        {
            new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
            new RowDefinition { Height = GridLength.Auto },
        };
        mainGrid.RowDefinitions = rowDefs;

        // Tab control for different setting categories
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

        // OK/Cancel buttons
        var buttonStack = new StackPanel 
        { 
            Orientation = Orientation.Horizontal, 
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        
        var okButton = new Button 
        { 
            Content = "OK", 
            Width = 80, 
            Height = 28,
            Margin = new Thickness(0, 0, 12, 0),
            IsDefault = true
        };
        okButton.Click += (s, e) => DialogResult = true;
        
        var cancelButton = new Button 
        { 
            Content = "Cancel", 
            Width = 80, 
            Height = 28,
            IsCancel = true
        };
        cancelButton.Click += (s, e) => DialogResult = false;
        
        buttonStack.Children.Add(okButton);
        buttonStack.Children.Add(cancelButton);
        
        mainGrid.Children.Add(buttonStack);
        Grid.SetRow(buttonStack, 1);

        Content = mainGrid;
    }

    private UIElement BuildGeneralTab()
    {
        var stack = new StackPanel { Margin = new Thickness(12) };
        
        stack.Children.Add(new TextBlock 
        { 
            Text = "Default Wall Selection:", 
            FontWeight = System.Windows.FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        });
        
        stack.Children.Add(new CheckBox 
        { 
            Content = "Include structural walls", 
            IsChecked = true, 
            Margin = new Thickness(0, 0, 0, 4) 
        });
        
        stack.Children.Add(new CheckBox 
        { 
            Content = "Include architectural walls", 
            IsChecked = true, 
            Margin = new Thickness(0, 0, 0, 4) 
        });
        
        stack.Children.Add(new CheckBox 
        { 
            Content = "Include linked models", 
            Margin = new Thickness(0, 0, 0, 16) 
        });
        
        stack.Children.Add(new TextBlock 
        { 
            Text = "Default Options:", 
            FontWeight = System.Windows.FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        });
        
        stack.Children.Add(new CheckBox 
        { 
            Content = "Merge coincident loads", 
            IsChecked = true, 
            Margin = new Thickness(0, 0, 0, 4) 
        });
        
        stack.Children.Add(new CheckBox 
        { 
            Content = "Ignore curtain walls", 
            IsChecked = true, 
            Margin = new Thickness(0, 0, 0, 4) 
        });
        
        stack.Children.Add(new CheckBox 
        { 
            Content = "Ignore demolished elements", 
            IsChecked = true 
        });
        
        return stack;
    }

    private UIElement BuildDefaultsTab()
    {
        var grid = new Grid { Margin = new Thickness(12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        
        int row = 0;
        
        // Default density
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(new TextBlock 
        { 
            Text = "Default density:", 
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 12)
        });
        Grid.SetRow(grid.Children[grid.Children.Count - 1], row);
        Grid.SetColumn(grid.Children[grid.Children.Count - 1], 0);
        
        var densityStack = new StackPanel { Orientation = Orientation.Horizontal };
        densityStack.Children.Add(new TextBox { Width = 80, Text = "24.0" });
        densityStack.Children.Add(new TextBlock { Text = " kN/m³", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
        grid.Children.Add(densityStack);
        Grid.SetRow(densityStack, row);
        Grid.SetColumn(densityStack, 1);
        row++;
        
        // Default tolerance
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(new TextBlock 
        { 
            Text = "Merge tolerance:", 
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 12)
        });
        Grid.SetRow(grid.Children[grid.Children.Count - 1], row);
        Grid.SetColumn(grid.Children[grid.Children.Count - 1], 0);
        
        var toleranceStack = new StackPanel { Orientation = Orientation.Horizontal };
        toleranceStack.Children.Add(new TextBox { Width = 60, Text = "2" });
        toleranceStack.Children.Add(new TextBlock { Text = " %", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
        grid.Children.Add(toleranceStack);
        Grid.SetRow(toleranceStack, row);
        Grid.SetColumn(toleranceStack, 1);
        row++;
        
        // Default load case
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(new TextBlock 
        { 
            Text = "Default load case:", 
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        });
        Grid.SetRow(grid.Children[grid.Children.Count - 1], row);
        Grid.SetColumn(grid.Children[grid.Children.Count - 1], 0);
        
        var loadCaseCombo = new ComboBox { Width = 150, SelectedIndex = 0 };
        loadCaseCombo.Items.Add("Dead Load");
        loadCaseCombo.Items.Add("Super Dead Load");
        loadCaseCombo.Items.Add("Live Load");
        loadCaseCombo.Items.Add("Partition Load");
        grid.Children.Add(loadCaseCombo);
        Grid.SetRow(loadCaseCombo, row);
        Grid.SetColumn(loadCaseCombo, 1);
        
        return grid;
    }

    private UIElement BuildAdvancedTab()
    {
        var stack = new StackPanel { Margin = new Thickness(12) };
        
        stack.Children.Add(new TextBlock 
        { 
            Text = "Logging", 
            FontWeight = System.Windows.FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        });
        
        stack.Children.Add(new CheckBox 
        { 
            Content = "Enable debug logging", 
            Margin = new Thickness(0, 0, 0, 4) 
        });
        
        var logButton = new Button 
        { 
            Content = "Open Log Folder", 
            Width = 120, 
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 16)
        };
        logButton.Click += (s, e) =>
        {
            string logPath = Services.LoggingService.GetLogFilePath();
            if (!string.IsNullOrEmpty(logPath))
            {
                System.Diagnostics.Process.Start("explorer.exe", Path.GetDirectoryName(logPath));
            }
        };
        stack.Children.Add(logButton);
        
        stack.Children.Add(new TextBlock 
        { 
            Text = "Performance", 
            FontWeight = System.Windows.FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        });
        
        stack.Children.Add(new TextBlock 
        { 
            Text = "For large models (>5000 walls), consider:\n• Disabling linked model processing\n• Increasing merge tolerance\n• Filtering by specific wall types",
            FontSize = 11,
            Foreground = System.Windows.Media.Brushes.Gray,
            TextWrapping = TextWrapping.Wrap
        });
        
        return stack;
    }
}
