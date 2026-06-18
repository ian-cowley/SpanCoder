using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Input;

namespace SpanCoder.Shell
{
    public class SaveChangesDialog : Window
    {
        public enum DialogResult
        {
            Save,
            DontSave,
            Cancel
        }

        public DialogResult Result { get; private set; } = DialogResult.Cancel;

        public SaveChangesDialog(List<string> items)
        {
            Title = "Save Changes";
            Width = 460;
            Height = 280;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Brushes.Transparent;
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
            SystemDecorations = SystemDecorations.None;
            CanResize = false;

            // Main Border for Rounded Corners and Border Line
            var mainBorder = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#1E1E1F")), // Sleek dark background
                BorderBrush = new SolidColorBrush(Color.Parse("#3C3C3C")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(20),
                ClipToBounds = true
            };

            var mainGrid = new Grid();
            mainGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // Header
            mainGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star)); // List
            mainGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // Buttons

            // Header Layout (Title + Close X)
            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

            var titleText = new TextBlock
            {
                Text = "Save changes to the following items?",
                Foreground = Brushes.White,
                FontSize = 18,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            headerGrid.Children.Add(titleText);
            Grid.SetColumn(titleText, 0);

            var closeButton = new Button
            {
                Content = "✕",
                Foreground = Brushes.Gray,
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                FontSize = 16,
                Padding = new Thickness(5),
                Cursor = new Cursor(StandardCursorType.Hand),
                Focusable = false
            };
            closeButton.PointerEntered += (s, e) => closeButton.Foreground = Brushes.White;
            closeButton.PointerExited += (s, e) => closeButton.Foreground = Brushes.Gray;
            closeButton.Click += (s, e) => { Result = DialogResult.Cancel; Close(); };
            headerGrid.Children.Add(closeButton);
            Grid.SetColumn(closeButton, 1);

            mainGrid.Children.Add(headerGrid);
            Grid.SetRow(headerGrid, 0);

            // Drag support
            headerGrid.PointerPressed += (s, e) => BeginMoveDrag(e);

            // Body List of modified items
            var listBorder = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#161616")), // Darker list container
                BorderBrush = new SolidColorBrush(Color.Parse("#2D2D2D")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(0, 15, 0, 15),
                Padding = new Thickness(10)
            };

            var listScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
            };

            var listStack = new StackPanel { Spacing = 6 };
            foreach (var item in items)
            {
                var itemText = new TextBlock
                {
                    Text = item + "*",
                    Foreground = new SolidColorBrush(Color.Parse("#D4D4D4")),
                    FontSize = 13
                };
                listStack.Children.Add(itemText);
            }
            listScroll.Content = listStack;
            listBorder.Child = listScroll;

            mainGrid.Children.Add(listBorder);
            Grid.SetRow(listBorder, 1);

            // Footer Buttons
            var buttonStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Spacing = 12,
                Margin = new Thickness(0, 5, 0, 5)
            };

            // Save Button (Purple background, white text)
            var btnSave = new Button
            {
                Content = "Save",
                Width = 100,
                Height = 32,
                Background = new SolidColorBrush(Color.Parse("#7C70F2")),
                Foreground = Brushes.White,
                FontWeight = FontWeight.Medium,
                CornerRadius = new CornerRadius(4),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            btnSave.PointerEntered += (s, e) => btnSave.Background = new SolidColorBrush(Color.Parse("#8E82FF"));
            btnSave.PointerExited += (s, e) => btnSave.Background = new SolidColorBrush(Color.Parse("#7C70F2"));
            btnSave.Click += (s, e) => { Result = DialogResult.Save; Close(); };

            // Don't Save Button
            var btnDontSave = new Button
            {
                Content = "Don't Save",
                Width = 100,
                Height = 32,
                Background = new SolidColorBrush(Color.Parse("#2D2D30")),
                BorderBrush = new SolidColorBrush(Color.Parse("#3E3E42")),
                BorderThickness = new Thickness(1),
                Foreground = Brushes.White,
                FontWeight = FontWeight.Medium,
                CornerRadius = new CornerRadius(4),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            btnDontSave.PointerEntered += (s, e) => btnDontSave.Background = new SolidColorBrush(Color.Parse("#3E3E42"));
            btnDontSave.PointerExited += (s, e) => btnDontSave.Background = new SolidColorBrush(Color.Parse("#2D2D30"));
            btnDontSave.Click += (s, e) => { Result = DialogResult.DontSave; Close(); };

            // Cancel Button
            var btnCancel = new Button
            {
                Content = "Cancel",
                Width = 100,
                Height = 32,
                Background = new SolidColorBrush(Color.Parse("#2D2D30")),
                BorderBrush = new SolidColorBrush(Color.Parse("#3E3E42")),
                BorderThickness = new Thickness(1),
                Foreground = Brushes.White,
                FontWeight = FontWeight.Medium,
                CornerRadius = new CornerRadius(4),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            btnCancel.PointerEntered += (s, e) => btnCancel.Background = new SolidColorBrush(Color.Parse("#3E3E42"));
            btnCancel.PointerExited += (s, e) => btnCancel.Background = new SolidColorBrush(Color.Parse("#2D2D30"));
            btnCancel.Click += (s, e) => { Result = DialogResult.Cancel; Close(); };

            buttonStack.Children.Add(btnSave);
            buttonStack.Children.Add(btnDontSave);
            buttonStack.Children.Add(btnCancel);

            mainGrid.Children.Add(buttonStack);
            Grid.SetRow(buttonStack, 2);

            mainBorder.Child = mainGrid;
            Content = mainBorder;

            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    Result = DialogResult.Save;
                    Close();
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    Result = DialogResult.Cancel;
                    Close();
                    e.Handled = true;
                }
            };
        }
    }
}
