using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SpanCoder.Contracts;

namespace SpanCoder.Shell
{
    public class NuGetManagerWindow : Window
    {
        private enum TabType { Browse, Installed, Updates }

        private readonly string _projectPath;
        private readonly string _projectDir;
        private readonly string _projectName;
        private readonly Window _ownerWindow;

        private TabType _currentTab = TabType.Browse;
        private readonly List<PackageItem> _installedTopLevel = new();
        private readonly List<PackageItem> _installedTransitive = new();
        private readonly List<PackageItem> _updatesList = new();
        private readonly List<NuGetApiClient.PackageData> _searchResults = new();
        private CancellationTokenSource? _searchCts;

        // UI Controls
        private readonly StackPanel _tabPanel;
        private readonly Button _browseTabBtn;
        private readonly Button _installedTabBtn;
        private readonly Button _updatesTabBtn;
        private readonly TextBlock _updatesBadgeText;
        private readonly TextBox _searchBox;
        private readonly Button _searchBtn;
        private readonly Button _refreshBtn;
        private readonly CheckBox _prereleaseCheck;
        private readonly ScrollViewer _packageListScroll;
        private readonly StackPanel _packageListPanel;
        private readonly ScrollViewer _detailsScroll;
        private readonly StackPanel _detailsContainer;
        private readonly Grid _busyOverlay;
        private readonly TextBlock _busyText;

        private NuGetApiClient.PackageData? _selectedPackage;
        private Border? _selectedCard;

        private class PackageItem
        {
            public string Id { get; set; } = "";
            public string RequestedVersion { get; set; } = "";
            public string ResolvedVersion { get; set; } = "";
            public bool IsTransitive { get; set; }
            public string LatestVersion { get; set; } = "";
            public string Description { get; set; } = "";
            public string Authors { get; set; } = "";
            public string IconUrl { get; set; } = "";
            public string ProjectUrl { get; set; } = "";
            public string LicenseUrl { get; set; } = "";
        }

        public NuGetManagerWindow(string projectPath, Window ownerWindow)
        {
            _projectPath = projectPath;
            _projectDir = Path.GetDirectoryName(projectPath) ?? "";
            _projectName = Path.GetFileNameWithoutExtension(projectPath);
            _ownerWindow = ownerWindow;

            LogHelper.Log($"[NuGetManager] Constructing window for {_projectPath}");

            Title = $"NuGet Package Manager: {_projectName}";
            Width = 900;
            Height = 650;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.Parse("#1E1E1E"));
            Foreground = Brushes.LightGray;

            // Keyboard shortcut Ctrl+L to focus search
            this.KeyDown += (s, e) =>
            {
                if (e.Key == Key.L && e.KeyModifiers.HasFlag(KeyModifiers.Control))
                {
                    _searchBox?.Focus();
                    _searchBox?.SelectAll();
                    e.Handled = true;
                }
            };

            // Main Grid
            var mainGrid = new Grid();
            mainGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // Header & Toolbar
            mainGrid.RowDefinitions.Add(new RowDefinition(new GridLength(1)));     // Top Border line
            mainGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star));     // Content
            mainGrid.RowDefinitions.Add(new RowDefinition(new GridLength(1)));     // Bottom Border line
            mainGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // Footer

            // HEADER & TOOLBAR
            var headerGrid = new Grid { Margin = new Thickness(12, 10, 12, 10) };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto)); // Tabs
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star)); // Space
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto)); // Search & options

            // Tab Buttons
            _tabPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            _browseTabBtn = CreateTabButton("Browse", TabType.Browse);
            _installedTabBtn = CreateTabButton("Installed", TabType.Installed);
            
            // Updates Tab with Badge
            var updatesPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _updatesTabBtn = CreateTabButton("Updates", TabType.Updates);
            _updatesBadgeText = new TextBlock
            {
                Text = "",
                Foreground = new SolidColorBrush(Color.Parse("#007ACC")),
                FontWeight = FontWeight.Bold,
                FontSize = 12,
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            updatesPanel.Children.Add(_updatesTabBtn);
            updatesPanel.Children.Add(_updatesBadgeText);

            _tabPanel.Children.Add(_browseTabBtn);
            _tabPanel.Children.Add(_installedTabBtn);
            _tabPanel.Children.Add(updatesPanel);
            headerGrid.Children.Add(_tabPanel);
            Grid.SetColumn(_tabPanel, 0);

            // Search Bar & Controls
            var controlsPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            
            _searchBox = new TextBox
            {
                Watermark = "Search (Ctrl+L)",
                Width = 220,
                Background = new SolidColorBrush(Color.Parse("#252526")),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.Parse("#3E3E42")),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 4),
                FontSize = 12
            };
            _searchBox.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) ExecuteSearch();
            };

            _searchBtn = new Button
            {
                Content = "Search",
                Padding = new Thickness(10, 4),
                Background = new SolidColorBrush(Color.Parse("#3E3E42")),
                Foreground = Brushes.White,
                FontSize = 12,
                CornerRadius = new CornerRadius(3)
            };
            _searchBtn.Click += (s, e) => ExecuteSearch();

            _refreshBtn = new Button
            {
                Content = "↻",
                Padding = new Thickness(10, 4),
                Background = new SolidColorBrush(Color.Parse("#3E3E42")),
                Foreground = Brushes.White,
                FontSize = 12,
                FontWeight = FontWeight.Bold,
                CornerRadius = new CornerRadius(3)
            };
            _refreshBtn.Click += async (s, e) => await RefreshAllAsync();

            _prereleaseCheck = new CheckBox
            {
                Content = "Include prerelease",
                Foreground = Brushes.LightGray,
                IsChecked = false,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            _prereleaseCheck.IsCheckedChanged += (s, e) => { if (_currentTab == TabType.Browse) ExecuteSearch(); };

            controlsPanel.Children.Add(_searchBox);
            controlsPanel.Children.Add(_searchBtn);
            controlsPanel.Children.Add(_refreshBtn);
            controlsPanel.Children.Add(_prereleaseCheck);
            headerGrid.Children.Add(controlsPanel);
            Grid.SetColumn(controlsPanel, 2);

            mainGrid.Children.Add(headerGrid);
            Grid.SetRow(headerGrid, 0);

            // Border Line
            var topBorder = new Border { Background = new SolidColorBrush(Color.Parse("#2D2D2D")), Height = 1 };
            mainGrid.Children.Add(topBorder);
            Grid.SetRow(topBorder, 1);

            // CONTENT GRID
            var contentGrid = new Grid();
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star)); // Left Panel (Package List)
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1)));     // Separator Line
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(380)));   // Right Panel (Details)

            // Package List Panel (Left)
            _packageListScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Padding = new Thickness(12)
            };
            _packageListPanel = new StackPanel { Spacing = 6 };
            _packageListScroll.Content = _packageListPanel;
            contentGrid.Children.Add(_packageListScroll);
            Grid.SetColumn(_packageListScroll, 0);

            // Vertical Separator
            var vertSeparator = new Border { Background = new SolidColorBrush(Color.Parse("#2D2D2D")), Width = 1 };
            contentGrid.Children.Add(vertSeparator);
            Grid.SetColumn(vertSeparator, 1);

            // Details Panel (Right)
            _detailsScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Padding = new Thickness(16)
            };
            _detailsContainer = new StackPanel { Spacing = 12 };
            _detailsScroll.Content = _detailsContainer;
            contentGrid.Children.Add(_detailsScroll);
            Grid.SetColumn(_detailsScroll, 2);

            mainGrid.Children.Add(contentGrid);
            Grid.SetRow(contentGrid, 2);

            // Border Line Bottom
            var bottomBorder = new Border { Background = new SolidColorBrush(Color.Parse("#2D2D2D")), Height = 1 };
            mainGrid.Children.Add(bottomBorder);
            Grid.SetRow(bottomBorder, 3);

            // FOOTER (License Disclaimer)
            var footerBorder = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#1A1A1A")),
                Padding = new Thickness(12, 8)
            };
            var footerText = new TextBlock
            {
                Text = "Each package is licensed to you by its owner. NuGet is not responsible for, nor does it grant any licenses to, third-party packages.",
                Foreground = Brushes.Gray,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap
            };
            footerBorder.Child = footerText;
            mainGrid.Children.Add(footerBorder);
            Grid.SetRow(footerBorder, 4);

            // BUSY OVERLAY
            _busyOverlay = new Grid
            {
                Background = new SolidColorBrush(Color.FromArgb(180, 20, 20, 20)),
                IsVisible = false
            };
            var busyPanel = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Spacing = 12
            };
            var spinnerBorder = new Border
            {
                Width = 24,
                Height = 24,
                BorderBrush = new SolidColorBrush(Color.Parse("#007ACC")),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(12),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            _busyText = new TextBlock
            {
                Text = "Executing command...",
                Foreground = Brushes.White,
                FontWeight = FontWeight.SemiBold,
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            busyPanel.Children.Add(spinnerBorder);
            busyPanel.Children.Add(_busyText);
            _busyOverlay.Children.Add(busyPanel);
            mainGrid.Children.Add(_busyOverlay);
            Grid.SetRowSpan(_busyOverlay, 5);

            Content = mainGrid;

            // Load installed packages first in background, then switch tab to Browse
            _ = Task.Run(async () =>
            {
                await LoadAllInstalledPackagesAsync();
                
                // Switch tab on UI thread
                Dispatcher.UIThread.Post(() =>
                {
                    SwitchTab(TabType.Browse);
                });
            });
        }

        private Button CreateTabButton(string text, TabType tabType)
        {
            var btn = new Button
            {
                Content = text,
                Background = Brushes.Transparent,
                Foreground = Brushes.Gray,
                BorderThickness = new Thickness(0, 0, 0, 2),
                BorderBrush = Brushes.Transparent,
                Padding = new Thickness(14, 8),
                FontSize = 13,
                FontWeight = FontWeight.SemiBold
            };
            btn.Click += (s, e) => SwitchTab(tabType);
            return btn;
        }

        private void SwitchTab(TabType tabType)
        {
            _currentTab = tabType;

            // Cancel any pending search
            _searchCts?.Cancel();

            // Update Tab Button Styles
            _browseTabBtn.Foreground = tabType == TabType.Browse ? Brushes.White : Brushes.Gray;
            _browseTabBtn.BorderBrush = tabType == TabType.Browse ? new SolidColorBrush(Color.Parse("#007ACC")) : Brushes.Transparent;

            _installedTabBtn.Foreground = tabType == TabType.Installed ? Brushes.White : Brushes.Gray;
            _installedTabBtn.BorderBrush = tabType == TabType.Installed ? new SolidColorBrush(Color.Parse("#007ACC")) : Brushes.Transparent;

            _updatesTabBtn.Foreground = tabType == TabType.Updates ? Brushes.White : Brushes.Gray;
            _updatesTabBtn.BorderBrush = tabType == TabType.Updates ? new SolidColorBrush(Color.Parse("#007ACC")) : Brushes.Transparent;

            // Setup Right Panel Placeholder
            _detailsContainer.Children.Clear();
            _detailsContainer.Children.Add(new TextBlock
            {
                Text = "Select a package to view details.",
                Foreground = Brushes.Gray,
                FontSize = 12,
                Margin = new Thickness(0, 10, 0, 0)
            });

            _selectedCard = null;
            _selectedPackage = null;

            UpdatePackageListUI();

            if (tabType == TabType.Browse)
            {
                ExecuteSearch();
            }
        }

        private void ExecuteSearch()
        {
            if (_currentTab != TabType.Browse) return;

            // Cancel any pending search
            _searchCts?.Cancel();
            _searchCts = new CancellationTokenSource();
            var token = _searchCts.Token;

            string query = _searchBox.Text ?? "";
            bool prerelease = _prereleaseCheck.IsChecked ?? false;

            ShowLoadingIndicator(true);

            Task.Run(async () =>
            {
                var results = await NuGetApiClient.SearchPackagesAsync(query, prerelease);
                
                if (token.IsCancellationRequested) return;

                Dispatcher.UIThread.Post(() =>
                {
                    if (token.IsCancellationRequested) return;
                    if (_currentTab != TabType.Browse) return;

                    _searchResults.Clear();
                    _searchResults.AddRange(results);
                    ShowLoadingIndicator(false);
                    UpdatePackageListUI();
                });
            });
        }

        private async Task RefreshAllAsync()
        {
            await LoadAllInstalledPackagesAsync();
        }

        private async Task LoadAllInstalledPackagesAsync()
        {
            LogHelper.Log($"[NuGetManager] LoadAllInstalledPackagesAsync started.");
            ShowLoadingIndicator(true);

            // Load top level from csproj first (instant)
            LoadInstalledPackagesFromCsproj();
            UpdatePackageListUI();

            // Run dotnet list to get resolved version + transitive packages
            try
            {
                LogHelper.Log($"[NuGetManager] Running dotnet list for {_projectPath}");
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"list \"{_projectPath}\" package --include-transitive",
                    WorkingDirectory = _projectDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = false, // Set to false to prevent deadlock
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new System.Diagnostics.Process { StartInfo = psi };
                var outputBuilder = new System.Text.StringBuilder();
                process.OutputDataReceived += (s, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); };
                process.Start();
                process.BeginOutputReadLine();
                await process.WaitForExitAsync();

                LogHelper.Log($"[NuGetManager] dotnet list completed. ExitCode={process.ExitCode}");
                if (process.ExitCode == 0)
                {
                    ParseDotnetListOutput(outputBuilder.ToString());
                }
            }
            catch (Exception ex)
            {
                LogHelper.Log($"[NuGetManager] Error running dotnet list: {ex}");
            }

            ShowLoadingIndicator(false);
            UpdatePackageListUI();

            // Automatically check for updates in background
            await CheckForUpdatesAsync();
        }

        private void LoadInstalledPackagesFromCsproj()
        {
            try
            {
                LogHelper.Log($"[NuGetManager] LoadInstalledPackagesFromCsproj: path={_projectPath}");
                if (!File.Exists(_projectPath))
                {
                    LogHelper.Log($"[NuGetManager] Csproj not found: {_projectPath}");
                    return;
                }
                var doc = XDocument.Load(_projectPath);
                var refs = doc.Descendants()
                    .Where(x => x.Name.LocalName == "PackageReference")
                    .Select(x => new {
                        Id = x.Attribute("Include")?.Value ?? x.Attribute("Update")?.Value ?? "",
                        Version = x.Attribute("Version")?.Value ?? x.Element("Version")?.Value ?? ""
                    })
                    .Where(x => !string.IsNullOrEmpty(x.Id))
                    .ToList();

                LogHelper.Log($"[NuGetManager] Found {refs.Count} package references in csproj.");

                _installedTopLevel.Clear();
                foreach (var r in refs)
                {
                    _installedTopLevel.Add(new PackageItem { Id = r.Id, ResolvedVersion = r.Version, RequestedVersion = r.Version, IsTransitive = false });
                }
            }
            catch (Exception ex)
            {
                LogHelper.Log($"[NuGetManager] Error parsing csproj: {ex}");
            }
        }

        private void ParseDotnetListOutput(string output)
        {
            try
            {
                var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                bool inTopLevel = false;
                bool inTransitive = false;

                var newTopLevel = new List<PackageItem>();
                var newTransitive = new List<PackageItem>();

                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("Top-level Package"))
                    {
                        inTopLevel = true;
                        inTransitive = false;
                        continue;
                    }
                    if (trimmed.StartsWith("Transitive Package"))
                    {
                        inTopLevel = false;
                        inTransitive = true;
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(trimmed)) continue;

                    if (trimmed.StartsWith(">"))
                    {
                        var parts = trimmed.Substring(1).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2)
                        {
                            var id = parts[0];
                            if (id == "(A)" || id.Contains("auto-referenced")) continue;

                            var resolved = parts[parts.Length - 1];
                            var requested = parts.Length > 2 ? parts[parts.Length - 2] : resolved;
                            if (requested == "(A)") requested = resolved;

                            if (inTopLevel)
                            {
                                newTopLevel.Add(new PackageItem 
                                { 
                                    Id = id, 
                                    RequestedVersion = requested, 
                                    ResolvedVersion = resolved, 
                                    IsTransitive = false 
                                });
                            }
                            else if (inTransitive)
                            {
                                newTransitive.Add(new PackageItem 
                                { 
                                    Id = id, 
                                    RequestedVersion = "", 
                                    ResolvedVersion = resolved, 
                                    IsTransitive = true 
                                });
                            }
                        }
                    }
                }

                LogHelper.Log($"[NuGetManager] Parsed from dotnet list: TopLevel={newTopLevel.Count}, Transitive={newTransitive.Count}");

                if (newTopLevel.Count > 0)
                {
                    _installedTopLevel.Clear();
                    _installedTopLevel.AddRange(newTopLevel);
                }
                _installedTransitive.Clear();
                _installedTransitive.AddRange(newTransitive);
            }
            catch (Exception ex)
            {
                LogHelper.Log($"[NuGetManager] Error parsing dotnet list output: {ex}");
            }
        }

        private async Task CheckForUpdatesAsync()
        {
            try
            {
                LogHelper.Log($"[NuGetManager] CheckForUpdatesAsync started for {_installedTopLevel.Count} packages.");
                var tasks = _installedTopLevel.Select(async pkg =>
                {
                    var details = await NuGetApiClient.GetPackageDetailsAsync(pkg.Id);
                    if (details != null && !string.IsNullOrEmpty(details.Version))
                    {
                        if (IsNewerVersion(pkg.ResolvedVersion, details.Version))
                        {
                            return new PackageItem
                            {
                                Id = pkg.Id,
                                RequestedVersion = pkg.RequestedVersion,
                                ResolvedVersion = pkg.ResolvedVersion,
                                LatestVersion = details.Version,
                                Description = details.Description,
                                Authors = string.Join(", ", details.Authors),
                                IconUrl = details.IconUrl,
                                ProjectUrl = details.ProjectUrl,
                                LicenseUrl = details.LicenseUrl
                            };
                        }
                    }
                    return null;
                }).ToList();

                var results = await Task.WhenAll(tasks);
                
                var updates = results.Where(r => r != null).Cast<PackageItem>().ToList();
                LogHelper.Log($"[NuGetManager] Found {updates.Count} updates.");

                _updatesList.Clear();
                _updatesList.AddRange(updates);
                
                UpdateTabHeadersCount();
                if (_currentTab == TabType.Updates)
                {
                    UpdatePackageListUI();
                }
            }
            catch (Exception ex)
            {
                LogHelper.Log($"[NuGetManager] Error checking updates: {ex}");
            }
        }

        private void UpdateTabHeadersCount()
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => UpdateTabHeadersCount());
                return;
            }
            _updatesBadgeText.Text = _updatesList.Count > 0 ? $"{_updatesList.Count}" : "";
        }

        private void ShowLoadingIndicator(bool show)
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => ShowLoadingIndicator(show));
                return;
            }

            _packageListPanel.Children.Clear();
            if (show)
            {
                _packageListPanel.Children.Add(new TextBlock
                {
                    Text = "Loading packages...",
                    Foreground = Brushes.Gray,
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 20, 0, 0)
                });
            }
        }

        private void UpdatePackageListUI()
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => UpdatePackageListUI());
                return;
            }

            _packageListPanel.Children.Clear();

            if (_currentTab == TabType.Browse)
            {
                if (_searchResults.Count == 0)
                {
                    _packageListPanel.Children.Add(new TextBlock { Text = "No packages found.", Foreground = Brushes.Gray, FontSize = 12 });
                    return;
                }

                foreach (var pkg in _searchResults)
                {
                    var card = CreatePackageCard(pkg.Id, pkg.Version, "", pkg.Description, string.Join(", ", pkg.Authors), pkg.IconUrl, false, false);
                    _packageListPanel.Children.Add(card);
                }
            }
            else if (_currentTab == TabType.Installed)
            {
                if (_installedTopLevel.Count == 0 && _installedTransitive.Count == 0)
                {
                    _packageListPanel.Children.Add(new TextBlock { Text = "No packages installed in this project.", Foreground = Brushes.Gray, FontSize = 12 });
                    return;
                }

                // Group 1: Top-level Packages
                if (_installedTopLevel.Count > 0)
                {
                    _packageListPanel.Children.Add(new TextBlock
                    {
                        Text = $"Top-level packages ({_installedTopLevel.Count})",
                        Foreground = Brushes.Gray,
                        FontWeight = FontWeight.Bold,
                        FontSize = 11,
                        Margin = new Thickness(0, 5, 0, 2)
                    });
                    foreach (var pkg in _installedTopLevel)
                    {
                        var hasUpdate = _updatesList.Any(u => u.Id.Equals(pkg.Id, StringComparison.OrdinalIgnoreCase));
                        var card = CreatePackageCard(pkg.Id, pkg.ResolvedVersion, "", pkg.Description, pkg.Authors, pkg.IconUrl, false, hasUpdate);
                        _packageListPanel.Children.Add(card);
                    }
                }

                // Group 2: Transitive Packages
                if (_installedTransitive.Count > 0)
                {
                    _packageListPanel.Children.Add(new TextBlock
                    {
                        Text = $"Transitive packages ({_installedTransitive.Count})",
                        Foreground = Brushes.Gray,
                        FontWeight = FontWeight.Bold,
                        FontSize = 11,
                        Margin = new Thickness(0, 15, 0, 2)
                    });
                    foreach (var pkg in _installedTransitive)
                    {
                        var card = CreatePackageCard(pkg.Id, pkg.ResolvedVersion, "", "", "", "", true, false);
                        _packageListPanel.Children.Add(card);
                    }
                }
            }
            else if (_currentTab == TabType.Updates)
            {
                if (_updatesList.Count == 0)
                {
                    _packageListPanel.Children.Add(new TextBlock { Text = "All packages are up-to-date.", Foreground = Brushes.Gray, FontSize = 12 });
                    return;
                }

                foreach (var pkg in _updatesList)
                {
                    var card = CreatePackageCard(pkg.Id, pkg.ResolvedVersion, pkg.LatestVersion, pkg.Description, pkg.Authors, pkg.IconUrl, false, true);
                    _packageListPanel.Children.Add(card);
                }
            }
        }

        private Border CreatePackageCard(string id, string currentVer, string latestVer, string desc, string authors, string iconUrl, bool isTransitive, bool hasUpdate)
        {
            var card = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#252526")),
                BorderBrush = new SolidColorBrush(Color.Parse("#2D2D2D")),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 8),
                CornerRadius = new CornerRadius(3)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto)); // Icon
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star)); // Details
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto)); // Versions

            // Col 0: Icon
            var iconControl = CreatePackageIconControl(iconUrl, id, hasUpdate);
            grid.Children.Add(iconControl);
            Grid.SetColumn(iconControl, 0);

            // Col 1: Details (Name, Authors, Description)
            var detailsPanel = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            var nameBlock = new TextBlock
            {
                Text = id,
                Foreground = Brushes.White,
                FontWeight = FontWeight.Bold,
                FontSize = 13
            };
            detailsPanel.Children.Add(nameBlock);

            if (!string.IsNullOrEmpty(authors))
            {
                var authorsBlock = new TextBlock
                {
                    Text = $"by {authors}",
                    Foreground = Brushes.Gray,
                    FontSize = 11
                };
                detailsPanel.Children.Add(authorsBlock);
            }

            if (!string.IsNullOrEmpty(desc))
            {
                string summary = desc;
                if (summary.Length > 80) summary = summary.Substring(0, 77) + "...";
                var descBlock = new TextBlock
                {
                    Text = summary,
                    Foreground = Brushes.Gray,
                    FontSize = 11,
                    TextWrapping = TextWrapping.NoWrap
                };
                detailsPanel.Children.Add(descBlock);
            }
            grid.Children.Add(detailsPanel);
            Grid.SetColumn(detailsPanel, 1);

            // Col 2: Versions Display on the right
            var versionsPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(10, 0, 0, 0) };
            if (!string.IsNullOrEmpty(latestVer))
            {
                var oldVerBlock = new TextBlock { Text = currentVer, Foreground = Brushes.Gray, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right };
                var newVerBlock = new TextBlock { Text = $"→ {latestVer}", Foreground = new SolidColorBrush(Color.Parse("#569CD6")), FontSize = 11, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Right };
                versionsPanel.Children.Add(oldVerBlock);
                versionsPanel.Children.Add(newVerBlock);
            }
            else
            {
                var verBlock = new TextBlock { Text = currentVer, Foreground = Brushes.LightGray, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right };
                versionsPanel.Children.Add(verBlock);
            }
            grid.Children.Add(versionsPanel);
            Grid.SetColumn(versionsPanel, 2);

            card.Child = grid;

            // Hover and Selection Events
            card.PointerEntered += (s, e) =>
            {
                if (_selectedCard != card)
                {
                    card.Background = new SolidColorBrush(Color.Parse("#2D2D30"));
                    card.BorderBrush = new SolidColorBrush(Color.Parse("#3F3F46"));
                }
            };
            card.PointerExited += (s, e) =>
            {
                if (_selectedCard != card)
                {
                    card.Background = new SolidColorBrush(Color.Parse("#252526"));
                    card.BorderBrush = new SolidColorBrush(Color.Parse("#2D2D2D"));
                }
            };
            card.PointerPressed += (s, e) =>
            {
                if (_selectedCard != null)
                {
                    _selectedCard.Background = new SolidColorBrush(Color.Parse("#252526"));
                    _selectedCard.BorderBrush = new SolidColorBrush(Color.Parse("#2D2D2D"));
                }
                _selectedCard = card;
                card.Background = new SolidColorBrush(Color.Parse("#007ACC"));
                card.BorderBrush = new SolidColorBrush(Color.Parse("#0097FB"));

                _ = ShowPackageDetailsAsync(id, isTransitive ? "" : currentVer, isTransitive);
            };

            return card;
        }

        private Control CreatePackageIconControl(string? iconUrl, string packageId, bool hasUpdate)
        {
            var grid = new Grid
            {
                Width = 40,
                Height = 36,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            var border = new Border
            {
                Width = 32,
                Height = 32,
                CornerRadius = new CornerRadius(3),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };

            var initials = packageId.Length >= 2 ? packageId.Substring(0, 2).ToUpper() : "PK";
            border.Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.Parse("#007ACC"), 0),
                    new GradientStop(Color.Parse("#004C80"), 1)
                }
            };
            border.Child = new TextBlock
            {
                Text = initials,
                Foreground = Brushes.White,
                FontWeight = FontWeight.Bold,
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            grid.Children.Add(border);

            if (hasUpdate)
            {
                var badge = new Border
                {
                    Width = 14,
                    Height = 14,
                    CornerRadius = new CornerRadius(7),
                    Background = new SolidColorBrush(Color.Parse("#007ACC")),
                    BorderBrush = new SolidColorBrush(Color.Parse("#1E1E1E")),
                    BorderThickness = new Thickness(1.5),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top
                };
                badge.Child = new TextBlock
                {
                    Text = "↑",
                    Foreground = Brushes.White,
                    FontWeight = FontWeight.Bold,
                    FontSize = 9,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, -1, 0, 0)
                };
                grid.Children.Add(badge);
            }

            if (!string.IsNullOrEmpty(iconUrl) && (iconUrl.StartsWith("http://") || iconUrl.StartsWith("https://")))
            {
                Task.Run(async () =>
                {
                    try
                    {
                        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                        var bytes = await client.GetByteArrayAsync(iconUrl);
                        using var ms = new MemoryStream(bytes);
                        var bitmap = new Avalonia.Media.Imaging.Bitmap(ms);
                        Dispatcher.UIThread.Post(() =>
                        {
                            border.Child = new Image
                            {
                                Source = bitmap,
                                Width = 32,
                                Height = 32
                            };
                            border.Background = Brushes.Transparent;
                        });
                    }
                    catch
                    {
                        // Keep initials
                    }
                });
            }

            return grid;
        }

        private async Task ShowPackageDetailsAsync(string packageId, string installedVersion, bool isTransitive)
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                await Dispatcher.UIThread.InvokeAsync(async () => await ShowPackageDetailsAsync(packageId, installedVersion, isTransitive));
                return;
            }

            _detailsContainer.Children.Clear();
            _selectedPackage = null;

            var title = new TextBlock
            {
                Text = packageId,
                Foreground = Brushes.White,
                FontWeight = FontWeight.Bold,
                FontSize = 16,
                Margin = new Thickness(0, 0, 0, 10),
                TextWrapping = TextWrapping.Wrap
            };
            _detailsContainer.Children.Add(title);

            var loadingText = new TextBlock
            {
                Text = "Loading package details...",
                Foreground = Brushes.Gray,
                FontSize = 12
            };
            _detailsContainer.Children.Add(loadingText);

            var details = await NuGetApiClient.GetPackageDetailsAsync(packageId);
            _detailsContainer.Children.Remove(loadingText);

            if (details == null)
            {
                _detailsContainer.Children.Add(new TextBlock
                {
                    Text = "Could not load package details from nuget.org.",
                    Foreground = Brushes.Red,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap
                });
                return;
            }

            _selectedPackage = details;

            // Action section grid
            var actionGrid = new Grid { Margin = new Thickness(0, 5, 0, 15) };
            actionGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star)); // Version Dropdown
            actionGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto)); // Install/Update Button
            actionGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto)); // Uninstall Button

            var versionCombo = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 8, 0)
            };

            var versionsList = details.Versions.Select(v => v.Version).Reverse().ToList();
            versionCombo.ItemsSource = versionsList;

            // Preselect target version
            if (!string.IsNullOrEmpty(installedVersion))
            {
                versionCombo.SelectedItem = installedVersion;
            }
            else if (versionsList.Count > 0)
            {
                var latestStable = versionsList.FirstOrDefault(v => !v.Contains("-"));
                versionCombo.SelectedItem = latestStable ?? versionsList[0];
            }

            actionGrid.Children.Add(versionCombo);
            Grid.SetColumn(versionCombo, 0);

            // Install/Update Button
            var installBtn = new Button
            {
                Content = !string.IsNullOrEmpty(installedVersion) ? "Update" : "Install",
                Background = new SolidColorBrush(Color.Parse("#007ACC")),
                Foreground = Brushes.White,
                Padding = new Thickness(12, 6),
                Margin = new Thickness(0, 0, 6, 0),
                CornerRadius = new CornerRadius(3),
                FontSize = 12
            };
            installBtn.Click += async (s, e) =>
            {
                var targetVer = versionCombo.SelectedItem as string;
                if (!string.IsNullOrEmpty(targetVer))
                {
                    string actionStr = !string.IsNullOrEmpty(installedVersion) ? "Updating" : "Installing";
                    string args = $"add \"{_projectPath}\" package \"{packageId}\" --version \"{targetVer}\"";
                    await RunPackageCommandAsync(args, $"{actionStr} {packageId} to version {targetVer}");
                }
            };
            actionGrid.Children.Add(installBtn);
            Grid.SetColumn(installBtn, 1);

            // Uninstall Button
            if (!string.IsNullOrEmpty(installedVersion) && !isTransitive)
            {
                var uninstallBtn = new Button
                {
                    Content = "Uninstall",
                    Background = new SolidColorBrush(Color.Parse("#D13438")),
                    Foreground = Brushes.White,
                    Padding = new Thickness(12, 6),
                    CornerRadius = new CornerRadius(3),
                    FontSize = 12
                };
                uninstallBtn.Click += async (s, e) =>
                {
                    string args = $"remove \"{_projectPath}\" package \"{packageId}\"";
                    await RunPackageCommandAsync(args, $"Uninstalling {packageId}");
                };
                actionGrid.Children.Add(uninstallBtn);
                Grid.SetColumn(uninstallBtn, 2);
            }

            _detailsContainer.Children.Add(actionGrid);

            // Message for transitive dependencies
            if (isTransitive)
            {
                var transBorder = new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#2D2D30")),
                    Padding = new Thickness(8),
                    Margin = new Thickness(0, 0, 0, 10),
                    CornerRadius = new CornerRadius(3)
                };
                transBorder.Child = new TextBlock
                {
                    Text = "This package is a transitive dependency (installed by another package). You can install it directly as a top-level package, but you cannot uninstall it here without removing its parent package.",
                    Foreground = Brushes.Gray,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    FontStyle = FontStyle.Italic
                };
                _detailsContainer.Children.Add(transBorder);
            }

            // Description
            _detailsContainer.Children.Add(new TextBlock { Text = "Description", FontWeight = FontWeight.Bold, Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(0, 8, 0, 0) });
            _detailsContainer.Children.Add(new TextBlock { Text = details.Description, Foreground = Brushes.LightGray, FontSize = 12, TextWrapping = TextWrapping.Wrap });

            // Authors
            _detailsContainer.Children.Add(new TextBlock { Text = "Authors", FontWeight = FontWeight.Bold, Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(0, 8, 0, 0) });
            _detailsContainer.Children.Add(new TextBlock { Text = string.Join(", ", details.Authors), Foreground = Brushes.LightGray, FontSize = 12, TextWrapping = TextWrapping.Wrap });

            // Downloads
            _detailsContainer.Children.Add(new TextBlock { Text = "Downloads", FontWeight = FontWeight.Bold, Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(0, 8, 0, 0) });
            _detailsContainer.Children.Add(new TextBlock { Text = details.TotalDownloads.ToString("N0"), Foreground = Brushes.LightGray, FontSize = 12 });

            // Project Site
            if (!string.IsNullOrEmpty(details.ProjectUrl))
            {
                _detailsContainer.Children.Add(new TextBlock { Text = "Project Site", FontWeight = FontWeight.Bold, Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(0, 8, 0, 0) });
                var link = new TextBlock 
                { 
                    Text = details.ProjectUrl, 
                    Foreground = new SolidColorBrush(Color.Parse("#569CD6")), 
                    FontSize = 12, 
                    TextWrapping = TextWrapping.Wrap
                };
                _detailsContainer.Children.Add(link);
            }

            // License Link
            if (!string.IsNullOrEmpty(details.LicenseUrl))
            {
                _detailsContainer.Children.Add(new TextBlock { Text = "License", FontWeight = FontWeight.Bold, Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(0, 8, 0, 0) });
                var link = new TextBlock 
                { 
                    Text = details.LicenseUrl, 
                    Foreground = new SolidColorBrush(Color.Parse("#569CD6")), 
                    FontSize = 12, 
                    TextWrapping = TextWrapping.Wrap
                };
                _detailsContainer.Children.Add(link);
            }
        }

        private async Task RunPackageCommandAsync(string arguments, string statusMessage)
        {
            ShowBusyOverlay(true, statusMessage);

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = arguments,
                    WorkingDirectory = _projectDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = false, // Set to false to avoid deadlock
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new System.Diagnostics.Process { StartInfo = psi };
                var outputBuilder = new System.Text.StringBuilder();
                process.OutputDataReceived += (s, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); };
                process.Start();
                process.BeginOutputReadLine();

                await process.WaitForExitAsync();

                string fullOutput = outputBuilder.ToString();
                if (process.ExitCode == 0)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        OutputDialog.Show(this, "Success", $"{statusMessage} Succeeded.\n\n{fullOutput}");
                    });
                }
                else
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        OutputDialog.Show(this, "Failed", $"{statusMessage} Failed with exit code {process.ExitCode}.\n\n{fullOutput}");
                    });
                }
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    OutputDialog.Show(this, "Error", $"Error executing package command: {ex.Message}");
                });
            }

            // Reload all packages
            await LoadAllInstalledPackagesAsync();
            
            ShowBusyOverlay(false, "");
        }

        private void ShowBusyOverlay(bool show, string message)
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => ShowBusyOverlay(show, message));
                return;
            }

            _busyOverlay.IsVisible = show;
            _busyText.Text = message;

            _tabPanel.IsEnabled = !show;
            _searchBox.IsEnabled = !show;
            _searchBtn.IsEnabled = !show;
            _refreshBtn.IsEnabled = !show;
            _prereleaseCheck.IsEnabled = !show;
            _packageListScroll.IsEnabled = !show;
            _detailsScroll.IsEnabled = !show;
        }

        private static bool IsNewerVersion(string currentStr, string latestStr)
        {
            if (currentStr == latestStr) return false;
            
            var currClean = currentStr.Split('-')[0];
            var lateClean = latestStr.Split('-')[0];

            if (Version.TryParse(currClean, out var currVer) && Version.TryParse(lateClean, out var lateVer))
            {
                if (lateVer > currVer) return true;
                if (lateVer < currVer) return false;
            }
            
            return string.Compare(latestStr, currentStr, StringComparison.OrdinalIgnoreCase) > 0;
        }
    }
}
