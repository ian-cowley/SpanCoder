using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SpanCoder.Contracts;

namespace SpanCoder.Shell
{
    public class SettingsWindow : Window
    {
        private readonly ListBox _categoryList;
        private readonly StackPanel _settingsContainer;
        private readonly TextBox _searchBox;
        private readonly Grid _mainGrid;
        private readonly ShellWindow? _shellWindow;

        public SettingsWindow(ShellWindow? shellWindow = null)
        {
            _shellWindow = shellWindow;
            Title = "Settings";
            Width = 650;
            Height = 500;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.Parse("#1E1E1E"));
            Foreground = Brushes.LightGray;

            var mainGrid = new Grid();
            _mainGrid = mainGrid;
            mainGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // Search bar
            mainGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star)); // Main split

            // Search Bar
            var searchPanel = new Border
            {
                BorderBrush = new SolidColorBrush(Color.Parse("#2D2D2D")),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(12),
                Background = new SolidColorBrush(Color.Parse("#252526"))
            };
            
            _searchBox = new TextBox
            {
                Watermark = "Search settings...",
                Background = new SolidColorBrush(Color.Parse("#1A1A1A")),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.Parse("#3E3E42")),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 4),
                FontSize = 13
            };
            _searchBox.KeyUp += (s, e) => RefreshSettingsList();
            searchPanel.Child = _searchBox;
            mainGrid.Children.Add(searchPanel);
            Grid.SetRow(searchPanel, 0);

            // Category & Content split
            var contentGrid = new Grid();
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(160))); // Sidebar
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto)); // Split border
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star)); // Content panel

            // Sidebar list
            _categoryList = new ListBox
            {
                Background = new SolidColorBrush(Color.Parse("#252526")),
                Foreground = Brushes.LightGray,
                BorderThickness = new Thickness(0)
            };
            _categoryList.SelectionChanged += (s, e) => RefreshSettingsList();
            contentGrid.Children.Add(_categoryList);
            Grid.SetColumn(_categoryList, 0);

            var borderLine = new Border
            {
                Width = 1,
                Background = new SolidColorBrush(Color.Parse("#2D2D2D"))
            };
            contentGrid.Children.Add(borderLine);
            Grid.SetColumn(borderLine, 1);

            // Scrollable Settings container
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Padding = new Thickness(16)
            };
            _settingsContainer = new StackPanel { Spacing = 16 };
            scroll.Content = _settingsContainer;
            contentGrid.Children.Add(scroll);
            Grid.SetColumn(scroll, 2);

            mainGrid.Children.Add(contentGrid);
            Grid.SetRow(contentGrid, 1);

            Content = mainGrid;

            // Load categories
            LoadCategories();
        }

        private void LoadCategories()
        {
            var categories = new List<string> { "All", "Text Editor", "Workbench", "AI Settings", "Extensions", "Keyboard Shortcuts", "Hardware Debugging" };
            _categoryList.ItemsSource = categories;
            _categoryList.SelectedIndex = 0;
        }

        private void RefreshSettingsList()
        {
            _settingsContainer.Children.Clear();
            string selectedCat = _categoryList.SelectedItem as string ?? "All";
            string query = _searchBox.Text ?? "";

            if (selectedCat == "Keyboard Shortcuts")
            {
                RenderKeyboardShortcuts(query);
                return;
            }

            if (selectedCat == "Hardware Debugging")
            {
                RenderHardwareDebuggingSettings();
                return;
            }

            var descriptors = SettingsManager.GetDescriptors();
            foreach (var desc in descriptors)
            {
                // Filter by category
                if (selectedCat == "Text Editor" && !desc.Id.StartsWith("editor.", StringComparison.OrdinalIgnoreCase)) continue;
                if (selectedCat == "Workbench" && !desc.Id.StartsWith("workbench.", StringComparison.OrdinalIgnoreCase)) continue;
                if (selectedCat == "AI Settings" && !desc.Id.StartsWith("ai.", StringComparison.OrdinalIgnoreCase)) continue;
                if (selectedCat == "Extensions" && (desc.Id.StartsWith("editor.", StringComparison.OrdinalIgnoreCase) || desc.Id.StartsWith("workbench.", StringComparison.OrdinalIgnoreCase) || desc.Id.StartsWith("liveUnitTesting.", StringComparison.OrdinalIgnoreCase) || desc.Id.StartsWith("ai.", StringComparison.OrdinalIgnoreCase))) continue;

                // Filter by query
                if (!string.IsNullOrEmpty(query))
                {
                    bool match = desc.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                 desc.Id.Contains(query, StringComparison.OrdinalIgnoreCase);
                    if (!match) continue;
                }

                // Render setting control
                var settingPanel = new StackPanel { Spacing = 4 };
                
                var label = new TextBlock
                {
                    Text = desc.DisplayName,
                    Foreground = Brushes.White,
                    FontWeight = FontWeight.Bold,
                    FontSize = 13
                };
                settingPanel.Children.Add(label);

                var descLabel = new TextBlock
                {
                    Text = desc.Id,
                    Foreground = Brushes.Gray,
                    FontSize = 10
                };
                settingPanel.Children.Add(descLabel);

                string currentVal = SettingsManager.Get(desc.Id);

                if (desc.Type.Equals("boolean", StringComparison.OrdinalIgnoreCase))
                {
                    var cb = new CheckBox
                    {
                        Content = "Enabled",
                        IsChecked = currentVal.Equals("true", StringComparison.OrdinalIgnoreCase),
                        Foreground = Brushes.LightGray,
                        Margin = new Thickness(0, 4, 0, 0)
                    };
                    cb.IsCheckedChanged += (s, e) =>
                    {
                        SettingsManager.Set(desc.Id, cb.IsChecked == true ? "true" : "false");
                    };
                    settingPanel.Children.Add(cb);
                }
                else
                {
                    var tb = new TextBox
                    {
                        Text = currentVal,
                        Background = new SolidColorBrush(Color.Parse("#1A1A1A")),
                        Foreground = Brushes.White,
                        BorderBrush = new SolidColorBrush(Color.Parse("#3E3E42")),
                        BorderThickness = new Thickness(1),
                        FontSize = 12,
                        Padding = new Thickness(6, 4)
                    };
                    tb.LostFocus += (s, e) =>
                    {
                        SettingsManager.Set(desc.Id, tb.Text ?? "");
                    };
                    settingPanel.Children.Add(tb);
                }

                _settingsContainer.Children.Add(settingPanel);
            }
        }

        private void RenderKeyboardShortcuts(string query)
        {
            var allCommands = new List<CommandDescriptor>();
            allCommands.AddRange(GeneratedCommandRegistry.Commands);
            if (_shellWindow != null)
            {
                allCommands.AddRange(_shellWindow.ExtensionCommands);
            }

            foreach (var cmd in allCommands)
            {
                if (!string.IsNullOrEmpty(query))
                {
                    bool match = cmd.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                 cmd.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                 cmd.DefaultShortcut.Contains(query, StringComparison.OrdinalIgnoreCase);
                    if (!match) continue;
                }

                var rowPanel = new Grid
                {
                    Margin = new Thickness(0, 4),
                    ColumnDefinitions = new ColumnDefinitions("*,Auto")
                };

                var textStack = new StackPanel { Spacing = 2 };
                
                var label = new TextBlock
                {
                    Text = cmd.DisplayName,
                    Foreground = Brushes.White,
                    FontWeight = FontWeight.Bold,
                    FontSize = 13
                };
                textStack.Children.Add(label);

                var activeShortcut = KeybindingsManager.GetShortcut(cmd.Id, cmd.DefaultShortcut);
                string displayShortcut = string.IsNullOrEmpty(activeShortcut) ? "None" : activeShortcut;
                bool isCustom = KeybindingsManager.HasCustomShortcut(cmd.Id);

                var descLabel = new TextBlock
                {
                    Text = $"{cmd.Id}  •  Keys: {displayShortcut}{(isCustom ? " (custom)" : "")}",
                    Foreground = isCustom ? Brushes.SkyBlue : Brushes.Gray,
                    FontSize = 10
                };
                textStack.Children.Add(descLabel);

                rowPanel.Children.Add(textStack);
                Grid.SetColumn(textStack, 0);

                var buttonStack = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6
                };

                var editBtn = new Button
                {
                    Content = "Edit",
                    Background = new SolidColorBrush(Color.Parse("#3E3E42")),
                    Foreground = Brushes.White,
                    FontSize = 11,
                    Padding = new Thickness(8, 2),
                    BorderThickness = new Thickness(0)
                };
                var cmdRef = cmd; // capture variable
                editBtn.Click += (s, e) => ShowKeybindingsRemapper(cmdRef);
                buttonStack.Children.Add(editBtn);

                if (isCustom)
                {
                    var resetBtn = new Button
                    {
                        Content = "Reset",
                        Background = new SolidColorBrush(Color.Parse("#3E3E42")),
                        Foreground = Brushes.LightGray,
                        FontSize = 11,
                        Padding = new Thickness(8, 2),
                        BorderThickness = new Thickness(0)
                    };
                    resetBtn.Click += (s, e) =>
                    {
                        KeybindingsManager.ResetShortcut(cmdRef.Id);
                        RefreshSettingsList();
                    };
                    buttonStack.Children.Add(resetBtn);
                }

                rowPanel.Children.Add(buttonStack);
                Grid.SetColumn(buttonStack, 1);

                _settingsContainer.Children.Add(rowPanel);
                
                // Add a divider
                _settingsContainer.Children.Add(new Border
                {
                    Height = 1,
                    Background = new SolidColorBrush(Color.Parse("#2D2D2D")),
                    Margin = new Thickness(0, 4)
                });
            }
        }

        private void ShowKeybindingsRemapper(CommandDescriptor cmd)
        {
            var overlay = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#CC121212")),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            Grid.SetRowSpan(overlay, 2);

            var card = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#252526")),
                BorderBrush = new SolidColorBrush(Color.Parse("#3E3E42")),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(24),
                CornerRadius = new CornerRadius(4),
                Width = 400,
                Height = 220,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var layout = new StackPanel { Spacing = 12 };

            var title = new TextBlock
            {
                Text = $"Remap: {cmd.DisplayName}",
                Foreground = Brushes.White,
                FontSize = 15,
                FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            layout.Children.Add(title);

            var instructions = new TextBlock
            {
                Text = "Press the key combination you want to bind, then press Enter to save. Press Esc to cancel.",
                Foreground = Brushes.LightGray,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            layout.Children.Add(instructions);

            string capturedShortcut = "";

            var keysText = new TextBlock
            {
                Text = "Press keys...",
                Foreground = Brushes.SkyBlue,
                FontSize = 18,
                FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 12, 0, 12)
            };
            layout.Children.Add(keysText);

            var warningText = new TextBlock
            {
                Text = "",
                Foreground = Brushes.Red,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 4),
                IsVisible = false
            };
            layout.Children.Add(warningText);

            card.Child = layout;
            overlay.Child = card;

            _mainGrid.Children.Add(overlay);

            bool isConfirmingConflict = false;
            CommandDescriptor? conflictingCmd = null;

            overlay.Focusable = true;
            overlay.KeyDown += (sender, ke) =>
            {
                ke.Handled = true;

                var mods = ke.KeyModifiers;
                var key = ke.Key;

                // Ignore modifier-only key presses (like pressing Ctrl alone)
                if (key == Key.LeftCtrl || key == Key.RightCtrl ||
                    key == Key.LeftShift || key == Key.RightShift ||
                    key == Key.LeftAlt || key == Key.RightAlt ||
                    key == Key.LWin || key == Key.RWin)
                {
                    return;
                }

                // If Enter is pressed, save
                if (key == Key.Enter)
                {
                    if (!string.IsNullOrEmpty(capturedShortcut))
                    {
                        if (conflictingCmd != null && !isConfirmingConflict)
                        {
                            warningText.Text = $"Already bound to '{conflictingCmd.Value.DisplayName}'. Press Enter again to overwrite.";
                            warningText.IsVisible = true;
                            isConfirmingConflict = true;
                            return;
                        }

                        if (isConfirmingConflict && conflictingCmd != null)
                        {
                            // Overwrite conflict
                            KeybindingsManager.SetShortcut(conflictingCmd.Value.Id, "");
                        }

                        KeybindingsManager.SetShortcut(cmd.Id, capturedShortcut);
                    }
                    _mainGrid.Children.Remove(overlay);
                    RefreshSettingsList();
                    return;
                }

                // If Escape is pressed, cancel
                if (key == Key.Escape)
                {
                    _mainGrid.Children.Remove(overlay);
                    return;
                }

                // Reset conflict state on new keypress
                isConfirmingConflict = false;
                conflictingCmd = null;
                warningText.IsVisible = false;

                // Build gesture string
                var parts = new List<string>();
                if (mods.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
                if (mods.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
                if (mods.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
                if (mods.HasFlag(KeyModifiers.Meta)) parts.Add("Windows");

                parts.Add(GetKeyName(key));
                capturedShortcut = string.Join("+", parts);
                keysText.Text = capturedShortcut;

                // Check conflict
                conflictingCmd = CheckConflict(cmd.Id, capturedShortcut);
                if (conflictingCmd != null)
                {
                    warningText.Text = $"Conflicts with '{conflictingCmd.Value.DisplayName}'. Press Enter to overwrite.";
                    warningText.IsVisible = true;
                }
            };

            // Focus the overlay so it receives KeyDown events immediately
            Avalonia.Threading.Dispatcher.UIThread.Post(() => overlay.Focus(), Avalonia.Threading.DispatcherPriority.Background);
        }

        private CommandDescriptor? CheckConflict(string currentCommandId, string shortcut)
        {
            var allCommands = new List<CommandDescriptor>();
            allCommands.AddRange(GeneratedCommandRegistry.Commands);
            if (_shellWindow != null)
            {
                allCommands.AddRange(_shellWindow.ExtensionCommands);
            }

            foreach (var cmd in allCommands)
            {
                if (cmd.Id == currentCommandId) continue;
                string existing = KeybindingsManager.GetShortcut(cmd.Id, cmd.DefaultShortcut);
                if (string.Equals(existing, shortcut, StringComparison.OrdinalIgnoreCase))
                {
                    return cmd;
                }
            }
            return null;
        }

        private string GetKeyName(Key key)
        {
            if (key >= Key.D0 && key <= Key.D9)
            {
                return (key - Key.D0).ToString();
            }
            if (key >= Key.NumPad0 && key <= Key.NumPad9)
            {
                return (key - Key.NumPad0).ToString();
            }
            switch (key)
            {
                case Key.OemPlus:
                case Key.Add:
                    return "Plus";
                case Key.OemMinus:
                case Key.Subtract:
                    return "Minus";
                default:
                    return key.ToString();
            }
        }

        private void RenderHardwareDebuggingSettings()
        {
            var header = new TextBlock
            {
                Text = "Microcontroller Hardware Debugging & Deployments",
                Foreground = Brushes.White,
                FontWeight = FontWeight.Bold,
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 8)
            };
            _settingsContainer.Children.Add(header);

            var desc = new TextBlock
            {
                Text = "SpanCoder supports target hardware firmware deployment and GDB-based silicon debugging. You can configure active probes, flash tools, and target connections directly in your workspace.",
                Foreground = Brushes.LightGray,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 16)
            };
            _settingsContainer.Children.Add(desc);

            var configBtn = new Button
            {
                Content = "Configure Deploy & Debug Settings...",
                Background = new SolidColorBrush(Color.Parse("#007ACC")),
                Foreground = Brushes.White,
                FontSize = 12,
                Padding = new Thickness(16, 6)
            };
            configBtn.Click += (s, e) =>
            {
                if (_shellWindow != null)
                {
                    var dlg = new HardwareDeployWindow(_shellWindow);
                    dlg.ShowDialog(this);
                }
            };
            _settingsContainer.Children.Add(configBtn);
        }
    }
}
