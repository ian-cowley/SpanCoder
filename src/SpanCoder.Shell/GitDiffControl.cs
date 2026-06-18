using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;

namespace SpanCoder.Shell
{
    public class GitDiffControl : Grid
    {
        private readonly string _relativePath;
        private readonly GitVersionProvider _gitProvider;
        private readonly Action _onStatusChanged;

        private readonly TextBlock _titleTextBlock;
        private readonly ListBox _diffListBox;

        public GitDiffControl(string relativePath, GitVersionProvider gitProvider, Action onStatusChanged)
        {
            _relativePath = relativePath;
            _gitProvider = gitProvider;
            _onStatusChanged = onStatusChanged;

            // Configure Grid: Row 0 is Header, Row 1 is Diff view
            RowDefinitions.Add(new RowDefinition(40, GridUnitType.Pixel));
            RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));

            // Header Background
            var headerBorder = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#252526")),
                BorderBrush = new SolidColorBrush(Color.Parse("#3D3D3D")),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(12, 0)
            };
            Children.Add(headerBorder);
            SetRow(headerBorder, 0);

            var headerDock = new DockPanel { LastChildFill = true };
            headerBorder.Child = headerDock;

            // Title block
            _titleTextBlock = new TextBlock
            {
                Text = $"Diff: {relativePath} (HEAD ↔ Working Copy)",
                Foreground = Brushes.White,
                FontWeight = FontWeight.SemiBold,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            headerDock.Children.Add(_titleTextBlock);

            // Action Buttons Panel
            var buttonsStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(buttonsStack, Dock.Right);
            headerDock.Children.Add(buttonsStack);

            var stageButton = new Button
            {
                Content = "Stage",
                Background = new SolidColorBrush(Color.Parse("#007ACC")),
                Foreground = Brushes.White,
                Padding = new Thickness(12, 4),
                FontSize = 11,
                CornerRadius = new CornerRadius(3)
            };
            stageButton.Click += async (s, e) =>
            {
                await _gitProvider.StageFileAsync(_relativePath);
                _onStatusChanged?.Invoke();
            };
            buttonsStack.Children.Add(stageButton);

            var unstageButton = new Button
            {
                Content = "Unstage",
                Background = new SolidColorBrush(Color.Parse("#3E3E3E")),
                Foreground = Brushes.White,
                Padding = new Thickness(12, 4),
                FontSize = 11,
                CornerRadius = new CornerRadius(3)
            };
            unstageButton.Click += async (s, e) =>
            {
                await _gitProvider.UnstageFileAsync(_relativePath);
                _onStatusChanged?.Invoke();
            };
            buttonsStack.Children.Add(unstageButton);

            // Diff ListBox
            _diffListBox = new ListBox
            {
                Background = new SolidColorBrush(Color.Parse("#1E1E1E")),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0)
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(_diffListBox, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
            ScrollViewer.SetVerticalScrollBarVisibility(_diffListBox, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);

            // Custom ItemTemplate for Diff rows
            _diffListBox.ItemTemplate = new FuncDataTemplate<DiffLine>((diffLine, names) =>
            {
                var rowGrid = new Grid();
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition(45, GridUnitType.Pixel)); // Left Line Number
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition(1.0, GridUnitType.Star)); // Left Line Text
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Pixel));  // Vertical Divider
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition(45, GridUnitType.Pixel)); // Right Line Number
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition(1.0, GridUnitType.Star)); // Right Line Text

                IBrush rowBackground = Brushes.Transparent;
                IBrush leftTextBrush = Brushes.LightGray;
                IBrush rightTextBrush = Brushes.LightGray;
                IBrush leftLineNoBrush = new SolidColorBrush(Color.Parse("#5A5A5A"));
                IBrush rightLineNoBrush = new SolidColorBrush(Color.Parse("#5A5A5A"));

                if (diffLine.Type == DiffType.Deleted)
                {
                    rowBackground = new SolidColorBrush(Color.Parse("#4A1515")); // Soft red
                    leftTextBrush = new SolidColorBrush(Color.Parse("#FFAAAA"));
                    leftLineNoBrush = new SolidColorBrush(Color.Parse("#FF5555"));
                }
                else if (diffLine.Type == DiffType.Added)
                {
                    rowBackground = new SolidColorBrush(Color.Parse("#154A15")); // Soft green
                    rightTextBrush = new SolidColorBrush(Color.Parse("#AAFFAA"));
                    rightLineNoBrush = new SolidColorBrush(Color.Parse("#55FF55"));
                }

                rowGrid.Background = rowBackground;

                // Left Line Number
                var leftNo = new TextBlock
                {
                    Text = diffLine.LeftLineNumber.HasValue ? diffLine.LeftLineNumber.Value.ToString() : "",
                    Foreground = leftLineNoBrush,
                    FontSize = 11,
                    FontFamily = new FontFamily("Consolas"),
                    TextAlignment = TextAlignment.Right,
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                rowGrid.Children.Add(leftNo);
                SetColumn(leftNo, 0);

                // Left Text
                var leftTxt = new TextBlock
                {
                    Text = diffLine.LeftText,
                    Foreground = leftTextBrush,
                    FontSize = 12,
                    FontFamily = new FontFamily("Consolas"),
                    Margin = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                rowGrid.Children.Add(leftTxt);
                SetColumn(leftTxt, 1);

                // Divider
                var divider = new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#303030")),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch
                };
                rowGrid.Children.Add(divider);
                SetColumn(divider, 2);

                // Right Line Number
                var rightNo = new TextBlock
                {
                    Text = diffLine.RightLineNumber.HasValue ? diffLine.RightLineNumber.Value.ToString() : "",
                    Foreground = rightLineNoBrush,
                    FontSize = 11,
                    FontFamily = new FontFamily("Consolas"),
                    TextAlignment = TextAlignment.Right,
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                rowGrid.Children.Add(rightNo);
                SetColumn(rightNo, 3);

                // Right Text
                var rightTxt = new TextBlock
                {
                    Text = diffLine.RightText,
                    Foreground = rightTextBrush,
                    FontSize = 12,
                    FontFamily = new FontFamily("Consolas"),
                    Margin = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                rowGrid.Children.Add(rightTxt);
                SetColumn(rightTxt, 4);

                return rowGrid;
            }, true);

            Children.Add(_diffListBox);
            SetRow(_diffListBox, 1);
        }

        public void SetDiff(string headContent, string localContent)
        {
            string[] headLines = headContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            string[] localLines = localContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

            var diffLines = DiffAlgorithm.ComputeDiff(headLines, localLines);
            _diffListBox.ItemsSource = diffLines;
        }
    }
}
