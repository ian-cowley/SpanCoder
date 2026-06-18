using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Glacier.Grep;

namespace SpanCoder.Shell
{
    public class FindResultsPanel : Grid
    {
        private readonly ShellWindow _window;
        private readonly StackPanel _resultsPanel;
        private readonly TextBlock _summaryLabel;

        public FindResultsPanel(ShellWindow window)
        {
            _window = window;

            // Row 0: Summary Header (Auto)
            // Row 1: ScrollViewer with Results (Star)
            RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            RowDefinitions.Add(new RowDefinition(GridLength.Star));

            // Summary Header
            var headerGrid = new Grid
            {
                Background = new SolidColorBrush(Color.Parse("#252526")),
                Height = 26
            };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

            _summaryLabel = new TextBlock
            {
                Text = "No search results to display.",
                Foreground = Brushes.LightGray,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };
            headerGrid.Children.Add(_summaryLabel);
            Grid.SetColumn(_summaryLabel, 0);

            Children.Add(headerGrid);
            Grid.SetRow(headerGrid, 0);

            // Results List
            _resultsPanel = new StackPanel { Margin = new Thickness(10) };
            var scrollViewer = new ScrollViewer
            {
                Content = _resultsPanel,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            };
            Children.Add(scrollViewer);
            Grid.SetRow(scrollViewer, 1);
        }

        public void ClearResults()
        {
            _resultsPanel.Children.Clear();
            _summaryLabel.Text = "No search results to display.";
        }

        public void DisplayResults(string query, List<SearchResult> results, string scanRoot)
        {
            _resultsPanel.Children.Clear();

            int totalMatches = results.Count;
            var fileGroups = results.GroupBy(r => r.FilePath).ToList();
            int totalFiles = fileGroups.Count;

            _summaryLabel.Text = $"Find all \"{query}\", Subfolders, Find Results, Entire solution: {totalMatches} occurrence(s) found in {totalFiles} file(s)";

            if (totalMatches == 0)
            {
                _resultsPanel.Children.Add(new TextBlock
                {
                    Text = "Matching lines: 0. Matches found: 0. Total files searched: 0.",
                    Foreground = Brushes.Gray,
                    FontSize = 11,
                    FontStyle = FontStyle.Italic
                });
                return;
            }

            foreach (var group in fileGroups)
            {
                string relPath = group.Key;
                string fullPath = Path.Combine(scanRoot, relPath);
                string fileName = Path.GetFileName(relPath);

                // File Header
                var fileHeader = new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#2D2D2D")),
                    Padding = new Thickness(6, 4),
                    Margin = new Thickness(0, 6, 0, 2),
                    CornerRadius = new CornerRadius(2),
                    Cursor = new Cursor(StandardCursorType.Hand)
                };
                var headerText = new TextBlock
                {
                    Text = $"{fileName} ({group.Count()} matches) - {Path.GetDirectoryName(relPath)}",
                    Foreground = Brushes.LightGray,
                    FontSize = 11,
                    FontWeight = FontWeight.Bold,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                fileHeader.Child = headerText;
                fileHeader.PointerPressed += (s, e) => _window.LoadFile(fullPath);
                _resultsPanel.Children.Add(fileHeader);

                // Matching Lines
                foreach (var res in group)
                {
                    var matchRow = new Grid
                    {
                        Margin = new Thickness(12, 1, 0, 1),
                        Cursor = new Cursor(StandardCursorType.Hand)
                    };
                    matchRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto)); // Line number
                    matchRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star)); // Match content

                    var lineLabel = new TextBlock
                    {
                        Text = $"{res.LineNumber}: ",
                        Foreground = new SolidColorBrush(Color.Parse("#D19A66")),
                        FontSize = 11,
                        Margin = new Thickness(0, 0, 6, 0)
                    };
                    matchRow.Children.Add(lineLabel);
                    Grid.SetColumn(lineLabel, 0);

                    var contentLabel = new TextBlock
                    {
                        Text = res.MatchContent.Trim(),
                        Foreground = Brushes.LightGray,
                        FontSize = 11,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    };
                    matchRow.Children.Add(contentLabel);
                    Grid.SetColumn(contentLabel, 1);

                    matchRow.PointerEntered += (s, e) => contentLabel.Foreground = Brushes.White;
                    matchRow.PointerExited += (s, e) => contentLabel.Foreground = Brushes.LightGray;

                    matchRow.PointerPressed += (s, e) =>
                    {
                        _window.LoadFile(fullPath);
                        _window.MoveCaretToLine(res.LineNumber - 1);
                        _window.ReturnFocusToEditor();
                    };

                    _resultsPanel.Children.Add(matchRow);
                }
            }
        }
    }
}
