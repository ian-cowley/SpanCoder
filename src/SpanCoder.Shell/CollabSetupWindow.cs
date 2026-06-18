using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Input;

namespace SpanCoder.Shell
{
    public class CollabSetupWindow : Window
    {
        public bool IsCancelled { get; private set; } = true;
        public string Username { get; private set; } = "";
        public int Port { get; private set; } = 5080;
        public string HostIp { get; private set; } = "127.0.0.1";

        public CollabSetupWindow(bool isHost)
        {
            Title = isHost ? "Host Collaboration" : "Join Collaboration";
            Width = 400;
            Height = 220;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Brushes.Transparent;
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
            SystemDecorations = SystemDecorations.None;
            CanResize = false;

            var mainBorder = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#1E1E1F")),
                BorderBrush = new SolidColorBrush(Color.Parse("#3C3C3C")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(20),
                ClipToBounds = true
            };

            var mainGrid = new Grid();
            mainGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // Header
            mainGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star)); // Inputs
            mainGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // Buttons

            // Header Layout (Title + Close X)
            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

            var titleText = new TextBlock
            {
                Text = isHost ? "Host Collaboration Session" : "Join Collaboration Session",
                Foreground = Brushes.White,
                FontSize = 16,
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
            closeButton.Click += (s, e) => Close();
            headerGrid.Children.Add(closeButton);
            Grid.SetColumn(closeButton, 1);

            mainGrid.Children.Add(headerGrid);
            Grid.SetRow(headerGrid, 0);
            headerGrid.PointerPressed += (s, e) => BeginMoveDrag(e);

            // Inputs Layout
            var inputsStack = new StackPanel { Spacing = 10, Margin = new Thickness(0, 15, 0, 15) };

            // Username field
            var usernameLabel = new TextBlock { Text = "Username:", Foreground = Brushes.Gray, FontSize = 12 };
            var usernameInput = new TextBox
            {
                Text = Environment.UserName,
                Background = new SolidColorBrush(Color.Parse("#161616")),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.Parse("#2D2D2D")),
                Padding = new Thickness(8, 4),
                FontSize = 13
            };
            inputsStack.Children.Add(usernameLabel);
            inputsStack.Children.Add(usernameInput);

            // Port or IP field
            TextBox connectionInput;
            if (isHost)
            {
                var portLabel = new TextBlock { Text = "Port:", Foreground = Brushes.Gray, FontSize = 12 };
                connectionInput = new TextBox
                {
                    Text = "5080",
                    Background = new SolidColorBrush(Color.Parse("#161616")),
                    Foreground = Brushes.White,
                    BorderBrush = new SolidColorBrush(Color.Parse("#2D2D2D")),
                    Padding = new Thickness(8, 4),
                    FontSize = 13
                };
                inputsStack.Children.Add(portLabel);
                inputsStack.Children.Add(connectionInput);
            }
            else
            {
                var ipLabel = new TextBlock { Text = "Host IP : Port:", Foreground = Brushes.Gray, FontSize = 12 };
                connectionInput = new TextBox
                {
                    Text = "127.0.0.1:5080",
                    Background = new SolidColorBrush(Color.Parse("#161616")),
                    Foreground = Brushes.White,
                    BorderBrush = new SolidColorBrush(Color.Parse("#2D2D2D")),
                    Padding = new Thickness(8, 4),
                    FontSize = 13
                };
                inputsStack.Children.Add(ipLabel);
                inputsStack.Children.Add(connectionInput);
            }

            mainGrid.Children.Add(inputsStack);
            Grid.SetRow(inputsStack, 1);

            // Footer Buttons
            var buttonStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 12
            };

            var btnSubmit = new Button
            {
                Content = isHost ? "Host" : "Join",
                Width = 80,
                Height = 30,
                Background = new SolidColorBrush(Color.Parse("#7C70F2")),
                Foreground = Brushes.White,
                FontWeight = FontWeight.Medium,
                CornerRadius = new CornerRadius(4),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            btnSubmit.PointerEntered += (s, e) => btnSubmit.Background = new SolidColorBrush(Color.Parse("#8E82FF"));
            btnSubmit.PointerExited += (s, e) => btnSubmit.Background = new SolidColorBrush(Color.Parse("#7C70F2"));

            var btnCancel = new Button
            {
                Content = "Cancel",
                Width = 80,
                Height = 30,
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

            btnSubmit.Click += (s, e) =>
            {
                Username = usernameInput.Text ?? "Anonymous";
                string connVal = connectionInput.Text ?? "";
                if (isHost)
                {
                    if (int.TryParse(connVal, out int parsedPort))
                        Port = parsedPort;
                }
                else
                {
                    int colonIdx = connVal.IndexOf(':');
                    if (colonIdx > 0)
                    {
                        HostIp = connVal.Substring(0, colonIdx);
                        if (int.TryParse(connVal.Substring(colonIdx + 1), out int parsedPort))
                            Port = parsedPort;
                    }
                    else
                    {
                        HostIp = connVal;
                    }
                }
                IsCancelled = false;
                Close();
            };

            btnCancel.Click += (s, e) => Close();

            buttonStack.Children.Add(btnSubmit);
            buttonStack.Children.Add(btnCancel);

            mainGrid.Children.Add(buttonStack);
            Grid.SetRow(buttonStack, 2);

            mainBorder.Child = mainGrid;
            Content = mainBorder;

            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    Username = usernameInput.Text ?? "Anonymous";
                    string connVal = connectionInput.Text ?? "";
                    if (isHost)
                    {
                        if (int.TryParse(connVal, out int parsedPort))
                            Port = parsedPort;
                    }
                    else
                    {
                        int colonIdx = connVal.IndexOf(':');
                        if (colonIdx > 0)
                        {
                            HostIp = connVal.Substring(0, colonIdx);
                            if (int.TryParse(connVal.Substring(colonIdx + 1), out int parsedPort))
                                Port = parsedPort;
                        }
                        else
                        {
                            HostIp = connVal;
                        }
                    }
                    IsCancelled = false;
                    Close();
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    Close();
                    e.Handled = true;
                }
            };
        }
    }
}
