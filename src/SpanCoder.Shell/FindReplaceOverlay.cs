using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace SpanCoder.Shell
{
    public class FindReplaceOverlay : Border
    {
        private readonly EditorPane _pane;
        private readonly TextEditorCanvas _canvas;
        private readonly TextBox _findInput;
        private readonly TextBox _replaceInput;
        private readonly TextBlock _statusLabel;
        private readonly Grid _replaceRow;
        private readonly Button _toggleReplaceBtn;
        private bool _isReplaceVisible;

        public FindReplaceOverlay(EditorPane pane)
        {
            _pane = pane;
            _canvas = pane.Canvas;

            // Premium Styling
            Width = 320;
            VerticalAlignment = VerticalAlignment.Top;
            HorizontalAlignment = HorizontalAlignment.Right;
            Margin = new Thickness(0, 8, 20, 0); // Spaced from top and vertical scrollbar
            ZIndex = 150;
            Background = new SolidColorBrush(Color.Parse("#252526")); // Visual Studio dark grey
            BorderBrush = new SolidColorBrush(Color.Parse("#3F3F46")); // Visual Studio border grey
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(4);
            BoxShadow = BoxShadows.Parse("0 4 12 0 #80000000"); // Drop shadow

            var mainLayout = new Grid();
            mainLayout.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // Row 0: Find input & controls
            mainLayout.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // Row 1: Replace input & controls

            // --- Row 0: Find Input Row ---
            var findGrid = new Grid();
            findGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto)); // Toggle replace button
            findGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star)); // Find TextBox
            findGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto)); // Match counts
            findGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto)); // Prev
            findGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto)); // Next
            findGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto)); // Close

            // Toggle Replace Button
            _toggleReplaceBtn = new Button
            {
                Content = "▸",
                Background = Brushes.Transparent,
                Foreground = Brushes.LightGray,
                BorderBrush = Brushes.Transparent,
                Width = 24,
                Height = 24,
                FontSize = 12,
                Padding = new Thickness(0),
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            _toggleReplaceBtn.Click += (s, e) => ToggleReplace();
            findGrid.Children.Add(_toggleReplaceBtn);
            Grid.SetColumn(_toggleReplaceBtn, 0);

            // Find Input
            _findInput = new TextBox
            {
                Watermark = "Find",
                Background = new SolidColorBrush(Color.Parse("#333337")),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.Parse("#3F3F46")),
                BorderThickness = new Thickness(1),
                Height = 22,
                FontSize = 12,
                Margin = new Thickness(4, 4, 2, 4),
                Padding = new Thickness(4, 0, 4, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                CaretBrush = Brushes.White
            };
            _findInput.TextChanged += (s, e) => OnFindTextChanged();
            _findInput.KeyDown += OnInputKeyDown;
            findGrid.Children.Add(_findInput);
            Grid.SetColumn(_findInput, 1);

            // Status Label
            _statusLabel = new TextBlock
            {
                Text = "0 of 0",
                Foreground = Brushes.Gray,
                FontSize = 10,
                Margin = new Thickness(4, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            findGrid.Children.Add(_statusLabel);
            Grid.SetColumn(_statusLabel, 2);

            // Prev Button
            var prevBtn = CreateIconButton("↑", "Previous Match (Shift+Enter)", () => _canvas.FindPrevMatch());
            findGrid.Children.Add(prevBtn);
            Grid.SetColumn(prevBtn, 3);

            // Next Button
            var nextBtn = CreateIconButton("↓", "Next Match (Enter)", () => _canvas.FindNextMatch());
            findGrid.Children.Add(nextBtn);
            Grid.SetColumn(nextBtn, 4);

            // Close Button
            var closeBtn = CreateIconButton("✕", "Close (Esc)", () => Hide());
            findGrid.Children.Add(closeBtn);
            Grid.SetColumn(closeBtn, 5);

            mainLayout.Children.Add(findGrid);
            Grid.SetRow(findGrid, 0);

            // --- Row 1: Replace Input Row ---
            _replaceRow = new Grid();
            _replaceRow.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(24))); // spacer
            _replaceRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star)); // Replace TextBox
            _replaceRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto)); // Replace button
            _replaceRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto)); // Replace All button
            _replaceRow.Margin = new Thickness(0, 0, 4, 4);
            _replaceRow.IsVisible = false;

            // Replace Input
            _replaceInput = new TextBox
            {
                Watermark = "Replace with",
                Background = new SolidColorBrush(Color.Parse("#333337")),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.Parse("#3F3F46")),
                BorderThickness = new Thickness(1),
                Height = 22,
                FontSize = 12,
                Margin = new Thickness(4, 0, 2, 0),
                Padding = new Thickness(4, 0, 4, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                CaretBrush = Brushes.White
            };
            _replaceInput.KeyDown += OnInputKeyDown;
            _replaceRow.Children.Add(_replaceInput);
            Grid.SetColumn(_replaceInput, 1);

            // Replace Button
            var replaceBtn = CreateTextButton("Replace", "Replace Current Match", () => {
                _canvas.ReplaceActiveMatch(_replaceInput.Text ?? "");
                UpdateStatus();
            });
            _replaceRow.Children.Add(replaceBtn);
            Grid.SetColumn(replaceBtn, 2);

            // Replace All Button
            var replaceAllBtn = CreateTextButton("All", "Replace All Matches", () => {
                _canvas.ReplaceAllMatches(_replaceInput.Text ?? "");
                UpdateStatus();
            });
            _replaceRow.Children.Add(replaceAllBtn);
            Grid.SetColumn(replaceAllBtn, 3);

            mainLayout.Children.Add(_replaceRow);
            Grid.SetRow(_replaceRow, 1);

            Child = mainLayout;
            IsVisible = false;
        }

        private Button CreateIconButton(string text, string tooltip, Action action)
        {
            var btn = new Button
            {
                Content = text,
                Background = Brushes.Transparent,
                Foreground = Brushes.LightGray,
                BorderBrush = Brushes.Transparent,
                Width = 24,
                Height = 24,
                FontSize = 11,
                Padding = new Thickness(0),
                Cursor = new Cursor(StandardCursorType.Hand),
                Focusable = false
            };
            ToolTip.SetTip(btn, tooltip);
            btn.Click += (s, e) => action();
            return btn;
        }

        private Button CreateTextButton(string text, string tooltip, Action action)
        {
            var btn = new Button
            {
                Content = text,
                Background = new SolidColorBrush(Color.Parse("#3E3E42")),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.Parse("#555555")),
                BorderThickness = new Thickness(1),
                Height = 22,
                FontSize = 10,
                Padding = new Thickness(6, 0, 6, 0),
                Margin = new Thickness(2, 0, 2, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand),
                Focusable = false
            };
            ToolTip.SetTip(btn, tooltip);
            btn.Click += (s, e) => action();
            return btn;
        }

        public void Show(bool showReplace)
        {
            IsVisible = true;
            _isReplaceVisible = showReplace;
            _replaceRow.IsVisible = showReplace;
            _toggleReplaceBtn.Content = showReplace ? "▾" : "▸";
            
            // Populate selection text if any text is selected
            string selectedText = _canvas.GetSelectedText(out _, out _);
            if (!string.IsNullOrEmpty(selectedText) && !selectedText.Contains('\n') && !selectedText.Contains('\r'))
            {
                _findInput.Text = selectedText;
            }

            _findInput.Focus();
            _findInput.SelectAll();
            OnFindTextChanged();
        }

        public void Hide()
        {
            IsVisible = false;
            _canvas.ClearSearchMatches();
            _pane.Canvas.Focus();
        }

        private void ToggleReplace()
        {
            _isReplaceVisible = !_isReplaceVisible;
            _replaceRow.IsVisible = _isReplaceVisible;
            _toggleReplaceBtn.Content = _isReplaceVisible ? "▾" : "▸";
        }

        private void OnFindTextChanged()
        {
            _canvas.UpdateSearchQuery(_findInput.Text ?? "");
            UpdateStatus();
        }

        private void UpdateStatus()
        {
            int total = _canvas.SearchMatches.Count;
            if (total == 0)
            {
                _statusLabel.Text = "No results";
                _statusLabel.Foreground = Brushes.Gray;
            }
            else
            {
                int current = _canvas.ActiveSearchMatchIndex + 1;
                _statusLabel.Text = $"{current} of {total}";
                _statusLabel.Foreground = Brushes.LightGray;
            }
        }

        private void OnInputKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Hide();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                if (e.KeyModifiers == KeyModifiers.Shift)
                {
                    _canvas.FindPrevMatch();
                }
                else
                {
                    _canvas.FindNextMatch();
                }
                UpdateStatus();
                e.Handled = true;
            }
        }
    }
}
