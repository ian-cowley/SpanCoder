using System;
using System.IO.Ports;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace SpanCoder.Shell
{
    public class SerialReplControl : Grid
    {
        private readonly ComboBox _portComboBox;
        private readonly ComboBox _baudComboBox;
        private readonly Button _connectBtn;
        private readonly Button _stopBtn;
        private readonly Button _rebootBtn;
        private readonly Button _flashBtn;
        private readonly TextBox _consoleTextBox;

        private SerialPort? _serialPort;
        private bool _isConnected;
        private CancellationTokenSource? _readCts;

        public SerialReplControl()
        {
            RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // Toolbar
            RowDefinitions.Add(new RowDefinition(GridLength.Star)); // Console

            Background = new SolidColorBrush(Color.Parse("#1A1A1A"));

            // Toolbar Panel
            var toolbarPanel = new Border
            {
                BorderBrush = new SolidColorBrush(Color.Parse("#2D2D2D")),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(8),
                Background = new SolidColorBrush(Color.Parse("#252526"))
            };

            var toolbarStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                VerticalAlignment = VerticalAlignment.Center
            };

            // Interpreter settings
            var lblPort = new TextBlock { Text = "Port:", Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
            _portComboBox = new ComboBox
            {
                Width = 240,
                Background = new SolidColorBrush(Color.Parse("#1E1E1E")),
                Foreground = Brushes.White,
                FontSize = 12
            };
            _portComboBox.DropDownOpened += (s, e) => RefreshPortNames();

            var refreshBtn = new Button
            {
                Content = "↻",
                Background = new SolidColorBrush(Color.Parse("#3E3E42")),
                Foreground = Brushes.White,
                FontSize = 12,
                Padding = new Thickness(8, 4),
                VerticalAlignment = VerticalAlignment.Center
            };
            refreshBtn.Click += (s, e) => RefreshPortNames();
            
            var lblBaud = new TextBlock { Text = "Baud:", Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
            _baudComboBox = new ComboBox
            {
                Width = 100,
                Background = new SolidColorBrush(Color.Parse("#1E1E1E")),
                Foreground = Brushes.White,
                FontSize = 12
            };

            _connectBtn = new Button
            {
                Content = "Connect",
                Background = new SolidColorBrush(Color.Parse("#3E3E42")),
                Foreground = Brushes.White,
                FontSize = 12,
                Padding = new Thickness(12, 4)
            };
            _connectBtn.Click += (s, e) => ToggleConnection();

            // Interruption buttons
            _stopBtn = new Button
            {
                Content = "Stop (Ctrl+C)",
                Background = new SolidColorBrush(Color.Parse("#CC2929")),
                Foreground = Brushes.White,
                FontSize = 12,
                Padding = new Thickness(12, 4),
                IsEnabled = false
            };
            _stopBtn.Click += (s, e) => SendStopSignal();

            _rebootBtn = new Button
            {
                Content = "Reboot (Ctrl+D)",
                Background = new SolidColorBrush(Color.Parse("#297ACC")),
                Foreground = Brushes.White,
                FontSize = 12,
                Padding = new Thickness(12, 4),
                IsEnabled = false
            };
            _rebootBtn.Click += (s, e) => SendRebootSignal();

            _flashBtn = new Button
            {
                Content = "Install MicroPython",
                Background = new SolidColorBrush(Color.Parse("#3E3E42")),
                Foreground = Brushes.White,
                FontSize = 12,
                Padding = new Thickness(12, 4)
            };
            _flashBtn.Click += (s, e) => ShowFlasherDialog();

            toolbarStack.Children.Add(lblPort);
            toolbarStack.Children.Add(_portComboBox);
            toolbarStack.Children.Add(refreshBtn);
            toolbarStack.Children.Add(lblBaud);
            toolbarStack.Children.Add(_baudComboBox);
            toolbarStack.Children.Add(_connectBtn);
            toolbarStack.Children.Add(_stopBtn);
            toolbarStack.Children.Add(_rebootBtn);
            toolbarStack.Children.Add(_flashBtn);
            toolbarPanel.Child = toolbarStack;

            Children.Add(toolbarPanel);
            Grid.SetRow(toolbarPanel, 0);

            // Console TextBox (styled like a terminal)
            _consoleTextBox = new TextBox
            {
                FontFamily = new FontFamily("Consolas, Courier New, monospace"),
                FontSize = 13,
                Background = new SolidColorBrush(Color.Parse("#1E1E1E")),
                Foreground = new SolidColorBrush(Color.Parse("#CCCCCC")),
                BorderThickness = new Thickness(0),
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                IsReadOnly = true, // Intercept keys to pipe to SerialPort
                Padding = new Thickness(12)
            };

            // Hook input events
            _consoleTextBox.KeyDown += OnConsoleKeyDown;
            _consoleTextBox.TextInput += OnConsoleTextInput;

            Children.Add(_consoleTextBox);
            Grid.SetRow(_consoleTextBox, 1);

            // Populate data
            RefreshPortNames();
            _baudComboBox.ItemsSource = new List<string> { "115200", "9600", "57600", "38400" };
            _baudComboBox.SelectedIndex = 0;

            AppendStatusText("SpanCoder Microcontroller Serial & REPL Console.\nChoose a port and click Connect, or select '< Try to detect port automatically >' to search.\n\n");
        }

        public void RefreshPortNames()
        {
            string? selected = _portComboBox.SelectedItem as string;
            var ports = new List<string> { "< Try to detect port automatically >" };
            try
            {
                ports.AddRange(SerialPort.GetPortNames());
            }
            catch { }
            _portComboBox.ItemsSource = ports;
            if (selected != null && ports.Contains(selected))
            {
                _portComboBox.SelectedItem = selected;
            }
            else
            {
                _portComboBox.SelectedIndex = 0;
            }
        }

        private void AppendStatusText(string text)
        {
            _consoleTextBox.Text += text;
            _consoleTextBox.CaretIndex = _consoleTextBox.Text.Length;
        }

        private void AppendDeviceText(string text)
        {
            // Clean up cursor backspace markers if any
            _consoleTextBox.Text += text;
            _consoleTextBox.CaretIndex = _consoleTextBox.Text.Length;
        }

        private void ShowFlasherDialog()
        {
            var window = new InstallMicroPythonWindow();
            var parent = this.VisualRoot as Window;
            if (parent != null)
            {
                window.ShowDialog(parent);
            }
            else
            {
                window.Show();
            }
        }

        private async void ToggleConnection()
        {
            if (_isConnected)
            {
                Disconnect();
            }
            else
            {
                await ConnectAsync();
            }
        }

        private async Task ConnectAsync()
        {
            string selectedPort = _portComboBox.SelectedItem as string ?? "";
            string selectedBaudStr = _baudComboBox.SelectedItem as string ?? "115200";
            int.TryParse(selectedBaudStr, out int baudRate);

            _connectBtn.IsEnabled = false;
            _portComboBox.IsEnabled = false;
            _baudComboBox.IsEnabled = false;
            _flashBtn.IsEnabled = false;

            if (selectedPort == "< Try to detect port automatically >")
            {
                AppendStatusText("Auto-detecting MicroPython board...\n");
                string? detected = await AutoDetectPortAsync(baudRate);
                if (detected == null)
                {
                    AppendStatusText("Couldn't find the device automatically.\nCheck the connection (making sure the device is not in bootloader mode) or choose a specific port.\n\n");
                    _connectBtn.IsEnabled = true;
                    _portComboBox.IsEnabled = true;
                    _baudComboBox.IsEnabled = true;
                    _flashBtn.IsEnabled = true;
                    return;
                }
                selectedPort = detected;
            }

            try
            {
                _serialPort = new SerialPort(selectedPort, baudRate)
                {
                    ReadTimeout = 500,
                    WriteTimeout = 500,
                    Encoding = Encoding.UTF8,
                    DtrEnable = true,
                    RtsEnable = true
                };

                _serialPort.Open();
                _isConnected = true;
                _connectBtn.Content = "Disconnect";
                _connectBtn.Background = new SolidColorBrush(Color.Parse("#CC7A00"));
                _connectBtn.IsEnabled = true;
                _stopBtn.IsEnabled = true;
                _rebootBtn.IsEnabled = true;

                _readCts = new CancellationTokenSource();
                _ = Task.Run(() => ReadSerialLoop(_readCts.Token));

                AppendStatusText($"Connected successfully to {selectedPort} @ {baudRate} baud.\n");
                
                // Send an initial return to prompt REPL
                SendString("\r\n");
            }
            catch (Exception ex)
            {
                AppendStatusText($"Failed to open port {selectedPort}: {ex.Message}\n\n");
                _connectBtn.IsEnabled = true;
                _portComboBox.IsEnabled = true;
                _baudComboBox.IsEnabled = true;
                _flashBtn.IsEnabled = true;
                _isConnected = false;
            }
        }

        private void Disconnect()
        {
            _isConnected = false;
            _readCts?.Cancel();
            
            try
            {
                if (_serialPort != null && _serialPort.IsOpen)
                {
                    _serialPort.Close();
                }
            }
            catch { }

            _serialPort = null;
            _connectBtn.Content = "Connect";
            _connectBtn.Background = new SolidColorBrush(Color.Parse("#3E3E42"));
            _stopBtn.IsEnabled = false;
            _rebootBtn.IsEnabled = false;
            _portComboBox.IsEnabled = true;
            _baudComboBox.IsEnabled = true;
            _flashBtn.IsEnabled = true;

            AppendStatusText("\nDisconnected from port.\n\n");
        }

        private async Task<string?> AutoDetectPortAsync(int baudRate)
        {
            string[] ports = SerialPort.GetPortNames();
            foreach (var port in ports)
            {
                AppendStatusText($"Probing {port}...\n");
                using (var probePort = new SerialPort(port, baudRate))
                {
                    probePort.ReadTimeout = 300;
                    probePort.WriteTimeout = 300;
                    probePort.DtrEnable = true;
                    probePort.RtsEnable = true;

                    try
                    {
                        probePort.Open();
                        probePort.DiscardInBuffer();
                        probePort.DiscardOutBuffer();

                        // Send Stop (Ctrl+C) followed by Newline to enter REPL mode
                        probePort.Write(new byte[] { 3 }, 0, 1);
                        await Task.Delay(100);
                        probePort.Write("\r\n");

                        var startTime = DateTime.UtcNow;
                        var sb = new StringBuilder();
                        while ((DateTime.UtcNow - startTime).TotalMilliseconds < 800)
                        {
                            if (probePort.BytesToRead > 0)
                            {
                                string chunk = probePort.ReadExisting();
                                sb.Append(chunk);
                                string currentText = sb.ToString();
                                if (currentText.Contains(">>>") || 
                                    currentText.Contains("MicroPython", StringComparison.OrdinalIgnoreCase) || 
                                    currentText.Contains("CircuitPython", StringComparison.OrdinalIgnoreCase))
                                {
                                    AppendStatusText($"Found MicroPython board on {port}!\n");
                                    return port;
                                }
                            }
                            await Task.Delay(50);
                        }
                    }
                    catch { }
                    finally
                    {
                        try { probePort.Close(); } catch { }
                    }
                }
            }
            return null;
        }

        private async Task ReadSerialLoop(CancellationToken token)
        {
            byte[] buffer = new byte[1024];
            while (_isConnected && !token.IsCancellationRequested)
            {
                try
                {
                    if (_serialPort != null && _serialPort.IsOpen && _serialPort.BytesToRead > 0)
                    {
                        int count = _serialPort.Read(buffer, 0, buffer.Length);
                        if (count > 0)
                        {
                            string text = Encoding.UTF8.GetString(buffer, 0, count);
                            Dispatcher.UIThread.Post(() => AppendDeviceText(text));
                        }
                    }
                }
                catch (TimeoutException) { }
                catch (Exception ex)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        AppendStatusText($"\nError reading serial port: {ex.Message}\n");
                        Disconnect();
                    });
                    break;
                }
                await Task.Delay(20, token);
            }
        }

        private void SendString(string text)
        {
            if (_serialPort != null && _serialPort.IsOpen)
            {
                try
                {
                    _serialPort.Write(text);
                }
                catch { }
            }
        }

        private void SendBytes(byte[] bytes)
        {
            if (_serialPort != null && _serialPort.IsOpen)
            {
                try
                {
                    _serialPort.Write(bytes, 0, bytes.Length);
                }
                catch { }
            }
        }

        private void SendStopSignal()
        {
            AppendStatusText("\n[KeyboardInterrupt / Stop sent]\n");
            SendBytes(new byte[] { 3 }); // Ctrl+C
        }

        private void SendRebootSignal()
        {
            AppendStatusText("\n[Soft Reboot sent]\n");
            SendBytes(new byte[] { 4 }); // Ctrl+D
        }

        private void OnConsoleTextInput(object? sender, TextInputEventArgs e)
        {
            if (!_isConnected) return;
            e.Handled = true;

            if (!string.IsNullOrEmpty(e.Text))
            {
                SendString(e.Text);
            }
        }

        private void OnConsoleKeyDown(object? sender, KeyEventArgs e)
        {
            if (!_isConnected) return;

            // Handle control keys that do not trigger TextInput
            if (e.Key == Key.Back)
            {
                e.Handled = true;
                SendBytes(new byte[] { 8 }); // Backspace character
            }
            else if (e.Key == Key.Delete)
            {
                e.Handled = true;
                SendBytes(new byte[] { 127 }); // Delete character
            }
            else if (e.Key == Key.Enter)
            {
                e.Handled = true;
                SendString("\r\n");
            }
            else if (e.Key == Key.Tab)
            {
                e.Handled = true;
                SendString("\t");
            }
            else if (e.Key == Key.C && e.KeyModifiers == KeyModifiers.Control)
            {
                e.Handled = true;
                SendStopSignal();
            }
            else if (e.Key == Key.D && e.KeyModifiers == KeyModifiers.Control)
            {
                e.Handled = true;
                SendRebootSignal();
            }
        }
    }
}
