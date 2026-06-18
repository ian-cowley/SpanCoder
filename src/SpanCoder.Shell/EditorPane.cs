using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Buffers;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using OpenDocument = SpanCoder.Shell.ShellWindow.OpenDocument;

namespace SpanCoder.Shell
{
    public class EditorPane : Grid
    {
        private readonly ShellWindow _window;
        public TextEditorCanvas Canvas { get; }
        public ScrollBar VScroll { get; }
        public ScrollBar HScroll { get; }
        public StackPanel TabsContainer { get; }
        public Grid EditorGrid { get; }
        public Border Border { get; }
        public List<OpenDocument> OpenDocuments { get; } = new();
        public OpenDocument? ActiveDocument { get; set; }

        public EditorPane(ShellWindow window)
        {
            _window = window;

            // Set up Grid Rows:
            // Row 0: Tab Bar + Buttons (Auto)
            // Row 1: Editor Canvas + ScrollBars (Star)
            RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            RowDefinitions.Add(new RowDefinition(GridLength.Star));

            // Create Tab Area Layout: 2 Columns
            // Column 0: TabScroll (Star)
            // Column 1: Pane Action Buttons (Auto)
            var tabGrid = new Grid();
            tabGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            tabGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

            var tabScroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                Height = 32,
                Background = new SolidColorBrush(Color.Parse("#252525"))
            };

            TabsContainer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(4, 4, 4, 0)
            };
            tabScroll.Content = TabsContainer;
            tabGrid.Children.Add(tabScroll);
            Grid.SetColumn(tabScroll, 0);

            // Pane Action Buttons
            var actionsPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Background = new SolidColorBrush(Color.Parse("#252525")),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            };

            var btnSplitH = CreateActionButton("—", "Split Horizontally", () => _window.SplitActivePane(true));
            var btnSplitV = CreateActionButton("|", "Split Vertically", () => _window.SplitActivePane(false));
            var btnClose = CreateActionButton("✕", "Close Pane", () => _window.UnsplitPane(this));

            actionsPanel.Children.Add(btnSplitH);
            actionsPanel.Children.Add(btnSplitV);
            actionsPanel.Children.Add(btnClose);
            tabGrid.Children.Add(actionsPanel);
            Grid.SetColumn(actionsPanel, 1);

            Children.Add(tabGrid);
            Grid.SetRow(tabGrid, 0);

            // Create Editor Grid Area
            EditorGrid = new Grid();
            EditorGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star)); // Canvas
            EditorGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto)); // Scrollbar V
            EditorGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star)); // Canvas / Scrollbar V
            EditorGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // Scrollbar H

            Canvas = new TextEditorCanvas();
            Canvas.LayoutChanged += UpdateScrollbars;
            EditorGrid.Children.Add(Canvas);
            Grid.SetRow(Canvas, 0);
            Grid.SetColumn(Canvas, 0);

            VScroll = new ScrollBar
            {
                Orientation = Orientation.Vertical,
                Width = 16
            };
            EditorGrid.Children.Add(VScroll);
            Grid.SetRow(VScroll, 0);
            Grid.SetColumn(VScroll, 1);

            HScroll = new ScrollBar
            {
                Orientation = Orientation.Horizontal,
                Height = 16
            };
            EditorGrid.Children.Add(HScroll);
            Grid.SetRow(HScroll, 1);
            Grid.SetColumn(HScroll, 0);

            // Editor border to show active focus border outline
            Border = new Border
            {
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                Child = EditorGrid
            };
            Children.Add(Border);
            Grid.SetRow(Border, 1);

            // Wire PointerPressed on Canvas and Tab Area to set active focus
            Canvas.PointerPressed += (s, e) => _window.SetActivePane(this);
            tabGrid.PointerPressed += (s, e) => _window.SetActivePane(this);

            // Wire scroll events
            Canvas.ScrollRequested += (dx, dy) =>
            {
                if (dy != 0 && VScroll.IsEnabled)
                {
                    double newV = Math.Max(0, Math.Min(VScroll.Value + dy, VScroll.Maximum));
                    VScroll.Value = newV;
                    Canvas.ScrollY = newV;
                }
                if (dx != 0 && HScroll.IsEnabled)
                {
                    double newV = Math.Max(0, Math.Min(HScroll.Value + dx, HScroll.Maximum));
                    HScroll.Value = newV;
                    Canvas.ScrollX = newV;
                }
                Canvas.InvalidateVisual();
            };

            VScroll.Scroll += (s, e) =>
            {
                Canvas.ScrollY = e.NewValue;
                Canvas.InvalidateVisual();
            };

            HScroll.Scroll += (s, e) =>
            {
                Canvas.ScrollX = e.NewValue;
                Canvas.InvalidateVisual();
            };
        }

        private Button CreateActionButton(string text, string tooltip, Action action)
        {
            var btn = new Button
            {
                Content = text,
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                Foreground = Brushes.Gray,
                Padding = new Thickness(6, 4),
                Margin = new Thickness(1, 2),
                FontSize = 12,
                Focusable = false
            };
            ToolTip.SetTip(btn, tooltip);
            btn.PointerEntered += (s, e) =>
            {
                btn.Foreground = Brushes.White;
                btn.Background = new SolidColorBrush(Color.Parse("#3E3E40"));
            };
            btn.PointerExited += (s, e) =>
            {
                btn.Foreground = Brushes.Gray;
                btn.Background = Brushes.Transparent;
            };
            btn.Click += (s, e) => action();
            return btn;
        }

        public void UpdateScrollbars()
        {
            if (Canvas.Document == null)
            {
                VScroll.Maximum = 0;
                VScroll.ViewportSize = 0;
                VScroll.IsEnabled = false;
                HScroll.Maximum = 0;
                HScroll.ViewportSize = 0;
                HScroll.IsEnabled = false;
                Canvas.ScrollX = 0;
                Canvas.ScrollY = 0;
                return;
            }

            int lineCount = Canvas.GetVisibleLineCount();
            double viewportHeight = Canvas.Bounds.Height;
            double totalHeight = lineCount * Canvas.LineHeight;

            if (totalHeight > viewportHeight)
            {
                VScroll.Maximum = totalHeight - viewportHeight;
                VScroll.ViewportSize = viewportHeight;
                VScroll.IsEnabled = true;
            }
            else
            {
                VScroll.Maximum = 0;
                VScroll.ViewportSize = viewportHeight;
                VScroll.IsEnabled = false;
                Canvas.ScrollY = 0;
            }

            double viewportWidth = Canvas.Bounds.Width;
            int maxLineLen = 80;
            int checkLines = Math.Min(100, lineCount);
            for (int i = 0; i < checkLines; i++)
            {
                var line = Canvas.Document.GetLine(i, out _, out var rented);
                if (line.Length > maxLineLen) maxLineLen = line.Length;
                if (rented != null) ArrayPool<char>.Shared.Return(rented);
            }

            double maxLineWidth = Canvas.GetGutterWidth() + (maxLineLen + 10) * Canvas.CharWidth;
            if (maxLineWidth > viewportWidth)
            {
                HScroll.Maximum = maxLineWidth - viewportWidth;
                HScroll.ViewportSize = viewportWidth;
                HScroll.IsEnabled = true;
            }
            else
            {
                HScroll.Maximum = 0;
                HScroll.ViewportSize = viewportWidth;
                HScroll.IsEnabled = false;
                Canvas.ScrollX = 0;
            }
        }

        public FindReplaceOverlay? FindReplaceOverlay { get; set; }

        public void UpdateDocumentView()
        {
            if (ActiveDocument != null && ActiveDocument.FilePath.StartsWith("extension://"))
            {
                Canvas.IsVisible = false;
                VScroll.IsVisible = false;
                HScroll.IsVisible = false;

                // Remove existing Extension view if any
                var existing = EditorGrid.Children.Cast<Control>().FirstOrDefault(c => c != Canvas && c != VScroll && c != HScroll && !(c is FindReplaceOverlay));
                if (existing != null)
                {
                    EditorGrid.Children.Remove(existing);
                }

                // Create and add the new extension details view control!
                var extId = ActiveDocument.FilePath.Substring("extension://".Length);
                var extDetailsView = _window.CreateExtensionDetailsView(extId);
                if (extDetailsView != null)
                {
                    EditorGrid.Children.Add(extDetailsView);
                    Grid.SetRow(extDetailsView, 0);
                    Grid.SetColumn(extDetailsView, 0);
                    Grid.SetRowSpan(extDetailsView, 2);
                    Grid.SetColumnSpan(extDetailsView, 2);
                }
            }
            else
            {
                Canvas.IsVisible = true;
                VScroll.IsVisible = true;
                HScroll.IsVisible = true;

                var existing = EditorGrid.Children.Cast<Control>().FirstOrDefault(c => c != Canvas && c != VScroll && c != HScroll && !(c is FindReplaceOverlay));
                if (existing != null)
                {
                    EditorGrid.Children.Remove(existing);
                }
            }
        }

        public void ShowFindReplace(bool showReplace)
        {
            if (FindReplaceOverlay == null)
            {
                FindReplaceOverlay = new FindReplaceOverlay(this);
                EditorGrid.Children.Add(FindReplaceOverlay);
                Grid.SetRow(FindReplaceOverlay, 0);
                Grid.SetColumn(FindReplaceOverlay, 0);
            }
            FindReplaceOverlay.Show(showReplace);
        }
    }
}
