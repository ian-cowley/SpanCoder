using System;
using System.Collections.Generic;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace SpanCoder.Shell
{
    public class OutputControl : Grid
    {
        private readonly ComboBox _channelComboBox;
        private readonly TextBox _textBox;
        private readonly Dictionary<string, StringBuilder> _channels = new(StringComparer.OrdinalIgnoreCase);
        private string _activeChannel = "Build";

        public OutputControl()
        {
            // Initialize TextBox first to avoid lambda capture warnings
            _textBox = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(Color.Parse("#CCCCCC")),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(6)
            };

            RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // Toolbar
            RowDefinitions.Add(new RowDefinition(GridLength.Star)); // Text Area

            Background = new SolidColorBrush(Color.Parse("#1A1A1A"));

            // Initialize default channels
            _channels["Build"] = new StringBuilder();
            _channels["Deploy"] = new StringBuilder();
            _channels["General"] = new StringBuilder();
            _channels["Tests"] = new StringBuilder();

            // Toolbar Border
            var toolbarBorder = new Border
            {
                BorderBrush = new SolidColorBrush(Color.Parse("#2D2D2D")),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(6),
                Background = new SolidColorBrush(Color.Parse("#252526"))
            };

            var toolbarStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                VerticalAlignment = VerticalAlignment.Center
            };

            var lblShow = new TextBlock
            {
                Text = "Show output from:",
                Foreground = Brushes.LightGray,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12
            };
            toolbarStack.Children.Add(lblShow);

            _channelComboBox = new ComboBox
            {
                Width = 150,
                Background = new SolidColorBrush(Color.Parse("#1E1E1E")),
                Foreground = Brushes.White,
                FontSize = 12
            };
            _channelComboBox.ItemsSource = new List<string> { "Build", "Deploy", "General", "Tests" };
            _channelComboBox.SelectedIndex = 0;
            _channelComboBox.SelectionChanged += (s, e) =>
            {
                if (_channelComboBox.SelectedItem is string selected)
                {
                    SwitchChannel(selected);
                }
            };
            toolbarStack.Children.Add(_channelComboBox);

            var clearBtn = new Button
            {
                Content = "Clear Output",
                Background = new SolidColorBrush(Color.Parse("#3E3E42")),
                Foreground = Brushes.White,
                FontSize = 12,
                Padding = new Thickness(10, 4),
                VerticalAlignment = VerticalAlignment.Center
            };
            clearBtn.Click += (s, e) => ClearActiveChannel();
            toolbarStack.Children.Add(clearBtn);

            var wrapCheckbox = new CheckBox
            {
                Content = "Word Wrap",
                Foreground = Brushes.LightGray,
                FontSize = 12,
                IsChecked = false,
                VerticalAlignment = VerticalAlignment.Center
            };
            wrapCheckbox.IsCheckedChanged += (s, e) =>
            {
                _textBox.TextWrapping = wrapCheckbox.IsChecked == true ? TextWrapping.Wrap : TextWrapping.NoWrap;
            };
            toolbarStack.Children.Add(wrapCheckbox);

            toolbarBorder.Child = toolbarStack;
            Children.Add(toolbarBorder);
            Grid.SetRow(toolbarBorder, 0);

            // Add TextBox to grid
            Children.Add(_textBox);
            Grid.SetRow(_textBox, 1);
        }

        public void AppendText(string channel, string text)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!_channels.TryGetValue(channel, out var sb))
                {
                    sb = new StringBuilder();
                    _channels[channel] = sb;
                    // Dynamically update channels list in UI if new channel
                    var items = new List<string>(_channelComboBox.ItemsSource as List<string> ?? new List<string>());
                    if (!items.Contains(channel))
                    {
                        items.Add(channel);
                        _channelComboBox.ItemsSource = items;
                    }
                }

                sb.Append(text);
                if (sb.Length > 150000)
                {
                    sb.Remove(0, sb.Length - 100000);
                }

                if (string.Equals(channel, _activeChannel, StringComparison.OrdinalIgnoreCase))
                {
                    _textBox.Text = (_textBox.Text ?? "") + text;
                    _textBox.CaretIndex = _textBox.Text.Length;
                }
            });
        }

        public void SelectChannel(string channel)
        {
            if (_channelComboBox == null) return;
            var items = _channelComboBox.ItemsSource as List<string> ?? new List<string>();
            int idx = items.FindIndex(x => string.Equals(x, channel, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                _channelComboBox.SelectedIndex = idx;
            }
        }

        private void SwitchChannel(string channel)
        {
            _activeChannel = channel;
            if (_channels.TryGetValue(channel, out var sb))
            {
                _textBox.Text = sb.ToString();
            }
            else
            {
                _textBox.Text = "";
            }
            // Auto scroll to bottom
            _textBox.CaretIndex = _textBox.Text?.Length ?? 0;
        }

        private void ClearActiveChannel()
        {
            if (_channels.TryGetValue(_activeChannel, out var sb))
            {
                sb.Clear();
            }
            _textBox.Text = "";
        }
    }
}
