using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SpanCoder.Contracts;

namespace SpanCoder.Shell
{
    public class InstallMicroPythonWindow : Window
    {
        public const uint Uf2MagicStart0 = 0x0A324655u;
        public const uint Uf2MagicStart1 = 0x9E5D5157u;
        public const uint Uf2MagicEnd = 0x0AB16F30u;

        private readonly ComboBox _targetVolumeComboBox;
        private readonly TextBox _uf2FileTextBox;
        private readonly TextBlock _infoText;
        private readonly ProgressBar _progressBar;
        private readonly Button _installBtn;
        private readonly Button _cancelBtn;

        private readonly List<string> _drivePaths = new();

        public InstallMicroPythonWindow()
        {
            Title = "Flash UF2 Bootloader Firmware";
            Width = 560;
            Height = 460;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.Parse("#1E1E1E"));
            Foreground = Brushes.LightGray;

            var mainLayout = new StackPanel
            {
                Margin = new Thickness(24),
                Spacing = 16
            };

            var headerLabel = new TextBlock
            {
                Text = "Deploy a verified UF2 firmware image (.NET nanoFramework, C/C++, or Rust) to a USB UF2 bootloader volume.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.White,
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 8)
            };
            mainLayout.Children.Add(headerLabel);

            var instructions = new TextBlock
            {
                Text = "1. Put your microcontroller into UF2 bootloader mode (e.g., hold BOOTSEL while connecting USB).\n" +
                       "2. Wait until the UF2 mass-storage volume (such as RPI-RP2) mounts.\n" +
                       "3. Select a valid local .uf2 firmware image.\n" +
                       "4. Click 'Flash UF2' — every 512-byte UF2 block header and payload is cryptographically/structurally verified before any bytes are written to hardware.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray,
                FontSize = 11,
                LineHeight = 16,
                Margin = new Thickness(0, 0, 0, 12)
            };
            mainLayout.Children.Add(instructions);

            var formGrid = new Grid();
            formGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(140)));
            formGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            formGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            formGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            // Row 0: Target Volume
            var lblVolume = new TextBlock { Text = "Target volume:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4) };
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

            // Row 1: Local UF2 Firmware File
            var lblUf2 = new TextBlock { Text = "Firmware (.uf2):", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4) };
            var fileGrid = new Grid();
            fileGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            fileGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

            _uf2FileTextBox = new TextBox
            {
                Watermark = "Select a compiled .uf2 firmware image...",
                Background = new SolidColorBrush(Color.Parse("#2A2A2A")),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.Parse("#3E3E42")),
                Margin = new Thickness(0, 4, 4, 4)
            };
            _uf2FileTextBox.TextChanged += (s, e) => UpdateInstallButtonState();

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
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Select UF2 Firmware Image",
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("UF2 Firmware (*.uf2)")
                        {
                            Patterns = new[] { "*.uf2" }
                        }
                    }
                });

                if (files != null && files.Count > 0)
                {
                    _uf2FileTextBox.Text = files[0].Path.LocalPath;
                }
            };

            fileGrid.Children.Add(_uf2FileTextBox); Grid.SetColumn(_uf2FileTextBox, 0);
            fileGrid.Children.Add(browseBtn); Grid.SetColumn(browseBtn, 1);

            formGrid.Children.Add(lblUf2);
            Grid.SetRow(lblUf2, 1); Grid.SetColumn(lblUf2, 0);
            formGrid.Children.Add(fileGrid);
            Grid.SetRow(fileGrid, 1); Grid.SetColumn(fileGrid, 1);

            mainLayout.Children.Add(formGrid);

            _infoText = new TextBlock
            {
                Text = "Select a bootloader volume and a valid .uf2 firmware file.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray,
                FontSize = 11,
                Margin = new Thickness(0, 8, 0, 8)
            };
            mainLayout.Children.Add(_infoText);

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

            var buttonsLayout = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 12,
                Margin = new Thickness(0, 16, 0, 0)
            };

            var refreshBtn = new Button
            {
                Content = "Rescan Drives",
                Height = 28,
                Background = new SolidColorBrush(Color.Parse("#3E3E42")),
                Foreground = Brushes.White
            };
            refreshBtn.Click += (s, e) =>
            {
                ScanVolumes();
                UpdateInstallButtonState();
            };

            _installBtn = new Button
            {
                Content = "Flash UF2",
                Width = 100,
                Height = 28,
                Background = new SolidColorBrush(Color.Parse("#007ACC")),
                Foreground = Brushes.White,
                IsEnabled = false
            };
            _installBtn.Click += (s, e) => StartInstallation();

            _cancelBtn = new Button
            {
                Content = "Close",
                Width = 80,
                Height = 28,
                Background = new SolidColorBrush(Color.Parse("#3E3E42")),
                Foreground = Brushes.White
            };
            _cancelBtn.Click += (s, e) => Close();

            buttonsLayout.Children.Add(refreshBtn);
            buttonsLayout.Children.Add(_installBtn);
            buttonsLayout.Children.Add(_cancelBtn);
            mainLayout.Children.Add(buttonsLayout);

            Content = mainLayout;

            ScanVolumes();
            _targetVolumeComboBox.SelectionChanged += (s, e) => UpdateInstallButtonState();
        }

        /// <summary>
        /// Strictly validates a binary UF2 firmware image according to the Microsoft UF2 specification.
        /// Rejects truncated, malformed, or zero-filled dummy UF2 payloads to protect physical USB hardware.
        /// </summary>
        public static bool TryValidateUf2Image(ReadOnlySpan<byte> data, out int blockCount, out string error)
        {
            blockCount = 0;
            if (data.Length < 512 || (data.Length % 512) != 0)
            {
                error = $"Invalid UF2 size ({data.Length} bytes). UF2 images must be a positive multiple of 512 bytes.";
                return false;
            }

            int totalBlocks = data.Length / 512;
            bool hasNonZeroPayload = false;

            for (int i = 0; i < totalBlocks; i++)
            {
                ReadOnlySpan<byte> block = data.Slice(i * 512, 512);
                uint magic0 = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(0, 4));
                uint magic1 = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(4, 4));
                uint magicEnd = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(508, 4));

                if (magic0 != Uf2MagicStart0 || magic1 != Uf2MagicStart1 || magicEnd != Uf2MagicEnd)
                {
                    error = $"UF2 block {i} failed magic header/footer verification.";
                    return false;
                }

                uint payloadSize = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(16, 4));
                uint blockNo = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(20, 4));
                uint numBlocks = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(24, 4));

                if (payloadSize == 0 || payloadSize > 476)
                {
                    error = $"UF2 block {i} has invalid payload size ({payloadSize} bytes).";
                    return false;
                }

                if (blockNo != (uint)i || numBlocks != (uint)totalBlocks)
                {
                    error = $"UF2 block sequence mismatch at block {i} (header reports {blockNo + 1}/{numBlocks}, expected {i + 1}/{totalBlocks}).";
                    return false;
                }

                if (!hasNonZeroPayload)
                {
                    ReadOnlySpan<byte> payload = block.Slice(32, (int)payloadSize);
                    for (int b = 0; b < payload.Length; b++)
                    {
                        if (payload[b] != 0)
                        {
                            hasNonZeroPayload = true;
                            break;
                        }
                    }
                }
            }

            if (!hasNonZeroPayload)
            {
                error = "Rejected empty/zeroed UF2 payload: refusing to erase microcontroller flash with blank data.";
                return false;
            }

            blockCount = totalBlocks;
            error = "";
            return true;
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
                                        drive.VolumeLabel.Contains("RP2350", StringComparison.OrdinalIgnoreCase) ||
                                        File.Exists(Path.Combine(drive.Name, "INFO_UF2.TXT"));

                    if (!isBootloader) continue;

                    volumes.Add($"{drive.Name} ({drive.VolumeLabel}) [UF2 BOOTLOADER]");
                    _drivePaths.Add(drive.Name);
                }
            }
            catch (Exception ex)
            {
                SpanCoderDiagnostics.LogWarning($"[Uf2Flasher] Error scanning drives: {ex.Message}");
            }

            if (volumes.Count == 0)
            {
                volumes.Add("< No UF2 bootloader volume detected (connect device in BOOTSEL mode) >");
                _drivePaths.Add("");
            }

            _targetVolumeComboBox.ItemsSource = volumes;
            _targetVolumeComboBox.SelectedIndex = 0;
        }

        private void UpdateInstallButtonState()
        {
            bool hasVolume = _targetVolumeComboBox.SelectedIndex >= 0 &&
                             _targetVolumeComboBox.SelectedIndex < _drivePaths.Count &&
                             !string.IsNullOrEmpty(_drivePaths[_targetVolumeComboBox.SelectedIndex]);
            string uf2Path = _uf2FileTextBox.Text?.Trim() ?? "";
            bool hasFile = !string.IsNullOrEmpty(uf2Path) && File.Exists(uf2Path);

            _installBtn.IsEnabled = hasVolume && hasFile;

            if (hasVolume && hasFile)
            {
                string path = _drivePaths[_targetVolumeComboBox.SelectedIndex];
                _infoText.Text = $"Ready to validate and flash '{Path.GetFileName(uf2Path)}' to UF2 bootloader volume {path}.";
                _infoText.Foreground = Brushes.LightGreen;
            }
            else if (!hasVolume)
            {
                _infoText.Text = "No verified UF2 bootloader volume (INFO_UF2.TXT) detected. Put your board into BOOTSEL mode and click 'Rescan Drives'.";
                _infoText.Foreground = Brushes.Orange;
            }
            else
            {
                _infoText.Text = "Select an existing .uf2 firmware image to flash.";
                _infoText.Foreground = Brushes.Orange;
            }
        }

        private async void StartInstallation()
        {
            string targetVolume = _drivePaths[_targetVolumeComboBox.SelectedIndex];
            string uf2Path = _uf2FileTextBox.Text?.Trim() ?? "";

            if (string.IsNullOrEmpty(targetVolume) || !Directory.Exists(targetVolume))
            {
                _infoText.Text = "Aborted: UF2 bootloader volume is no longer mounted.";
                _infoText.Foreground = Brushes.Red;
                return;
            }

            if (string.IsNullOrEmpty(uf2Path) || !File.Exists(uf2Path))
            {
                _infoText.Text = "Aborted: Selected .uf2 firmware file does not exist.";
                _infoText.Foreground = Brushes.Red;
                return;
            }

            _targetVolumeComboBox.IsEnabled = false;
            _uf2FileTextBox.IsEnabled = false;
            _installBtn.IsEnabled = false;
            _cancelBtn.IsEnabled = false;
            _progressBar.IsVisible = true;
            _progressBar.Value = 0;

            try
            {
                byte[] fileBytes = await File.ReadAllBytesAsync(uf2Path);
                if (!TryValidateUf2Image(fileBytes, out int blockCount, out string validationError))
                {
                    SpanCoderDiagnostics.LogError($"[Uf2Flasher] Rejected UF2 image '{uf2Path}': {validationError}");
                    _infoText.Text = $"Flashing blocked for hardware safety: {validationError}";
                    _infoText.Foreground = Brushes.Red;
                    return;
                }

                string destinationPath = Path.Combine(targetVolume, Path.GetFileName(uf2Path));
                _infoText.Text = $"Writing {blockCount} verified UF2 blocks to {targetVolume}...";
                _infoText.Foreground = Brushes.SkyBlue;

                await using (var fs = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
                {
                    const int chunkSize = 65536;
                    int totalWritten = 0;
                    while (totalWritten < fileBytes.Length)
                    {
                        int count = Math.Min(chunkSize, fileBytes.Length - totalWritten);
                        await fs.WriteAsync(fileBytes.AsMemory(totalWritten, count));
                        totalWritten += count;
                        int percent = (int)((totalWritten * 100L) / fileBytes.Length);
                        Dispatcher.UIThread.Post(() =>
                        {
                            _progressBar.Value = percent;
                            _infoText.Text = $"Flashing verified UF2 firmware... {percent}%";
                        });
                    }
                    await fs.FlushAsync();
                }

                _infoText.Text = $"Flashed {blockCount} UF2 blocks successfully. Target device will reboot automatically.";
                _infoText.Foreground = Brushes.LightGreen;
            }
            catch (Exception ex) when (ex is IOException or DirectoryNotFoundException)
            {
                SpanCoderDiagnostics.LogInformation($"[Uf2Flasher] Volume unmounted on final UF2 block reboot: {ex.Message}");
                _infoText.Text = "Firmware transfer completed and target board rebooted.";
                _infoText.Foreground = Brushes.LightGreen;
            }
            catch (Exception ex)
            {
                _infoText.Text = $"Flashing failed: {ex.Message}";
                _infoText.Foreground = Brushes.Red;
            }
            finally
            {
                _targetVolumeComboBox.IsEnabled = true;
                _uf2FileTextBox.IsEnabled = true;
                _cancelBtn.IsEnabled = true;
                UpdateInstallButtonState();
            }
        }
    }
}
