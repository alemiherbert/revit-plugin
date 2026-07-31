using System.Windows;
using System.Windows.Controls;

namespace StructuralTools.UI;

/// <summary>
/// Progress window showing operation status and results.
/// </summary>
public class ProgressWindow : Window
{
    private ProgressBar _progressBar = null!;
    private TextBlock _statusText = null!;
    private TextBlock _countText = null!;
    private StackPanel _resultsPanel = null!;

    public ProgressWindow()
    {
        Title = "Wall Load Generator";
        Width = 400;
        Height = 300;
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
            new RowDefinition { Height = GridLength.Auto },   // Status
            new RowDefinition { Height = GridLength.Auto },   // Progress bar
            new RowDefinition { Height = GridLength.Auto },   // Count
            new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }, // Spacer
            new RowDefinition { Height = GridLength.Auto },   // Results
            new RowDefinition { Height = GridLength.Auto },   // Close button
        };
        mainGrid.RowDefinitions = rowDefs;

        int row = 0;

        // Status text
        _statusText = new TextBlock
        {
            Text = "Collecting walls...",
            FontWeight = System.Windows.FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        };
        mainGrid.Children.Add(_statusText);
        Grid.SetRow(_statusText, row++);

        // Progress bar
        _progressBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Height = 20,
            Margin = new Thickness(0, 0, 0, 8)
        };
        mainGrid.Children.Add(_progressBar);
        Grid.SetRow(_progressBar, row++);

        // Count text
        _countText = new TextBlock
        {
            Text = "0 / 0",
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 16)
        };
        mainGrid.Children.Add(_countText);
        Grid.SetRow(_countText, row++);

        // Results panel (hidden initially)
        _resultsPanel = new StackPanel { Visibility = Visibility.Collapsed };
        
        var resultsBorder = new Border
        {
            BorderBrush = System.Windows.Media.Brushes.LightGray,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8),
            Child = _resultsPanel
        };
        mainGrid.Children.Add(resultsBorder);
        Grid.SetRow(resultsBorder, row++);

        // Close button (hidden initially)
        _closeButton = new Button
        {
            Content = "Close",
            Width = 100,
            Height = 30,
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        _closeButton.Click += (s, e) => DialogResult = true;
        mainGrid.Children.Add(_closeButton);
        Grid.SetRow(_closeButton, row);

        Content = mainGrid;
    }

    private Button _closeButton = null!;

    /// <summary>
    /// Updates the progress bar and status text.
    /// </summary>
    public void UpdateProgress(string status, double percent)
    {
        Dispatcher.Invoke(() =>
        {
            _statusText.Text = status;
            _progressBar.Value = percent;
        });
    }

    /// <summary>
    /// Updates the count display.
    /// </summary>
    public void UpdateCount(int current, int total)
    {
        Dispatcher.Invoke(() =>
        {
            _countText.Text = $"{current} / {total}";
        });
    }

    /// <summary>
    /// Shows the final results.
    /// </summary>
    public void ShowResults(Services.LoadCreationResult result, int totalWalls)
    {
        Dispatcher.Invoke(() =>
        {
            _resultsPanel.Children.Clear();
            
            _resultsPanel.Children.Add(CreateResultLine("Walls scanned", totalWalls.ToString()));
            _resultsPanel.Children.Add(CreateResultLine("Loads created", result.CreatedCount.ToString()));
            _resultsPanel.Children.Add(CreateResultLine("Skipped", result.SkippedCount.ToString()));
            _resultsPanel.Children.Add(new Separator { Margin = new Thickness(0, 8, 0, 8) });
            _resultsPanel.Children.Add(CreateResultLine("Elapsed time", result.ElapsedTime.TotalSeconds.ToString("F2") + " s"));
            
            if (result.SkippedReasons.Count > 0 && result.SkippedReasons.Count <= 10)
            {
                _resultsPanel.Children.Add(new TextBlock 
                { 
                    Text = "\nSkipped reasons:", 
                    FontWeight = System.Windows.FontWeights.SemiBold,
                    Margin = new Thickness(0, 8, 0, 4)
                });
                
                foreach (var reason in result.SkippedReasons.Take(10))
                {
                    _resultsPanel.Children.Add(new TextBlock 
                    { 
                        Text = $"• {reason}", 
                        FontSize = 10,
                        Foreground = System.Windows.Media.Brushes.Gray
                    });
                }
                
                if (result.SkippedReasons.Count > 10)
                {
                    _resultsPanel.Children.Add(new TextBlock 
                    { 
                        Text = $"... and {result.SkippedReasons.Count - 10} more", 
                        FontSize = 10,
                        Foreground = System.Windows.Media.Brushes.Gray
                    });
                }
            }
            
            _resultsPanel.Visibility = Visibility.Visible;
            _closeButton.Visibility = Visibility.Visible;
            _progressBar.IsEnabled = false;
        });
    }

    private UIElement CreateResultLine(string label, string value)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        
        var labelTb = new TextBlock { Text = label };
        var valueTb = new TextBlock 
        { 
            Text = value, 
            FontWeight = System.Windows.FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        
        grid.Children.Add(labelTb);
        grid.Children.Add(valueTb);
        Grid.SetColumn(valueTb, 1);
        
        return grid;
    }
}
