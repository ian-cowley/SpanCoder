using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Input;

namespace SpanCoder.Shell
{
    public class RenameDialog : Window
    {
        private readonly TextBox _textBox;
        public string? Result { get; private set; }

        public RenameDialog(string currentName)
        {
            Title = "Rename Symbol";
            Width = 350;
            Height = 150;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.Parse("#1E1E1E"));
            Foreground = Brushes.White;
            CanResize = false;

            var mainStack = new StackPanel
            {
                Margin = new Thickness(15),
                Spacing = 10
            };

            var label = new Label
            {
                Content = $"Rename '{currentName}' to:",
                Foreground = Brushes.LightGray,
                FontSize = 13
            };

            _textBox = new TextBox
            {
                Text = currentName,
                Background = new SolidColorBrush(Color.Parse("#2D2D2D")),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.Parse("#3E3E3E")),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(5),
                SelectionStart = 0,
                SelectionEnd = currentName.Length
            };

            var buttonStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 10,
                Margin = new Thickness(0, 10, 0, 0)
            };

            var okButton = new Button
            {
                Content = "Rename",
                Width = 85,
                Height = 28,
                Background = new SolidColorBrush(Color.Parse("#0E639C")),
                Foreground = Brushes.White,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            okButton.Click += (s, e) => Confirm();

            var cancelButton = new Button
            {
                Content = "Cancel",
                Width = 85,
                Height = 28,
                Background = new SolidColorBrush(Color.Parse("#3E3E3E")),
                Foreground = Brushes.White,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            cancelButton.Click += (s, e) => Close();

            buttonStack.Children.Add(okButton);
            buttonStack.Children.Add(cancelButton);

            mainStack.Children.Add(label);
            mainStack.Children.Add(_textBox);
            mainStack.Children.Add(buttonStack);

            Content = mainStack;

            // Handle pressing Enter or Escape
            _textBox.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    Confirm();
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    Close();
                    e.Handled = true;
                }
            };

            // Focus textbox on start
            Opened += (s, e) => _textBox.Focus();
        }

        private void Confirm()
        {
            if (!string.IsNullOrWhiteSpace(_textBox.Text))
            {
                Result = _textBox.Text.Trim();
                Close();
            }
        }
    }
}
