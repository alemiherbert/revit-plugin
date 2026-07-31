using System.Windows;
using System.Windows.Controls;
using WallLoadGenerator.Services;

namespace WallLoadGenerator.UI;

/// <summary>
/// Progress window showing load generation progress and results.
/// </summary>
public class ProgressWindow : Window
{
    private ProgressBar _progressBar = null!;
    private TextBlock _statusText = null!;
    private TextBlock _countText = null!;
    private StackPanel _resultsPanel = null!;
    private Button _btnClose = null!;

    public ProgressWindow()
    {
        Title = "Wall Load Generator - Progress";
        Width = 400;
        Height = 300;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        
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
            new RowDefinition { Height = GridLength.Auto },   // Status
            new RowDefinition { Height = GridLength.Auto },   // Progress bar
            new RowDefinition { Height = GridLength.Auto },   // Count
            new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }, // Spacer
            new RowDefinition { Height = GridLength.Auto },   // Results
            new RowDefinition { Height = GridLength.Auto },   // Button
        };
        mainGrid.RowDefinitions = rowDefs;

        int row = 0;

        // Status text
        _statusText = new TextBlock
        {
            Text = "Initializing...",
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 12)
        };
        mainGrid.Children.Add(_statusText);
        Grid.SetRow(_statusText, row);

        // Progress bar
        row++;
        _progressBar = new ProgressBar
        {
            Height = 24,
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Margin = new Thickness(0, 0, 0, 12)
        };
        mainGrid.Children.Add(_progressBar);
        Grid.SetRow(_progressBar, row);

        // Count text
        row++;
        _countText = new TextBlock
        {
            Text = "0 / 0",
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 0, 12),
            Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Colors.Gray)
        };
        mainGrid.Children.Add(_countText);
        Grid.SetRow(_countText, row);

        // Results panel (hidden initially)
        row++;
        _resultsPanel = new StackPanel
        {
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 0, 0, 12)
        };
        
        var resultsBorder = new Border
        {
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Colors.LightGray),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(4),
            Child = _resultsPanel
        };
        mainGrid.Children.Add(resultsBorder);
        Grid.SetRow(resultsBorder, 4);

        // Close button
        row = 5;
        _btnClose = new Button
        {
            Content = "Close",
            Width = 100,
            Height = 30,
            HorizontalAlignment = HorizontalAlignment.Right,
            Visibility = Visibility.Collapsed
        };
        _btnClose.Click += (s, e) => DialogResult = true;
        mainGrid.Children.Add(_btnClose);
        Grid.SetRow(_btnClose, row);

        Content = mainGrid;
    }

    public void UpdateProgress(string status, double percent)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            _statusText.Text = status;
            _progressBar.Value = percent;
        });
    }

    public void UpdateCount(int current, int total)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            _countText.Text = $"{current} / {total}";
        });
    }

    public void ShowResults(LoadCreationResult result, int totalWalls)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            _resultsPanel.Children.Clear();
            
            // Add summary
            AddResultItem($"Walls scanned:      {totalWalls}");
            AddResultItem($"Loads created:      {result.CreatedCount}");
            AddResultItem($"Skipped:            {result.SkippedCount}");
            
            if (result.MergedCount > 0)
                AddResultItem($"Merged loads:       {result.MergedCount}");
            
            // Add elapsed time estimate
            AddResultItem("");
            AddResultItem($"Status:             {(result.Success ? "✓ Success" : "✗ Failed")}");
            
            // Show skipped reasons if any
            if (result.SkippedWalls.Count > 0)
            {
                AddResultItem("");
                AddResultItem($"Skipped walls:");
                
                var reasonGroups = result.SkippedWalls
                    .GroupBy(x => x.Reason)
                    .ToDictionary(g => g.Key, g => g.Count());
                
                foreach (var kvp in reasonGroups)
                {
                    AddResultItem($"  • {kvp.Key}: {kvp.Value}", true);
                }
            }
            
            _resultsPanel.Visibility = Visibility.Visible;
            _btnClose.Visibility = Visibility.Visible;
            
            // Update title
            Title = $"Wall Load Generator - Complete ({result.CreatedCount} loads)";
        });
    }

    private void AddResultItem(string text, bool isSubItem = false)
    {
        var textBlock = new TextBlock
        {
            Text = text,
            Margin = new Thickness(0, 2, 0, 2),
            FontFamily = new System.Windows.Media.FontFamily("Consolas")
        };
        
        if (isSubItem)
        {
            textBlock.FontSize = 11;
            textBlock.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(100, 100, 100));
        }
        
        _resultsPanel.Children.Add(textBlock);
    }
}
