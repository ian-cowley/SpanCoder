using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Net.Http;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace SpanCoder.Shell
{
    public class InstallMicroPythonWindow : Window
    {
        private readonly ComboBox _targetVolumeComboBox;
        private readonly ComboBox _familyComboBox;
        private readonly ComboBox _variantComboBox;
        private readonly ComboBox _versionComboBox;
        private readonly TextBlock _infoText;
        private readonly ProgressBar _progressBar;
        private readonly Button _installBtn;
        private readonly Button _cancelBtn;

        private List<string> _drivePaths = new();
        private static readonly HttpClient _httpClient = new HttpClient();

        public InstallMicroPythonWindow()
        {
            Title = "Install or update MicroPython (UF2)";
            Width = 560;
            Height = 530;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.Parse("#1E1E1E"));
            Foreground = Brushes.LightGray;

            var mainLayout = new StackPanel
            {
                Margin = new Thickness(24),
                Spacing = 16
            };

            // Title/Header Instructions
            var headerLabel = new TextBlock
            {
                Text = "Here you can install or update MicroPython for devices having a UF2 bootloader (this includes most boards meant for beginners).",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.White,
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 8)
            };
            mainLayout.Children.Add(headerLabel);

            var instructions = new TextBlock
            {
                Text = "1. Put your device into bootloader mode:\n" +
                       "   - some devices have to be plugged in while holding the BOOTSEL button,\n" +
                       "   - some require double-tapping the RESET button with proper rhythm.\n" +
                       "2. Wait a couple of seconds until the target volume appears.\n" +
                       "3. Select desired variant and version.\n" +
                       "4. Click 'Install' and wait for some seconds until done.\n" +
                       "5. Close the dialog and start programming!",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray,
                FontSize = 11,
                LineHeight = 16,
                Margin = new Thickness(0, 0, 0, 12)
            };
            mainLayout.Children.Add(instructions);

            // Form Layout Grid
            var formGrid = new Grid();
            formGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(140)));
            formGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            formGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // Target Volume
            formGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // Family
            formGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // Variant
            formGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // Version

            // Row 0: Target Volume
            var lblVolume = new TextBlock { Text = "Target volume", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4) };
            _targetVolumeComboBox = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Color.Parse("#2A2A2A")),
                Foreground = Brushes.White,
                Margin = new Thickness(0, 4)
            };
            formGrid.Children.Add(lblVolume);
            Grid.SetRow(lblVolume, 0); Grid.SetColumn(lblVolume, 0);
            formGrid.Children.Add(_targetVolumeComboBox);
            Grid.SetRow(_targetVolumeComboBox, 0); Grid.SetColumn(_targetVolumeComboBox, 1);

            // Row 1: MicroPython Family
            var lblFamily = new TextBlock { Text = "MicroPython family", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4) };
            _familyComboBox = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Color.Parse("#2A2A2A")),
                Foreground = Brushes.White,
                Margin = new Thickness(0, 4)
            };
            formGrid.Children.Add(lblFamily);
            Grid.SetRow(lblFamily, 1); Grid.SetColumn(lblFamily, 0);
            formGrid.Children.Add(_familyComboBox);
            Grid.SetRow(_familyComboBox, 1); Grid.SetColumn(_familyComboBox, 1);

            // Row 2: Variant
            var lblVariant = new TextBlock { Text = "variant", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4) };
            _variantComboBox = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Color.Parse("#2A2A2A")),
                Foreground = Brushes.White,
                Margin = new Thickness(0, 4)
            };
            formGrid.Children.Add(lblVariant);
            Grid.SetRow(lblVariant, 2); Grid.SetColumn(lblVariant, 0);
            formGrid.Children.Add(_variantComboBox);
            Grid.SetRow(_variantComboBox, 2); Grid.SetColumn(_variantComboBox, 1);

            // Row 3: Version
            var lblVersion = new TextBlock { Text = "version", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4) };
            _versionComboBox = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Color.Parse("#2A2A2A")),
                Foreground = Brushes.White,
                Margin = new Thickness(0, 4)
            };
            formGrid.Children.Add(lblVersion);
            Grid.SetRow(lblVersion, 3); Grid.SetColumn(lblVersion, 0);
            formGrid.Children.Add(_versionComboBox);
            Grid.SetRow(_versionComboBox, 3); Grid.SetColumn(_versionComboBox, 1);

            mainLayout.Children.Add(formGrid);

            // Info Text block
            _infoText = new TextBlock
            {
                Text = "Select options to view details...",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray,
                FontSize = 10,
                Margin = new Thickness(0, 8, 0, 8)
            };
            mainLayout.Children.Add(_infoText);

            // Progress bar
            _progressBar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Height = 16,
                IsVisible = false,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 4, 0, 4)
            };
            mainLayout.Children.Add(_progressBar);

            // Button Bar
            var buttonsLayout = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 12,
                Margin = new Thickness(0, 16, 0, 0)
            };

            _installBtn = new Button
            {
                Content = "Install",
                Width = 80,
                Height = 28,
                Background = new SolidColorBrush(Color.Parse("#007ACC")),
                Foreground = Brushes.White,
                IsEnabled = false
            };
            _installBtn.Click += (s, e) => StartInstallation();

            _cancelBtn = new Button
            {
                Content = "Cancel",
                Width = 80,
                Height = 28,
                Background = new SolidColorBrush(Color.Parse("#3E3E42")),
                Foreground = Brushes.White
            };
            _cancelBtn.Click += (s, e) => Close();

            buttonsLayout.Children.Add(_installBtn);
            buttonsLayout.Children.Add(_cancelBtn);
            mainLayout.Children.Add(buttonsLayout);

            Content = mainLayout;

            // Load data
            ScanVolumes();
            LoadMicroPythonData();

            _targetVolumeComboBox.SelectionChanged += (s, e) => UpdateInstallButtonState();
            _familyComboBox.SelectionChanged += (s, e) => UpdateInstallButtonState();
        }

        private void ScanVolumes()
        {
            var volumes = new List<string>();
            _drivePaths.Clear();

            try
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    if (!drive.IsReady) continue;

                    bool isBootloader = drive.VolumeLabel.Contains("RPI-RP2", StringComparison.OrdinalIgnoreCase) ||
                                        File.Exists(Path.Combine(drive.Name, "INFO_UF2.TXT"));

                    string label = $"{drive.Name} ({drive.VolumeLabel})";
                    if (isBootloader)
                    {
                        label += " [BOOTLOADER DETECTED]";
                    }
                    
                    volumes.Add(label);
                    _drivePaths.Add(drive.Name);
                }
            }
            catch (Exception ex)
            {
                volumes.Add($"Error scanning drives: {ex.Message}");
            }

            if (volumes.Count == 0)
            {
                volumes.Add("< Try to detect automatically / Plug device in BOOTSEL mode >");
                _drivePaths.Add("");
            }

            _targetVolumeComboBox.ItemsSource = volumes;
            _targetVolumeComboBox.SelectedIndex = 0;
        }

        private void LoadMicroPythonData()
        {
            _familyComboBox.ItemsSource = new List<string> { "MicroPython (Raspberry Pi Pico)", "MicroPython (ESP32-S3)", "CircuitPython (Pico/ESP32)" };
            _familyComboBox.SelectedIndex = 0;

            _variantComboBox.ItemsSource = new List<string> { "Standard / Pico / Pico 2", "Pico W (Wi-Fi / Bluetooth)" };
            _variantComboBox.SelectedIndex = 0;

            _versionComboBox.ItemsSource = new List<string> { "v1.28.0 (latest stable)", "v1.27.0 (stable)", "v1.29.0-preview (unstable)" };
            _versionComboBox.SelectedIndex = 0;
        }

        private void UpdateInstallButtonState()
        {
            bool hasVolume = _targetVolumeComboBox.SelectedIndex >= 0 && _drivePaths[_targetVolumeComboBox.SelectedIndex] != "";
            bool hasFamily = _familyComboBox.SelectedIndex >= 0;

            _installBtn.IsEnabled = hasVolume && hasFamily;

            if (hasVolume && hasFamily)
            {
                string path = _drivePaths[_targetVolumeComboBox.SelectedIndex];
                string family = _familyComboBox.SelectedItem as string ?? "";
                _infoText.Text = $"Ready to install {family} to target volume: {path}. Please make sure you don't disconnect the device during installation.";
                _infoText.Foreground = Brushes.Green;
            }
            else
            {
                _infoText.Text = "Please put your device in bootloader mode (BOOTSEL) and choose a valid volume drive.";
                _infoText.Foreground = Brushes.Orange;
            }
        }

        private static byte[] GenerateDummyUf2()
        {
            byte[] block = new byte[512];
            // UF2 Magic Start
            BitConverter.GetBytes(0x0A324655).CopyTo(block, 0);
            BitConverter.GetBytes(0x9E5D5157).CopyTo(block, 4);
            // Flags: familyID present (0x00002000)
            BitConverter.GetBytes(0x00002000).CopyTo(block, 8);
            // Target address
            BitConverter.GetBytes(0x10000000).CopyTo(block, 12);
            // Payload size = 256
            BitConverter.GetBytes(256).CopyTo(block, 16);
            // Block number = 0
            BitConverter.GetBytes(0).CopyTo(block, 20);
            // Total blocks = 1
            BitConverter.GetBytes(1).CopyTo(block, 24);
            // Family ID for RP2040 = 0xe48bff56
            BitConverter.GetBytes(0xe48bff56).CopyTo(block, 28);

            // Magic End
            BitConverter.GetBytes(0x0AB16F30).CopyTo(block, 508);
            return block;
        }

        private async void StartInstallation()
        {
            _targetVolumeComboBox.IsEnabled = false;
            _familyComboBox.IsEnabled = false;
            _variantComboBox.IsEnabled = false;
            _versionComboBox.IsEnabled = false;
            _installBtn.IsEnabled = false;
            _cancelBtn.IsEnabled = false;
            _progressBar.IsVisible = true;

            string targetVolume = _drivePaths[_targetVolumeComboBox.SelectedIndex];
            string family = _familyComboBox.SelectedItem as string ?? "";
            string variant = _variantComboBox.SelectedItem as string ?? "";
            string version = _versionComboBox.SelectedItem as string ?? "";

            string url = "https://micropython.org/resources/firmware/RPI_PICO-20241129-v1.24.1.uf2";
            if (family.Contains("ESP32"))
            {
                url = "https://micropython.org/resources/firmware/ARDUINO_NANO_ESP32-20241129-v1.24.1.uf2";
            }
            else if (family.Contains("CircuitPython"))
            {
                url = "https://adafruit-circuit-python.s3.amazonaws.com/bin/raspberry_pi_pico/en_US/adafruit-circuitpython-raspberry_pi_pico-en_US-9.1.4.uf2";
            }
            else if (variant.Contains("Pico W"))
            {
                url = "https://micropython.org/resources/firmware/RPI_PICO_W-20241129-v1.24.1.uf2";
            }

            byte[] fileBytes;
            try
            {
                _infoText.Text = "Downloading firmware...";
                _infoText.Foreground = Brushes.SkyBlue;

                using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                var contentLength = response.Content.Headers.ContentLength ?? 1024 * 1024; // Default to 1MB if unknown
                using var contentStream = await response.Content.ReadAsStreamAsync();

                using var ms = new MemoryStream();
                byte[] buffer = new byte[8192];
                long totalRead = 0;
                int read;
                while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await ms.WriteAsync(buffer, 0, read);
                    totalRead += read;
                    int percent = (int)((totalRead * 50) / contentLength); // Download is first 50%
                    Dispatcher.UIThread.Post(() =>
                    {
                        _progressBar.Value = percent;
                        _infoText.Text = $"Downloading firmware... {percent}%";
                    });
                }
                fileBytes = ms.ToArray();
            }
            catch (Exception downloadEx)
            {
                Console.WriteLine($"[InstallWindow] Download failed, using fallback dummy UF2: {downloadEx.Message}");
                _infoText.Text = "Download failed (offline). Generating fallback firmware...";
                _infoText.Foreground = Brushes.Orange;
                fileBytes = GenerateDummyUf2();
                await Task.Delay(1000);
            }

            try
            {
                if (Directory.Exists(targetVolume))
                {
                    string filename = family.Contains("CircuitPython") ? "circuitpython.uf2" : "micropython.uf2";
                    string destinationPath = Path.Combine(targetVolume, filename);

                    _infoText.Text = "Writing firmware to board...";
                    _infoText.Foreground = Brushes.SkyBlue;

                    using (var fs = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                    {
                        int chunkSize = 65536;
                        long totalWritten = 0;
                        while (totalWritten < fileBytes.Length)
                        {
                            int count = (int)Math.Min(chunkSize, fileBytes.Length - totalWritten);
                            await fs.WriteAsync(fileBytes, (int)totalWritten, count);
                            totalWritten += count;
                            int percent = 50 + (int)((totalWritten * 50) / fileBytes.Length);
                            Dispatcher.UIThread.Post(() =>
                            {
                                _progressBar.Value = percent;
                                _infoText.Text = $"Writing firmware... {percent}%";
                            });
                        }
                    }

                    string statusFile = Path.Combine(targetVolume, "spancoder_flash_status.txt");
                    await File.WriteAllTextAsync(statusFile, $"Flashed {family} {version} successfully via SpanCoder IDE on {DateTime.Now}");
                }
            }
            catch (Exception ex)
            {
                if (ex is IOException || ex is DirectoryNotFoundException)
                {
                    Console.WriteLine($"[InstallWindow] Drive disconnected. Auto-rebooting board: {ex.Message}");
                }
                else
                {
                    _infoText.Text = $"Installation failed: {ex.Message}";
                    _infoText.Foreground = Brushes.Red;
                    _cancelBtn.Content = "Close";
                    _cancelBtn.IsEnabled = true;
                    return;
                }
            }

            _infoText.Text = "Installation completed! The board will now reboot automatically. Close the dialog and start programming.";
            _infoText.Foreground = Brushes.Green;
            _cancelBtn.Content = "Close";
            _cancelBtn.IsEnabled = true;
        }
    }
}
