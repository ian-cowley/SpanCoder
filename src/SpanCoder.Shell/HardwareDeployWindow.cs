using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace SpanCoder.Shell
{
    public class HardwareDeployWindow : Window
    {
        private readonly ShellWindow _shellWindow;
        private readonly ComboBox _targetBoardComboBox;
        private readonly ComboBox _probeComboBox;
        private readonly TextBox _gdbPathTextBox;
        private readonly TextBox _programTextBox;
        private readonly TextBox _targetConnTextBox;
        private readonly TextBox _deployCmdTextBox;
        private readonly TextBlock _statusText;
        private readonly Button _saveBtn;
        private readonly Button _deployBtn;

        public HardwareDeployWindow(ShellWindow shellWindow)
        {
            _shellWindow = shellWindow;

            Title = "Hardware Deploy & Debug Configuration";
            Width = 580;
            Height = 460;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.Parse("#1E1E1E"));
            Foreground = Brushes.LightGray;

            var mainLayout = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 12
            };

            var headerLabel = new TextBlock
            {
                Text = "Configure microcontroller hardware targets and deploy/flash settings.",
                Foreground = Brushes.White,
                FontSize = 14,
                FontWeight = FontWeight.Bold,
                Margin = new Thickness(0, 0, 0, 8)
            };
            mainLayout.Children.Add(headerLabel);

            // Form grid
            var formGrid = new Grid();
            formGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(140)));
            formGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            formGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // Target Board
            formGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // Probe
            formGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // GDB Path
            formGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // Program (.elf)
            formGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // Target Connection
            formGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // Deploy Command

            // 1. Target Board
            var lblBoard = new TextBlock { Text = "Target Board/Chip:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4) };
            _targetBoardComboBox = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Color.Parse("#2A2A2A")),
                Foreground = Brushes.White,
                Margin = new Thickness(0, 4)
            };
            _targetBoardComboBox.ItemsSource = new List<string> { "Raspberry Pi Pico (RP2040)", "Raspberry Pi Pico 2 (RP2350)", "STM32F4 Discovery", "ESP32 DevKit", "Custom Target" };
            _targetBoardComboBox.SelectedIndex = 0;
            _targetBoardComboBox.SelectionChanged += (s, e) => UpdateCommandPresets();

            formGrid.Children.Add(lblBoard);
            Grid.SetRow(lblBoard, 0); Grid.SetColumn(lblBoard, 0);
            formGrid.Children.Add(_targetBoardComboBox);
            Grid.SetRow(_targetBoardComboBox, 0); Grid.SetColumn(_targetBoardComboBox, 1);

            // 2. Debug Probe
            var lblProbe = new TextBlock { Text = "Debug Probe:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4) };
            _probeComboBox = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Color.Parse("#2A2A2A")),
                Foreground = Brushes.White,
                Margin = new Thickness(0, 4)
            };
            _probeComboBox.ItemsSource = new List<string> { "Raspberry Pi Debug Probe (CMSIS-DAP)", "Segger J-Link", "ST-Link v2 / v3", "ESP-Prog", "Custom Probe" };
            _probeComboBox.SelectedIndex = 0;
            _probeComboBox.SelectionChanged += (s, e) => UpdateCommandPresets();

            formGrid.Children.Add(lblProbe);
            Grid.SetRow(lblProbe, 1); Grid.SetColumn(lblProbe, 0);
            formGrid.Children.Add(_probeComboBox);
            Grid.SetRow(_probeComboBox, 1); Grid.SetColumn(_probeComboBox, 1);

            // 3. GDB Path
            var lblGdb = new TextBlock { Text = "GDB Path:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4) };
            _gdbPathTextBox = new TextBox
            {
                Text = "gdb-multiarch",
                Background = new SolidColorBrush(Color.Parse("#2A2A2A")),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.Parse("#3E3E42")),
                Margin = new Thickness(0, 4)
            };
            formGrid.Children.Add(lblGdb);
            Grid.SetRow(lblGdb, 2); Grid.SetColumn(lblGdb, 0);
            formGrid.Children.Add(_gdbPathTextBox);
            Grid.SetRow(_gdbPathTextBox, 2); Grid.SetColumn(_gdbPathTextBox, 1);

            // 4. Program (.elf)
            var lblProg = new TextBlock { Text = "Target Program (.elf):", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4) };
            var progGrid = new Grid();
            progGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            progGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

            _programTextBox = new TextBox
            {
                Text = "build/firmware.elf",
                Background = new SolidColorBrush(Color.Parse("#2A2A2A")),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.Parse("#3E3E42")),
                Margin = new Thickness(0, 4, 4, 4)
            };
            var browseBtn = new Button
            {
                Content = "Browse...",
                Background = new SolidColorBrush(Color.Parse("#3E3E42")),
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 4, 0, 4)
            };
            browseBtn.Click += async (s, e) =>
            {
                var dialog = new OpenFileDialog
                {
                    Title = "Select ELF Executable File",
                    Filters = new List<FileDialogFilter> { new FileDialogFilter { Name = "ELF Files (*.elf;*)", Extensions = new List<string> { "elf" } } }
                };
                var paths = await dialog.ShowAsync(this);
                if (paths != null && paths.Length > 0)
                {
                    string path = paths[0];
                    string? ws = _shellWindow.WorkspaceRootPath;
                    if (ws != null && path.StartsWith(ws, StringComparison.OrdinalIgnoreCase))
                    {
                        path = path.Substring(ws.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    }
                    _programTextBox.Text = path;
                }
            };
            progGrid.Children.Add(_programTextBox); Grid.SetColumn(_programTextBox, 0);
            progGrid.Children.Add(browseBtn); Grid.SetColumn(browseBtn, 1);

            formGrid.Children.Add(lblProg);
            Grid.SetRow(lblProg, 3); Grid.SetColumn(lblProg, 0);
            formGrid.Children.Add(progGrid);
            Grid.SetRow(progGrid, 3); Grid.SetColumn(progGrid, 1);

            // 5. Target Connection
            var lblConn = new TextBlock { Text = "GDB Target Port:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4) };
            _targetConnTextBox = new TextBox
            {
                Text = "localhost:3333",
                Background = new SolidColorBrush(Color.Parse("#2A2A2A")),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.Parse("#3E3E42")),
                Margin = new Thickness(0, 4)
            };
            formGrid.Children.Add(lblConn);
            Grid.SetRow(lblConn, 4); Grid.SetColumn(lblConn, 0);
            formGrid.Children.Add(_targetConnTextBox);
            Grid.SetRow(_targetConnTextBox, 4); Grid.SetColumn(_targetConnTextBox, 1);

            // 6. Deploy Command
            var lblCmd = new TextBlock { Text = "Deploy/Flash Command:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4) };
            _deployCmdTextBox = new TextBox
            {
                Text = "",
                Background = new SolidColorBrush(Color.Parse("#2A2A2A")),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.Parse("#3E3E42")),
                Margin = new Thickness(0, 4)
            };
            formGrid.Children.Add(lblCmd);
            Grid.SetRow(lblCmd, 5); Grid.SetColumn(lblCmd, 0);
            formGrid.Children.Add(_deployCmdTextBox);
            Grid.SetRow(_deployCmdTextBox, 5); Grid.SetColumn(_deployCmdTextBox, 1);

            mainLayout.Children.Add(formGrid);

            // Status message
            _statusText = new TextBlock
            {
                Text = "",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Margin = new Thickness(0, 8, 0, 8)
            };
            mainLayout.Children.Add(_statusText);

            // Buttons Bar
            var buttonsLayout = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 12,
                Margin = new Thickness(0, 16, 0, 0)
            };

            _saveBtn = new Button
            {
                Content = "Save Config",
                Width = 110,
                Height = 28,
                Background = new SolidColorBrush(Color.Parse("#007ACC")),
                Foreground = Brushes.White
            };
            _saveBtn.Click += (s, e) => SaveConfiguration();

            _deployBtn = new Button
            {
                Content = "Deploy Now",
                Width = 110,
                Height = 28,
                Background = new SolidColorBrush(Color.Parse("#3E3E42")),
                Foreground = Brushes.White
            };
            _deployBtn.Click += (s, e) => TriggerFlashDeploy();

            var cancelBtn = new Button
            {
                Content = "Close",
                Width = 80,
                Height = 28,
                Background = new SolidColorBrush(Color.Parse("#3E3E42")),
                Foreground = Brushes.White
            };
            cancelBtn.Click += (s, e) => Close();

            buttonsLayout.Children.Add(_saveBtn);
            buttonsLayout.Children.Add(_deployBtn);
            buttonsLayout.Children.Add(cancelBtn);
            mainLayout.Children.Add(buttonsLayout);

            Content = mainLayout;

            // Load presets initially
            UpdateCommandPresets();
            LoadExistingConfig();
        }

        private void UpdateCommandPresets()
        {
            if (_deployCmdTextBox == null) return;

            string board = _targetBoardComboBox.SelectedItem as string ?? "";
            string probe = _probeComboBox.SelectedItem as string ?? "";

            string interfaceFile = "cmsis-dap.cfg";
            if (probe.Contains("J-Link")) interfaceFile = "jlink.cfg";
            else if (probe.Contains("ST-Link")) interfaceFile = "stlink.cfg";

            string targetFile = "rp2040.cfg";
            if (board.Contains("Pico 2") || board.Contains("RP2350")) targetFile = "rp2350.cfg";
            else if (board.Contains("STM32")) targetFile = "stm32f4x.cfg";
            else if (board.Contains("ESP32")) targetFile = "esp32.cfg";

            string prog = _programTextBox?.Text ?? "build/firmware.elf";
            _deployCmdTextBox.Text = $"openocd -f interface/{interfaceFile} -f target/{targetFile} -c \"program {prog} verify reset exit\"";
        }

        private void LoadExistingConfig()
        {
            string? ws = _shellWindow.WorkspaceRootPath;
            if (string.IsNullOrEmpty(ws))
            {
                _statusText.Text = "No active workspace/folder opened. Configuration cannot be saved.";
                _statusText.Foreground = Brushes.Orange;
                _saveBtn.IsEnabled = false;
                return;
            }

            string configPath = Path.Combine(ws, "spancoder_debug.json");
            if (File.Exists(configPath))
            {
                try
                {
                    string json = File.ReadAllText(configPath);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("gdbPath", out var gdbProp)) _gdbPathTextBox.Text = gdbProp.GetString();
                    if (root.TryGetProperty("program", out var progProp)) _programTextBox.Text = progProp.GetString();
                    if (root.TryGetProperty("target", out var targetProp)) _targetConnTextBox.Text = targetProp.GetString();
                    if (root.TryGetProperty("deployCmd", out var deployProp)) _deployCmdTextBox.Text = deployProp.GetString();

                    _statusText.Text = "Configuration loaded from spancoder_debug.json.";
                    _statusText.Foreground = Brushes.LightGreen;
                }
                catch (Exception ex)
                {
                    _statusText.Text = $"Error reading spancoder_debug.json: {ex.Message}";
                    _statusText.Foreground = Brushes.Red;
                }
            }
        }

        private void SaveConfiguration()
        {
            string? ws = _shellWindow.WorkspaceRootPath;
            if (string.IsNullOrEmpty(ws)) return;

            string configPath = Path.Combine(ws, "spancoder_debug.json");

            try
            {
                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "silicon");
                    writer.WriteString("gdbPath", _gdbPathTextBox.Text ?? "gdb-multiarch");
                    writer.WriteString("program", _programTextBox.Text ?? "build/firmware.elf");
                    writer.WriteString("target", _targetConnTextBox.Text ?? "localhost:3333");
                    writer.WriteString("deployCmd", _deployCmdTextBox.Text ?? "");
                    
                    writer.WriteStartArray("autorun");
                    writer.WriteStringValue($"target remote {_targetConnTextBox.Text}");
                    writer.WriteStringValue("load");
                    writer.WriteStringValue("monitor reset halt");
                    writer.WriteEndArray();

                    writer.WriteEndObject();
                }

                File.WriteAllText(configPath, System.Text.Encoding.UTF8.GetString(stream.ToArray()));
                _statusText.Text = "Configuration saved successfully to spancoder_debug.json.";
                _statusText.Foreground = Brushes.Green;
            }
            catch (Exception ex)
            {
                _statusText.Text = $"Failed to save configuration: {ex.Message}";
                _statusText.Foreground = Brushes.Red;
            }
        }

        private void TriggerFlashDeploy()
        {
            string cmd = _deployCmdTextBox.Text ?? "";
            if (string.IsNullOrEmpty(cmd)) return;

            _statusText.Text = "Starting deployment command...";
            _statusText.Foreground = Brushes.SkyBlue;

            _shellWindow.RunHardwareDeploymentCommand(cmd);
        }
    }
}
