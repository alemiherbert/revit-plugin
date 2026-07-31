using System.Windows;
using System.Windows.Controls;
using System.Reflection;

namespace WallLoadGenerator.UI;

/// <summary>
/// About dialog showing add-in information.
/// </summary>
public class AboutWindow : Window
{
    public AboutWindow()
    {
        Title = "About Wall Load Generator";
        Width = 400;
        Height = 350;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        
        BuildUI();
    }

    private void BuildUI()
    {
        var mainGrid = new Grid();
        mainGrid.Margin = new Thickness(20);
        
        var rowDefs = new RowDefinitionCollection
        {
            new RowDefinition { Height = GridLength.Auto },   // Logo/Icon
            new RowDefinition { Height = GridLength.Auto },   // Title
            new RowDefinition { Height = GridLength.Auto },   // Version
            new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }, // Spacer
            new RowDefinition { Height = GridLength.Auto },   // Info
            new RowDefinition { Height = GridLength.Auto },   // Copyright
            new RowDefinition { Height = GridLength.Auto },   // Button
        };
        mainGrid.RowDefinitions = rowDefs;

        int row = 0;

        // Icon placeholder
        var iconBorder = new Border
        {
            Width = 64,
            Height = 64,
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0, 120, 215)), // Revit blue
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 16)
        };
        
        var iconText = new TextBlock
        {
            Text = "WL",
            FontSize = 28,
            FontWeight = System.Windows.FontWeights.Bold,
            Foreground = System.Windows.Media.Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        iconBorder.Child = iconText;
        mainGrid.Children.Add(iconBorder);
        Grid.SetRow(iconBorder, row);

        // Title
        row++;
        var titleText = new TextBlock
        {
            Text = "Wall Load Generator",
            FontSize = 24,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        };
        mainGrid.Children.Add(titleText);
        Grid.SetRow(titleText, row);

        // Version
        row++;
        var versionText = new TextBlock
        {
            Text = GetVersionInfo(),
            FontSize = 12,
            Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Colors.Gray),
            Margin = new Thickness(0, 0, 0, 16)
        };
        mainGrid.Children.Add(versionText);
        Grid.SetRow(versionText, row);

        // Description
        row++;
        var descriptionText = new TextBlock
        {
            Text = "A native Revit 2027 add-in for generating analytical line loads from walls to supporting floors.\n\n" +
                   "Features:\n" +
                   "• Automatic wall collection and filtering\n" +
                   "• Material-based load calculation\n" +
                   "• Coincident load merging\n" +
                   "• Support for linked models\n" +
                   "• Comprehensive diagnostics",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 16),
            LineHeight = 4
        };
        mainGrid.Children.Add(descriptionText);
        Grid.SetRow(descriptionText, row);

        // Info section
        row++;
        var infoBorder = new Border
        {
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Colors.LightGray),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 0, 0, 12)
        };
        
        var infoStack = new StackPanel();
        infoStack.Children.Add(CreateInfoLine("Framework:", ".NET 8 / WPF"));
        infoStack.Children.Add(CreateInfoLine("Revit Version:", "Revit 2027"));
        infoStack.Children.Add(CreateInfoLine("Vendor:", "Structural Tools"));
        infoStack.Children.Add(CreateInfoLine("License:", "Proprietary"));
        
        infoBorder.Child = infoStack;
        mainGrid.Children.Add(infoBorder);
        Grid.SetRow(infoBorder, row);

        // Copyright
        row++;
        var copyrightText = new TextBlock
        {
            Text = $"© {DateTime.Now.Year} Structural Tools. All rights reserved.",
            FontSize = 11,
            Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Colors.Gray),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 16)
        };
        mainGrid.Children.Add(copyrightText);
        Grid.SetRow(copyrightText, row);

        // OK Button
        row++;
        var btnOk = new Button
        {
            Content = "OK",
            Width = 100,
            Height = 30,
            HorizontalAlignment = HorizontalAlignment.Right,
            IsDefault = true
        };
        btnOk.Click += (s, e) => DialogResult = true;
        mainGrid.Children.Add(btnOk);
        Grid.SetRow(btnOk, row);

        Content = mainGrid;
    }

    private StackPanel CreateInfoLine(string label, string value)
    {
        var stack = new StackPanel 
        { 
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 2, 0, 2)
        };
        
        var labelBlock = new TextBlock
        {
            Text = label,
            Width = 100,
            FontWeight = System.Windows.FontWeights.SemiBold,
            FontSize = 11
        };
        
        var valueBlock = new TextBlock
        {
            Text = value,
            FontSize = 11
        };
        
        stack.Children.Add(labelBlock);
        stack.Children.Add(valueBlock);
        
        return stack;
    }

    private string GetVersionInfo()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version;
            return $"Version {version?.ToString(3) ?? "1.0.0"}";
        }
        catch
        {
            return "Version 1.0.0";
        }
    }
}
