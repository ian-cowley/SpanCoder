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
        public Grid VScrollContainer { get; }
        public Border SplitHandle { get; }
        public Grid HScrollContainer { get; }
        public Grid VSplitContainer { get; }
        public Border VSplitHandle { get; }
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

            var btnClose = CreateActionButton("✕", "Close Pane", () => _window.UnsplitPane(this));

            actionsPanel.Children.Add(btnClose);
            tabGrid.Children.Add(actionsPanel);
            Grid.SetColumn(actionsPanel, 1);

            Children.Add(tabGrid);
            Grid.SetRow(tabGrid, 0);

            // Create Editor Grid Area
            EditorGrid = new Grid();
            EditorGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto)); // Col 0: VSplitContainer (vertical split handle)
            EditorGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star)); // Col 1: Canvas
            EditorGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto)); // Col 2: Scrollbar V
            EditorGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star)); // Canvas / Scrollbar V / VSplit
            EditorGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // Scrollbar H

            Canvas = new TextEditorCanvas();
            Canvas.IsGutterVisible = !_window._zenMode;
            Canvas.LayoutChanged += UpdateScrollbars;
            EditorGrid.Children.Add(Canvas);
            Grid.SetRow(Canvas, 0);
            Grid.SetColumn(Canvas, 1);

            VScrollContainer = new Grid();
            VScrollContainer.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // Row 0: Split handle
            VScrollContainer.RowDefinitions.Add(new RowDefinition(GridLength.Star)); // Row 1: VScroll

            SplitHandle = new Border
            {
                Height = 8,
                Background = new SolidColorBrush(Color.Parse("#3E3E40")),
                BorderBrush = new SolidColorBrush(Color.Parse("#252525")),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Cursor = new Cursor(StandardCursorType.SizeNorthSouth),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            ToolTip.SetTip(SplitHandle, "Drag down to split editor horizontally");

            // Micro-interactions for hover effect
            SplitHandle.PointerEntered += (s, e) => SplitHandle.Background = new SolidColorBrush(Color.Parse("#505052"));
            SplitHandle.PointerExited += (s, e) => SplitHandle.Background = new SolidColorBrush(Color.Parse("#3E3E40"));

            VScroll = new ScrollBar
            {
                Orientation = Orientation.Vertical,
                Width = 16
            };

            VScrollContainer.Children.Add(SplitHandle);
            Grid.SetRow(SplitHandle, 0);

            VScrollContainer.Children.Add(VScroll);
            Grid.SetRow(VScroll, 1);

            EditorGrid.Children.Add(VScrollContainer);
            Grid.SetRow(VScrollContainer, 0);
            Grid.SetColumn(VScrollContainer, 2);

            // Set up VSplitContainer and VSplitHandle (vertical split handle on the top-left)
            VSplitContainer = new Grid();
            VSplitContainer.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // Row 0: VSplitHandle
            VSplitContainer.RowDefinitions.Add(new RowDefinition(GridLength.Star)); // Row 1: Spacer

            VSplitHandle = new Border
            {
                Width = 8,
                Height = 16,
                Background = new SolidColorBrush(Color.Parse("#3E3E40")),
                BorderBrush = new SolidColorBrush(Color.Parse("#252525")),
                BorderThickness = new Thickness(0, 0, 1, 1),
                Cursor = new Cursor(StandardCursorType.SizeWestEast),
                VerticalAlignment = VerticalAlignment.Top
            };
            ToolTip.SetTip(VSplitHandle, "Drag right to split editor vertically");

            // Micro-interactions for hover effect
            VSplitHandle.PointerEntered += (s, e) => VSplitHandle.Background = new SolidColorBrush(Color.Parse("#505052"));
            VSplitHandle.PointerExited += (s, e) => VSplitHandle.Background = new SolidColorBrush(Color.Parse("#3E3E40"));

            VSplitContainer.Children.Add(VSplitHandle);
            Grid.SetRow(VSplitHandle, 0);

            EditorGrid.Children.Add(VSplitContainer);
            Grid.SetRow(VSplitContainer, 0);
            Grid.SetColumn(VSplitContainer, 0);

            // Set up HScrollContainer (horizontal scrollbar container)
            HScrollContainer = new Grid();

            HScroll = new ScrollBar
            {
                Orientation = Orientation.Horizontal,
                Height = 16
            };

            HScrollContainer.Children.Add(HScroll);

            EditorGrid.Children.Add(HScrollContainer);
            Grid.SetRow(HScrollContainer, 1);
            Grid.SetColumn(HScrollContainer, 1);

            // Wiring dragging for horizontal split (SplitHandle)
            Border? dragPreviewH = null;
            bool isDraggingH = false;

            SplitHandle.PointerPressed += (s, e) =>
            {
                var properties = e.GetCurrentPoint(SplitHandle).Properties;
                if (properties.IsLeftButtonPressed)
                {
                    isDraggingH = true;
                    e.Pointer.Capture(SplitHandle);
                    e.Handled = true;

                    // Create preview line overlay
                    dragPreviewH = new Border
                    {
                        Height = 2,
                        Background = new SolidColorBrush(Color.Parse("#007ACC")),
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        VerticalAlignment = VerticalAlignment.Top,
                        ZIndex = 999,
                        IsHitTestVisible = false
                    };
                    Children.Add(dragPreviewH);
                    Grid.SetRowSpan(dragPreviewH, 2);
                }
            };

            SplitHandle.PointerMoved += (s, e) =>
            {
                if (isDraggingH && dragPreviewH != null)
                {
                    var relativePoint = e.GetPosition(this);
                    dragPreviewH.Margin = new Thickness(0, relativePoint.Y, 0, 0);
                    e.Handled = true;
                }
            };

            Action finishDragH = () =>
            {
                if (isDraggingH)
                {
                    isDraggingH = false;
                    if (dragPreviewH != null)
                    {
                        Children.Remove(dragPreviewH);
                        dragPreviewH = null;
                    }
                }
            };

            SplitHandle.PointerReleased += (s, e) =>
            {
                if (isDraggingH)
                {
                    var relativePoint = e.GetPosition(this);
                    e.Pointer.Capture(null);
                    finishDragH();

                    if (relativePoint.Y > 30 && relativePoint.Y < Bounds.Height - 30)
                    {
                        double ratio = relativePoint.Y / Bounds.Height;
                        _window.SplitActivePane(true, ratio);
                    }
                    e.Handled = true;
                }
            };

            SplitHandle.PointerCaptureLost += (s, e) => finishDragH();

            // Wiring dragging for vertical split (VSplitHandle)
            Border? dragPreviewV = null;
            bool isDraggingV = false;

            VSplitHandle.PointerPressed += (s, e) =>
            {
                var properties = e.GetCurrentPoint(VSplitHandle).Properties;
                if (properties.IsLeftButtonPressed)
                {
                    isDraggingV = true;
                    e.Pointer.Capture(VSplitHandle);
                    e.Handled = true;

                    // Create preview line overlay
                    dragPreviewV = new Border
                    {
                        Width = 2,
                        Background = new SolidColorBrush(Color.Parse("#007ACC")),
                        VerticalAlignment = VerticalAlignment.Stretch,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        ZIndex = 999,
                        IsHitTestVisible = false
                    };
                    Children.Add(dragPreviewV);
                    Grid.SetRowSpan(dragPreviewV, 2);
                }
            };

            VSplitHandle.PointerMoved += (s, e) =>
            {
                if (isDraggingV && dragPreviewV != null)
                {
                    var relativePoint = e.GetPosition(this);
                    dragPreviewV.Margin = new Thickness(relativePoint.X, 0, 0, 0);
                    e.Handled = true;
                }
            };

            Action finishDragV = () =>
            {
                if (isDraggingV)
                {
                    isDraggingV = false;
                    if (dragPreviewV != null)
                    {
                        Children.Remove(dragPreviewV);
                        dragPreviewV = null;
                    }
                }
            };

            VSplitHandle.PointerReleased += (s, e) =>
            {
                if (isDraggingV)
                {
                    var relativePoint = e.GetPosition(this);
                    e.Pointer.Capture(null);
                    finishDragV();

                    if (relativePoint.X > 30 && relativePoint.X < Bounds.Width - 30)
                    {
                        double ratio = relativePoint.X / Bounds.Width;
                        _window.SplitActivePane(false, ratio);
                    }
                    e.Handled = true;
                }
            };

            VSplitHandle.PointerCaptureLost += (s, e) => finishDragV();

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

            UpdateSplitHandlesVisibility();
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
                VScrollContainer.IsVisible = false;
                HScrollContainer.IsVisible = false;
                VSplitContainer.IsVisible = false;

                // Remove existing Custom views if any
                var customControls = EditorGrid.Children.Cast<Control>().Where(c => 
                    c != Canvas && 
                    c != VScrollContainer && 
                    c != HScrollContainer && 
                    c != VSplitContainer && 
                    !(c is FindReplaceOverlay) && 
                    c != _window._autocompleteBorder && 
                    c != _window._hoverBorder && 
                    c != _window._debugToolbar).ToList();

                foreach (var ctrl in customControls)
                {
                    EditorGrid.Children.Remove(ctrl);
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
                    Grid.SetColumnSpan(extDetailsView, 3);
                }
            }
            else if (ActiveDocument != null && ActiveDocument.FilePath.StartsWith("gitdiff://"))
            {
                Canvas.IsVisible = false;
                VScrollContainer.IsVisible = false;
                HScrollContainer.IsVisible = false;
                VSplitContainer.IsVisible = false;

                // Remove existing Custom views if any
                var customControls = EditorGrid.Children.Cast<Control>().Where(c => 
                    c != Canvas && 
                    c != VScrollContainer && 
                    c != HScrollContainer && 
                    c != VSplitContainer && 
                    !(c is FindReplaceOverlay) && 
                    c != _window._autocompleteBorder && 
                    c != _window._hoverBorder && 
                    c != _window._debugToolbar).ToList();

                foreach (var ctrl in customControls)
                {
                    EditorGrid.Children.Remove(ctrl);
                }

                // Create and add the new Git Diff view control!
                var relPath = ActiveDocument.FilePath.Substring("gitdiff://".Length);
                var gitDiffView = _window.CreateGitDiffView(relPath);
                if (gitDiffView != null)
                {
                    EditorGrid.Children.Add(gitDiffView);
                    Grid.SetRow(gitDiffView, 0);
                    Grid.SetColumn(gitDiffView, 0);
                    Grid.SetRowSpan(gitDiffView, 2);
                    Grid.SetColumnSpan(gitDiffView, 3);
                }
            }
            else
            {
                Canvas.IsVisible = true;
                VScrollContainer.IsVisible = true;
                HScrollContainer.IsVisible = true;
                UpdateSplitHandlesVisibility();

                // Remove existing Custom views if any
                var customControls = EditorGrid.Children.Cast<Control>().Where(c => 
                    c != Canvas && 
                    c != VScrollContainer && 
                    c != HScrollContainer && 
                    c != VSplitContainer && 
                    !(c is FindReplaceOverlay) && 
                    c != _window._autocompleteBorder && 
                    c != _window._hoverBorder && 
                    c != _window._debugToolbar).ToList();

                foreach (var ctrl in customControls)
                {
                    EditorGrid.Children.Remove(ctrl);
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
                Grid.SetColumn(FindReplaceOverlay, 1);
            }
            FindReplaceOverlay.Show(showReplace);
        }

        public void UpdateSplitHandlesVisibility()
        {
            bool isSplit = _window.EditorPanesCount >= 2;
            SplitHandle.IsVisible = !isSplit;
            VSplitHandle.IsVisible = !isSplit;
            VSplitContainer.IsVisible = !isSplit;
        }
    }
}
