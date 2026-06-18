using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace SpanCoder.Shell
{
    public class SettingsWindow : Window
    {
        private readonly ListBox _categoryList;
        private readonly StackPanel _settingsContainer;
        private readonly TextBox _searchBox;

        public SettingsWindow()
        {
            Title = "Settings";
            Width = 650;
            Height = 500;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.Parse("#1E1E1E"));
            Foreground = Brushes.LightGray;

            var mainGrid = new Grid();
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
            var categories = new List<string> { "All", "Text Editor", "Workbench", "Extensions" };
            _categoryList.ItemsSource = categories;
            _categoryList.SelectedIndex = 0;
        }

        private void RefreshSettingsList()
        {
            _settingsContainer.Children.Clear();
            string selectedCat = _categoryList.SelectedItem as string ?? "All";
            string query = _searchBox.Text ?? "";

            var descriptors = SettingsManager.GetDescriptors();
            foreach (var desc in descriptors)
            {
                // Filter by category
                if (selectedCat == "Text Editor" && !desc.Id.StartsWith("editor.", StringComparison.OrdinalIgnoreCase)) continue;
                if (selectedCat == "Workbench" && !desc.Id.StartsWith("workbench.", StringComparison.OrdinalIgnoreCase)) continue;
                if (selectedCat == "Extensions" && (desc.Id.StartsWith("editor.", StringComparison.OrdinalIgnoreCase) || desc.Id.StartsWith("workbench.", StringComparison.OrdinalIgnoreCase) || desc.Id.StartsWith("liveUnitTesting.", StringComparison.OrdinalIgnoreCase))) continue;

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
    }
}
