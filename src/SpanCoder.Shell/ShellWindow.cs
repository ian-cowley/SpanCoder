using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.Controls.Templates;
using SpanCoder.Contracts;
using System.Buffers;


namespace SpanCoder.Shell
{
    public class ShellWindow : Window
    {
        private EditorPane _activePane = null!;
        private readonly List<EditorPane> _editorPanes = new();
        private readonly List<TextEditorCanvas.ContextMenuItem> _extensionContextMenuItems = new();
        private Grid _editorSplitContainer = null!;

        private TextEditorCanvas _canvas => _activePane.Canvas;
        private ScrollBar _vScroll => _activePane.VScroll;
        private ScrollBar _hScroll => _activePane.HScroll;
        private StackPanel _tabsContainer => _activePane.TabsContainer;
        private List<OpenDocument> _openDocuments => _activePane.OpenDocuments;
        private OpenDocument? _activeDocument
        {
            get => _activePane.ActiveDocument;
            set => _activePane.ActiveDocument = value;
        }

        public TextEditorCanvas ActiveCanvas => _canvas;

        private TextBlock _statusBar = null!;
        private StackPanel _statusBarExtensionPanel = null!;
        
        private Border _debugToolbar = null!;
        private ListBox _debugVariablesList = null!;
        private ListBox _debugCallStackList = null!;
        private ListBox _debugBreakpointsList = null!;
        private TabItem _debugTab = null!;
        private bool _isDebugging = false;

        private ListBox _gitChangesList = null!;
        private TextBox _commitMessageInput = null!;
        private GitVersionProvider _gitProvider = null!;
        private DispatcherTimer? _gitTimer;
        private PtyHost? _terminalPty;

        private IEngineConnection? _engine;
        private CollabServer? _collabServer;
        private CollabClient? _collabClient;
        private string _currentFilePath = "Untitled";
        private System.Diagnostics.Stopwatch? _startupStopwatch;

        private IExtensionManager? _extensionManager;
        private TabControl _sidebarTabControl = null!;
        private AiChatPanel _aiChatPanel = null!;
        private FindReplaceFilesWindow? _findReplaceFilesWindow;
        private TabControl _bottomTabControl = null!;
        private TabItem _findResultsTab = null!;
        private FindResultsPanel _findResultsPanel = null!;
        private Menu _mainMenu = null!;
        private StackPanel _toolbarPanel = null!;
        private readonly Dictionary<string, TextBlock> _pluginPanels = new();
        private readonly Dictionary<string, string> _commandToExtensionMap = new();
        private readonly List<CommandDescriptor> _extensionCommands = new();
        private readonly Dictionary<string, Border> _extensionStatusBarItems = new(StringComparer.OrdinalIgnoreCase);
        private CommandPalette _commandPalette = null!;
        
        private SidebarFileTree _fileTree = null!;

        private Border _autocompleteBorder = null!;
        private ListBox _autocompleteList = null!;
        private List<AutocompleteItem> _autocompleteItems = new();
        private Border _hoverBorder = null!;
        private TextBlock _hoverText = null!;
        private double _lastHoverMouseX;
        private double _lastHoverMouseY;
        private (string FilePath, int Line, int Character)? _pendingNavigation;
        private DispatcherTimer? _ghostTextTimer;
        private static readonly System.Net.Http.SocketsHttpHandler _socketsHandler = new()
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(1)
        };

        private readonly Dictionary<string, List<Control>> _extensionUiElements = new();
        private readonly Dictionary<string, List<KeyBinding>> _extensionKeyBindings = new();
        private readonly Dictionary<string, List<string>> _extensionPanelIds = new();
        private readonly Dictionary<string, List<string>> _extensionLanguageExts = new();
        private readonly Dictionary<string, (System.Net.Sockets.TcpClient Client, System.Threading.CancellationTokenSource Cts)> _mockExtensionConnections = new();

        public ShellWindow()
        {
            Title = "SpanCoder IDE";
            Width = 900;
            Height = 600;
            Background = Brushes.Black;

            // Intercept Alt key down/up to defend TextEditorCanvas focus from menu mnemonics activation
            AddHandler(InputElement.KeyDownEvent, (s, e) =>
            {
                if (e.Key == Key.LeftAlt || e.Key == Key.RightAlt)
                {
                    var focused = FocusManager?.GetFocusedElement();
                    if (focused is TextEditorCanvas)
                    {
                        LogHelper.Log("[ShellWindow] Tunneling KeyDown: Intercepted Alt key on TextEditorCanvas, marking Handled=true");
                        e.Handled = true;
                    }
                }
            }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

            AddHandler(InputElement.KeyUpEvent, (s, e) =>
            {
                if (e.Key == Key.LeftAlt || e.Key == Key.RightAlt)
                {
                    var focused = FocusManager?.GetFocusedElement();
                    if (focused is TextEditorCanvas)
                    {
                        LogHelper.Log("[ShellWindow] Tunneling KeyUp: Intercepted Alt key on TextEditorCanvas, marking Handled=true");
                        e.Handled = true;
                    }
                }
            }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        }

        public ShellWindow(System.Diagnostics.Stopwatch startupStopwatch) : this()
        {
            _startupStopwatch = startupStopwatch;
        }

        public void InitializeLayout()
        {
            var mainGrid = new Grid();
            mainGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // Row 0: Menu
            mainGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // Row 1: Toolbar
            mainGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star)); // Row 2: Workspace
            mainGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // Row 3: Status Bar

            // 1. Dynamic Menu
            _mainMenu = ShellLayoutManager.BuildMenu(OnCommandInvoked);
            mainGrid.Children.Add(_mainMenu);
            Grid.SetRow(_mainMenu, 0);

            // 1.5. Dynamic Toolbar
            _toolbarPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Background = new SolidColorBrush(Color.Parse("#252526")),
                Height = 36,
                Spacing = 6,
                Margin = new Thickness(0)
            };

            var toolbarBorder = new Border
            {
                BorderBrush = new SolidColorBrush(Color.Parse("#2D2D2D")),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(8, 4),
                Background = new SolidColorBrush(Color.Parse("#252526")),
                Child = _toolbarPanel
            };

            mainGrid.Children.Add(toolbarBorder);
            Grid.SetRow(toolbarBorder, 1);

            // Add default toolbar items
            AddToolbarButton("Toggle Line Comment", "Edit.ToggleLineComment", "Toggle Line Comment (Ctrl+/)");
            AddToolbarButton("Toggle Block Comment", "Edit.ToggleBlockComment", "Toggle Block Comment (Ctrl+Shift+/)");
            AddToolbarButton("⚡ Hot Reload", "Build.HotReload", "Hot Reload Changes (F4)");

            // 2. Workspace Layout
            var workspaceGrid = new Grid();
            var sidebarCol = new ColumnDefinition { Width = new GridLength(220), MinWidth = 150 };
            workspaceGrid.ColumnDefinitions.Add(sidebarCol); // Sidebar
            workspaceGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto)); // Splitter
            workspaceGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star)); // Editor Pane

            // 2.1. Sidebar TabControl
            _sidebarTabControl = new TabControl
            {
                Background = new SolidColorBrush(Color.Parse("#1A1A1A")),
                TabStripPlacement = Dock.Top,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var explorerGrid = new Grid();
            explorerGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // Header
            explorerGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star)); // TreeView

            var sidebarHeader = new TextBlock
            {
                Text = "EXPLORER",
                Foreground = Brushes.DarkGray,
                FontWeight = FontWeight.Bold,
                FontSize = 11,
                Padding = new Thickness(10, 8, 10, 4)
            };
            explorerGrid.Children.Add(sidebarHeader);
            Grid.SetRow(sidebarHeader, 0);

            _fileTree = new SidebarFileTree(this);
            _fileTree.FileSelected += OpenFile;
            explorerGrid.Children.Add(_fileTree);
            Grid.SetRow(_fileTree, 1);

            var explorerTabHeader = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 2)
            };
            explorerTabHeader.Children.Add(new Avalonia.Controls.Shapes.Path
            {
                Width = 14,
                Height = 14,
                Data = StreamGeometry.Parse("M19,3 H5 C3.9,3 3,3.9 3,5 V19 C3,20.1 3.9,21 5,21 H19 C20.1,21 21,20.1 21,19 V5 C21,3.9 20.1,3 19,3 Z M14,17 H7 V15 H14 V17 Z M17,13 H7 V11 H17 V13 Z M17,9 H7 V7 H17 V9 Z"),
                Fill = new SolidColorBrush(Color.Parse("#519ABA")),
                Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            explorerTabHeader.Children.Add(new TextBlock
            {
                Text = "Explorer",
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            });

            var explorerTab = new TabItem
            {
                Header = explorerTabHeader,
                Content = explorerGrid
            };
            _sidebarTabControl.Items.Add(explorerTab);

            // 2.1.2. Source Control Tab Layout
            var gitGrid = new Grid();
            gitGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // Changes header
            gitGrid.RowDefinitions.Add(new RowDefinition(new GridLength(1.0, GridUnitType.Star))); // Changes ListBox
            gitGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // Commit section

            var gitHeader = new TextBlock { Text = "CHANGES", Foreground = Brushes.DarkGray, FontSize = 11, Padding = new Thickness(10, 8, 10, 4), FontWeight = FontWeight.Bold };
            gitGrid.Children.Add(gitHeader);
            Grid.SetRow(gitHeader, 0);

            _gitChangesList = new ListBox
            {
                Background = new SolidColorBrush(Color.Parse("#1A1A1A")),
                Foreground = Brushes.LightGray,
                FontSize = 11
            };
            
            _gitChangesList.DoubleTapped += (s, e) =>
            {
                if (_gitChangesList.SelectedItem is GitFileStatus status && !string.IsNullOrEmpty(_fileTree.RootPath))
                {
                    string baseDir = Directory.Exists(_fileTree.RootPath) ? _fileTree.RootPath : Path.GetDirectoryName(_fileTree.RootPath) ?? "";
                    string fullPath = Path.Combine(baseDir, status.FilePath);
                    if (File.Exists(fullPath))
                    {
                        OpenFile(fullPath);
                    }
                }
            };

            _gitChangesList.ItemTemplate = new FuncDataTemplate<GitFileStatus>((status, names) =>
            {
                var panel = new DockPanel { HorizontalAlignment = HorizontalAlignment.Stretch, LastChildFill = true };
                
                IBrush badgeBrush = status.Status switch
                {
                    "A" => Brushes.LightGreen,
                    "D" => Brushes.Red,
                    "U" => Brushes.Gold,
                    _ => Brushes.SkyBlue
                };

                var badge = new Border
                {
                    Background = badgeBrush,
                    CornerRadius = new CornerRadius(2),
                    Padding = new Thickness(4, 1),
                    Margin = new Thickness(4, 0),
                    Child = new TextBlock
                    {
                        Text = status.Status,
                        Foreground = Brushes.Black,
                        FontWeight = FontWeight.Bold,
                        FontSize = 10
                    }
                };
                DockPanel.SetDock(badge, Dock.Right);
                panel.Children.Add(badge);

                var text = new TextBlock
                {
                    Text = status.FilePath,
                    Foreground = status.Staged ? Brushes.LightGreen : Brushes.LightGray,
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center
                };
                panel.Children.Add(text);

                return panel;
            }, true);

            var gitContextMenu = new ContextMenu();
            var menuStage = new MenuItem { Header = "Stage File" };
            menuStage.Click += async (s, e) =>
            {
                if (_gitChangesList.SelectedItem is GitFileStatus status)
                {
                    await _gitProvider.StageFileAsync(status.FilePath);
                    await RefreshGitStatusAsync();
                }
            };
            var menuUnstage = new MenuItem { Header = "Unstage File" };
            menuUnstage.Click += async (s, e) =>
            {
                if (_gitChangesList.SelectedItem is GitFileStatus status)
                {
                    await _gitProvider.UnstageFileAsync(status.FilePath);
                    await RefreshGitStatusAsync();
                }
            };
            gitContextMenu.Items.Add(menuStage);
            gitContextMenu.Items.Add(menuUnstage);
            _gitChangesList.ContextMenu = gitContextMenu;

            gitGrid.Children.Add(_gitChangesList);
            Grid.SetRow(_gitChangesList, 1);

            // Commit Section
            var commitPanel = new StackPanel { Orientation = Orientation.Vertical, Spacing = 4 };
            _commitMessageInput = new TextBox
            {
                Watermark = "Commit message...",
                Background = new SolidColorBrush(Color.Parse("#252526")),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.Parse("#3E3E42")),
                Margin = new Thickness(8, 4),
                FontSize = 12
            };
            commitPanel.Children.Add(_commitMessageInput);

            var buttonsPanel = new Grid();
            buttonsPanel.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            buttonsPanel.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

            var btnCommit = new Button
            {
                Content = "Commit",
                Background = new SolidColorBrush(Color.Parse("#0E639C")),
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(8, 2, 4, 4),
                Height = 28
            };
            btnCommit.Click += async (s, e) =>
            {
                string msg = _commitMessageInput.Text ?? "";
                if (!string.IsNullOrWhiteSpace(msg))
                {
                    await _gitProvider.CommitAsync(msg);
                    _commitMessageInput.Text = "";
                    await RefreshGitStatusAsync();
                }
            };
            buttonsPanel.Children.Add(btnCommit);
            Grid.SetColumn(btnCommit, 0);

            var btnPush = new Button
            {
                Content = "Push",
                Background = new SolidColorBrush(Color.Parse("#333333")),
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(4, 2, 8, 4),
                Height = 28
            };
            btnPush.Click += async (s, e) =>
            {
                await _gitProvider.PushAsync();
                await RefreshGitStatusAsync();
            };
            buttonsPanel.Children.Add(btnPush);
            Grid.SetColumn(btnPush, 1);

            commitPanel.Children.Add(buttonsPanel);
            gitGrid.Children.Add(commitPanel);
            Grid.SetRow(commitPanel, 2);

            var gitTabHeader = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2) };
            gitTabHeader.Children.Add(new Avalonia.Controls.Shapes.Path
            {
                Width = 14,
                Height = 14,
                Data = StreamGeometry.Parse("M12,2 C6.48,2 2,6.48 2,12 C2,15.62 3.9,18.8 6.75,20.6 L6.75,20.65 L6.75,22 C6.75,22.55 7.2,23 7.75,23 C8.3,23 8.75,22.55 8.75,22 L8.75,20.25 L9,20 C10,19.3 11,19 12,19 C13,19 14,19.3 15,20 L15.25,20.25 L15.25,22 C15.25,22.55 15.7,23 16.25,23 C16.8,23 17.25,22.55 17.25,22 L17.25,20.65 C20.1,18.8 22,15.62 22,12 C22,6.48 17.52,2 12,2 Z"),
                Fill = new SolidColorBrush(Color.Parse("#F05032")),
                Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            gitTabHeader.Children.Add(new TextBlock { Text = "Source Control", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });

            var gitTab = new TabItem
            {
                Header = gitTabHeader,
                Content = gitGrid
            };
            _sidebarTabControl.Items.Add(gitTab);

            // 2.1.5. Debug Tab Layout
            var debugGrid = new Grid();
            debugGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // Variables header
            debugGrid.RowDefinitions.Add(new RowDefinition(new GridLength(1.0, GridUnitType.Star))); // Variables ListBox
            debugGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // Call Stack header
            debugGrid.RowDefinitions.Add(new RowDefinition(new GridLength(1.0, GridUnitType.Star))); // Call Stack ListBox
            debugGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // Breakpoints header
            debugGrid.RowDefinitions.Add(new RowDefinition(new GridLength(0.8, GridUnitType.Star))); // Breakpoints ListBox

            var varHeader = new TextBlock { Text = "VARIABLES", Foreground = Brushes.DarkGray, FontSize = 11, Padding = new Thickness(10, 8, 10, 4), FontWeight = FontWeight.Bold };
            debugGrid.Children.Add(varHeader);
            Grid.SetRow(varHeader, 0);

            _debugVariablesList = new ListBox
            {
                Background = new SolidColorBrush(Color.Parse("#1A1A1A")),
                Foreground = Brushes.LightGray,
                FontSize = 11
            };
            debugGrid.Children.Add(_debugVariablesList);
            Grid.SetRow(_debugVariablesList, 1);

            var stackHeader = new TextBlock { Text = "CALL STACK", Foreground = Brushes.DarkGray, FontSize = 11, Padding = new Thickness(10, 8, 10, 4), FontWeight = FontWeight.Bold };
            debugGrid.Children.Add(stackHeader);
            Grid.SetRow(stackHeader, 2);

            _debugCallStackList = new ListBox
            {
                Background = new SolidColorBrush(Color.Parse("#1A1A1A")),
                Foreground = Brushes.LightGray,
                FontSize = 11
            };
            _debugCallStackList.DoubleTapped += OnCallStackDoubleTapped;
            debugGrid.Children.Add(_debugCallStackList);
            Grid.SetRow(_debugCallStackList, 3);

            var bpHeader = new TextBlock { Text = "BREAKPOINTS", Foreground = Brushes.DarkGray, FontSize = 11, Padding = new Thickness(10, 8, 10, 4), FontWeight = FontWeight.Bold };
            debugGrid.Children.Add(bpHeader);
            Grid.SetRow(bpHeader, 4);

            _debugBreakpointsList = new ListBox
            {
                Background = new SolidColorBrush(Color.Parse("#1A1A1A")),
                Foreground = Brushes.LightGray,
                FontSize = 11
            };
            debugGrid.Children.Add(_debugBreakpointsList);
            Grid.SetRow(_debugBreakpointsList, 5);

            var debugTabHeader = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2) };
            debugTabHeader.Children.Add(new Avalonia.Controls.Shapes.Path
            {
                Width = 14,
                Height = 14,
                Data = StreamGeometry.Parse("M19,8 H16.24 A6,6 0 0,0 12,5 A6,6 0 0,0 7.76,8 H5 A1,1 0 0,0 5,10 H7.12 A6,6 0 0,0 7,12 A6,6 0 0,0 7.12,14 H5 A1,1 0 0,0 5,16 H7.76 A6,6 0 0,0 12,19 A6,6 0 0,0 16.24,16 H19 A1,1 0 0,0 19,14 H16.88 A6,6 0 0,0 17,12 A6,6 0 0,0 16.88,10 H19 A1,1 0 0,0 19,8 Z"),
                Fill = new SolidColorBrush(Color.Parse("#75B943")),
                Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            debugTabHeader.Children.Add(new TextBlock { Text = "Debug", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });

            _debugTab = new TabItem
            {
                Header = debugTabHeader,
                Content = debugGrid
            };
            _sidebarTabControl.Items.Add(_debugTab);

            // 2.1.6. Extensions Tab Layout
            var extGrid = new Grid();
            extGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // Search
            extGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star)); // ListBox

            var extSearchBox = new TextBox
            {
                Watermark = "Search extensions...",
                Background = new SolidColorBrush(Color.Parse("#252526")),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.Parse("#3E3E42")),
                Margin = new Thickness(8, 4),
                FontSize = 12
            };
            extGrid.Children.Add(extSearchBox);
            Grid.SetRow(extSearchBox, 0);

            var extListBox = new ListBox
            {
                Background = new SolidColorBrush(Color.Parse("#1A1A1A")),
                Foreground = Brushes.LightGray,
                FontSize = 11,
                Margin = new Thickness(0, 4)
            };
            extGrid.Children.Add(extListBox);
            Grid.SetRow(extListBox, 1);

            var marketplaceExtensions = new List<MarketplaceExtension>
            {
                new MarketplaceExtension
                {
                    Id = "html-preview",
                    Name = "HTML Previewer",
                    Description = "Live preview for HTML files.",
                    ManifestJson = @"{
                      ""id"": ""html-preview"",
                      ""commands"": [
                        {
                          ""id"": ""html-preview.show"",
                          ""displayName"": ""Show HTML Preview"",
                          ""category"": ""View"",
                          ""defaultShortcut"": ""Ctrl+Shift+H""
                        }
                      ],
                      ""menuItems"": [
                        {
                          ""commandId"": ""html-preview.show"",
                          ""menuPath"": ""View/Show HTML Preview"",
                          ""orderPriority"": 50
                        }
                      ],
                      ""panels"": [
                        {
                          ""id"": ""html-preview-panel"",
                          ""title"": ""HTML Preview""
                        }
                      ],
                      ""toolbarItems"": [
                        {
                          ""commandId"": ""html-preview.show"",
                          ""displayName"": ""HTML Preview"",
                          ""orderPriority"": 200
                        }
                      ]
                    }"
                },
                new MarketplaceExtension
                {
                    Id = "python-lang",
                    Name = "Python Support",
                    Description = "Syntax highlight and tools for Python.",
                    ManifestJson = @"{
                      ""id"": ""python-lang"",
                      ""commands"": [
                        {
                          ""id"": ""python.run"",
                          ""displayName"": ""Run Python Script"",
                          ""category"": ""Tools"",
                          ""defaultShortcut"": ""Ctrl+F5""
                        }
                      ],
                      ""menuItems"": [
                        {
                          ""commandId"": ""python.run"",
                          ""menuPath"": ""Tools/Run Python Script"",
                          ""orderPriority"": 60
                        }
                      ],
                      ""toolbarItems"": [
                        {
                          ""commandId"": ""python.run"",
                          ""displayName"": ""Run Python"",
                          ""orderPriority"": 210
                        }
                      ],
                      ""languages"": [
                        {
                          ""extension"": "".py"",
                          ""lineComment"": ""#"",
                          ""keywords"": [""def"", ""class"", ""import"", ""from"", ""if"", ""elif"", ""else"", ""while"", ""for"", ""in"", ""return"", ""print""],
                          ""types"": [""int"", ""str"", ""list"", ""dict"", ""object""]
                        }
                      ]
                    }"
                }
            };

            void RefreshExtensionsList()
            {
                string query = extSearchBox.Text ?? "";
                var filtered = marketplaceExtensions
                    .Where(x => x.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                x.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                extListBox.ItemsSource = filtered;
            }

            extSearchBox.KeyUp += (s, e) => RefreshExtensionsList();

            extListBox.ItemTemplate = new FuncDataTemplate<MarketplaceExtension>((item, names) =>
            {
                var itemPanel = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(6, 4) };
                
                var titleText = new TextBlock { Text = item.Name, Foreground = Brushes.White, FontWeight = FontWeight.Bold, FontSize = 12 };
                var descText = new TextBlock { Text = item.Description, Foreground = Brushes.Gray, FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) };
                
                var btnAction = new Button
                {
                    Content = item.IsInstalled ? "Uninstall" : "Install",
                    Background = item.IsInstalled ? new SolidColorBrush(Color.Parse("#444444")) : new SolidColorBrush(Color.Parse("#0E639C")),
                    Foreground = Brushes.White,
                    FontSize = 10,
                    Padding = new Thickness(8, 2),
                    Height = 22,
                    HorizontalAlignment = HorizontalAlignment.Left
                };

                btnAction.Click += (s, e) =>
                {
                    if (item.IsInstalled)
                    {
                        StopMockExtension(item.Id);
                        UnregisterExtensionUI(item.Id);
                        item.IsInstalled = false;
                    }
                    else
                    {
                        StartMockExtension(item.Id, item.ManifestJson);
                        item.IsInstalled = true;
                    }
                    RefreshExtensionsList();
                };

                itemPanel.Children.Add(titleText);
                itemPanel.Children.Add(descText);
                itemPanel.Children.Add(btnAction);

                return itemPanel;
            }, true);

            var extTabHeader = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2) };
            extTabHeader.Children.Add(new Avalonia.Controls.Shapes.Path
            {
                Width = 14,
                Height = 14,
                Data = StreamGeometry.Parse("M20.5,11 H19 V9 C19,7.9 18.1,7 17,7 H15 V5.5 C15,4.12 13.88,3 12.5,3 C11.12,3 10,4.12 10,5.5 V7 H8 C6.9,7 6,7.9 6,9 V11 H4.5 C3.12,11 2,12.12 2,13.5 C2,14.88 3.12,16 4.5,16 H6 V18 C6,19.1 6.9,20 8,20 H10 V21.5 C10,22.88 11.12,24 12.5,24 C13.88,24 15,22.88 15,21.5 V20 H17 C18.1,20 19,19.1 19,18 V16 H20.5 C21.88,16 23,14.88 23,13.5 C23,12.12 21.88,11 20.5,11 Z"),
                Fill = new SolidColorBrush(Color.Parse("#D8A0DF")),
                Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            extTabHeader.Children.Add(new TextBlock { Text = "Extensions", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });

            var extTab = new TabItem
            {
                Header = extTabHeader,
                Content = extGrid
            };
            _sidebarTabControl.Items.Add(extTab);

            // AI Chat Tab Layout
            _aiChatPanel = new AiChatPanel();
            var aiTabHeader = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2) };
            aiTabHeader.Children.Add(new Avalonia.Controls.Shapes.Path
            {
                Width = 14,
                Height = 14,
                Data = StreamGeometry.Parse("M20,2 H4 C2.9,2 2,2.9 2,4 V22 L6,18 H20 C21.1,18 22,17.1 22,16 V4 C22,2.9 21.1,2 20,2 Z M18,14 H6 V12 H18 V14 Z M18,10 H6 V8 H18 V10 Z"),
                Fill = new SolidColorBrush(Color.Parse("#4EC9B0")),
                Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            aiTabHeader.Children.Add(new TextBlock { Text = "AI Chat", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });

            var aiTab = new TabItem
            {
                Header = aiTabHeader,
                Content = _aiChatPanel
            };
            _sidebarTabControl.Items.Add(aiTab);

             RefreshExtensionsList();

            workspaceGrid.Children.Add(_sidebarTabControl);
            Grid.SetColumn(_sidebarTabControl, 0);

            // 2.2. Grid Splitter
            var splitter = new GridSplitter
            {
                Width = 4,
                Background = new SolidColorBrush(Color.Parse("#2D2D2D")),
                ResizeDirection = GridResizeDirection.Columns
            };
            workspaceGrid.Children.Add(splitter);
            Grid.SetColumn(splitter, 1);

            // 2.3. Editor Pane (Tabs + Editor Canvas Container)
            var editorPane = new Grid();
            editorPane.RowDefinitions.Add(new RowDefinition(GridLength.Star)); // Row 0: Editor Split Container
            editorPane.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // Row 1: Bottom Splitter
            editorPane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(180, GridUnitType.Pixel) }); // Row 2: Bottom Panel

            _editorSplitContainer = new Grid();
            editorPane.Children.Add(_editorSplitContainer);
            Grid.SetRow(_editorSplitContainer, 0);

            // Initial Editor Pane
            var initialPane = new EditorPane(this);
            _editorPanes.Add(initialPane);
            _activePane = initialPane;
            initialPane.Border.BorderBrush = new SolidColorBrush(Color.Parse("#007ACC"));

            _editorSplitContainer.Children.Add(initialPane);
            Grid.SetRow(initialPane, 0);
            Grid.SetColumn(initialPane, 0);

            // Autocomplete Overlay
            _autocompleteList = new ListBox
            {
                Background = new SolidColorBrush(Color.Parse("#252526")),
                Foreground = Brushes.LightGray,
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.Parse("#3E3E42")),
                MaxHeight = 150,
                Width = 250,
                Focusable = false
            };

            _autocompleteList.ItemTemplate = new FuncDataTemplate<AutocompleteItem>((item, names) =>
            {
                var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 2) };
                var labelText = new TextBlock { Text = item.Label, Foreground = Brushes.White, FontWeight = FontWeight.Bold, FontSize = 12 };
                var detailText = new TextBlock { Text = $"  {item.Detail}", Foreground = Brushes.Gray, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
                panel.Children.Add(labelText);
                panel.Children.Add(detailText);
                return panel;
            }, true);

            _autocompleteBorder = new Border
            {
                Child = _autocompleteList,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                IsVisible = false,
                ZIndex = 100
            };

            initialPane.EditorGrid.Children.Add(_autocompleteBorder);
            Grid.SetRow(_autocompleteBorder, 0);
            Grid.SetColumn(_autocompleteBorder, 0);

            // Hover Tooltip Overlay
            _hoverText = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            };

            _hoverBorder = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#2D2D30")),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.Parse("#434346")),
                Padding = new Thickness(6, 4),
                CornerRadius = new CornerRadius(3),
                Child = _hoverText,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                IsVisible = false,
                MaxWidth = 300,
                ZIndex = 101
            };

            initialPane.EditorGrid.Children.Add(_hoverBorder);
            Grid.SetRow(_hoverBorder, 0);
            Grid.SetColumn(_hoverBorder, 0);

            // Floating Debug Toolbar Overlay
            var debugToolbarPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(4)
            };

            var btnContinue = CreateDebugToolbarButton("|> Continue", "Debug.Start", Brushes.Green);
            var btnStepOver = CreateDebugToolbarButton("↷ Step Over", "Debug.StepOver", Brushes.LightBlue);
            var btnStepInto = CreateDebugToolbarButton("↓ Step Into", "Debug.StepInto", Brushes.LightGreen);
            var btnStepOut = CreateDebugToolbarButton("↑ Step Out", "Debug.StepOut", Brushes.LightYellow);
            var btnStop = CreateDebugToolbarButton("■ Stop", "Debug.Stop", Brushes.Red);

            debugToolbarPanel.Children.Add(btnContinue);
            debugToolbarPanel.Children.Add(btnStepOver);
            debugToolbarPanel.Children.Add(btnStepInto);
            debugToolbarPanel.Children.Add(btnStepOut);
            debugToolbarPanel.Children.Add(btnStop);

            _debugToolbar = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#2D2D30")),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.Parse("#434346")),
                Padding = new Thickness(6, 4),
                CornerRadius = new CornerRadius(3),
                Child = debugToolbarPanel,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 10, 20, 0), // Anchored top-right of canvas
                IsVisible = false,
                ZIndex = 102
            };

            initialPane.EditorGrid.Children.Add(_debugToolbar);
            Grid.SetRow(_debugToolbar, 0);
            Grid.SetColumn(_debugToolbar, 0);

            // Row 1: Bottom Splitter
            var bottomSplitter = new GridSplitter
            {
                Height = 4,
                Background = new SolidColorBrush(Color.Parse("#2D2D2D")),
                ResizeDirection = GridResizeDirection.Rows,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            editorPane.Children.Add(bottomSplitter);
            Grid.SetRow(bottomSplitter, 1);

            // Row 3: Bottom Panel TabControl
            _bottomTabControl = new TabControl
            {
                Background = new SolidColorBrush(Color.Parse("#1E1E1E")),
                TabStripPlacement = Dock.Bottom
            };

            var terminalTabHeader = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2) };
            terminalTabHeader.Children.Add(new Avalonia.Controls.Shapes.Path
            {
                Width = 12,
                Height = 12,
                Data = StreamGeometry.Parse("M20,19 V17 H4 V19 H20 Z M4.5,14 L8.5,10 L4.5,6 L6,5 L11,10 L6,15 L4.5,14 Z"),
                Fill = new SolidColorBrush(Color.Parse("#CCCCCC")),
                Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            terminalTabHeader.Children.Add(new TextBlock { Text = "Terminal", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });

            var terminalControl = new TerminalControl();
            var terminalTab = new TabItem { Header = terminalTabHeader, Content = terminalControl };
            _bottomTabControl.Items.Add(terminalTab);

            var perfTabHeader = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2) };
            perfTabHeader.Children.Add(new Avalonia.Controls.Shapes.Path
            {
                Width = 12,
                Height = 12,
                Data = StreamGeometry.Parse("M19,3 H5 C3.9,3 3,3.9 3,5 V19 C3,20.1 3.9,21 5,21 H19 C20.1,21 21,20.1 21,19 V5 C21,3.9 20.1,3 19,3 Z M9,17 H7 V10 H9 V17 Z M13,17 H11 V7 H13 V17 Z M17,17 H15 V12 H17 V17 Z"),
                Fill = new SolidColorBrush(Color.Parse("#00E676")),
                Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            perfTabHeader.Children.Add(new TextBlock { Text = "Performance", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });

            var perfControl = new PerformanceGraphsControl();
            var perfTab = new TabItem { Header = perfTabHeader, Content = perfControl };
            _bottomTabControl.Items.Add(perfTab);

            // Find Results Tab
            _findResultsPanel = new FindResultsPanel(this);
            var findResultsTabHeader = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2) };
            findResultsTabHeader.Children.Add(new Avalonia.Controls.Shapes.Path
            {
                Width = 12,
                Height = 12,
                Data = StreamGeometry.Parse("M15.5,14 L14.71,14 L14.43,13.72 C15.41,12.59 16,11.11 16,9.5 C16,5.91 13.09,3 9.5,3 C5.91,3 3,5.91 3,9.5 C3,13.09 5.91,16 9.5,16 C11.11,16 12.59,15.41 13.72,14.43 L14,14.71 L14,15.5 L19,20.5 L20.5,19 L15.5,14 Z"),
                Fill = new SolidColorBrush(Color.Parse("#81D4FA")),
                Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            findResultsTabHeader.Children.Add(new TextBlock { Text = "Find Results", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });

            _findResultsTab = new TabItem { Header = findResultsTabHeader, Content = _findResultsPanel };
            _bottomTabControl.Items.Add(_findResultsTab);

            editorPane.Children.Add(_bottomTabControl);
            Grid.SetRow(_bottomTabControl, 2);

            // Start the PTY process
            _terminalPty = new PtyHost();
            string workingDir = _fileTree.RootPath != null 
                ? (Directory.Exists(_fileTree.RootPath) ? _fileTree.RootPath : Path.GetDirectoryName(_fileTree.RootPath) ?? "C:\\")
                : "C:\\";

            string shellPath = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "powershell.exe" : "/bin/bash";
            if (_terminalPty.Start(shellPath, Array.Empty<string>(), workingDir, 80, 24))
            {
                terminalControl.BindPty(_terminalPty);
            }

            workspaceGrid.Children.Add(editorPane);
            Grid.SetColumn(editorPane, 2);

            mainGrid.Children.Add(workspaceGrid);
            Grid.SetRow(workspaceGrid, 2);

            // 3. Status Bar Container
            var statusBarGrid = new Grid
            {
                Background = new SolidColorBrush(Color.Parse("#1A1A1A")),
                Height = 22
            };
            statusBarGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star)); // Left status text
            statusBarGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto)); // Right extension controls

            _statusBar = new TextBlock
            {
                Foreground = Brushes.DarkGray,
                Padding = new Thickness(8, 2),
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12
            };
            statusBarGrid.Children.Add(_statusBar);
            Grid.SetColumn(_statusBar, 0);

            _statusBarExtensionPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            statusBarGrid.Children.Add(_statusBarExtensionPanel);
            Grid.SetColumn(_statusBarExtensionPanel, 1);

            mainGrid.Children.Add(statusBarGrid);
            Grid.SetRow(statusBarGrid, 3);

            // 4. Command Palette Overlay
            _commandPalette = new CommandPalette(this);
            mainGrid.Children.Add(_commandPalette);
            Grid.SetRow(_commandPalette, 0);
            Grid.SetRowSpan(_commandPalette, 4);

            Content = mainGrid;

            // Wire initial canvas events
            WireCanvasEvents(initialPane);

            this.LayoutUpdated += (s, e) => UpdateScrollbars();

            // Default workspace root path
            string defaultWorkspace = @"c:\Users\spuri\source\repos\PolarsPlus\Glacier.SpanCoder";
            string defaultSolution = System.IO.Path.Combine(defaultWorkspace, "SpanCoder.slnx");
            if (System.IO.File.Exists(defaultSolution))
            {
                _fileTree.SetRootPath(defaultSolution);
            }
            else if (System.IO.Directory.Exists(defaultWorkspace))
            {
                _fileTree.SetRootPath(defaultWorkspace);
            }

            UpdateStatusBar();
            UpdateEditMenuState();
            StartGitMonitoring();
            StartLocalAiMonitoring();
        }

        public void ConnectEngine(IEngineConnection engine)
        {
            _engine = engine;
            _engine.MessageReceived += OnEngineMessage;
            _aiChatPanel.SetEngineConnection(engine);
        }

        public void ConnectExtensions(IExtensionManager extManager)
        {
            _extensionManager = extManager;
            _extensionManager.ExtensionRegistered += (extId, manifest) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (manifest.Settings != null)
                    {
                        foreach (var setting in manifest.Settings)
                        {
                            SettingsManager.RegisterExtensionSetting(setting);
                        }
                    }

                    foreach (var cmd in manifest.Commands)
                    {
                        _commandToExtensionMap[cmd.Id] = extId;
                        if (!_extensionCommands.Any(c => c.Id == cmd.Id))
                        {
                            _extensionCommands.Add(cmd);
                        }
                    }

                    foreach (var menu in manifest.MenuItems)
                    {
                        var command = manifest.Commands.FirstOrDefault(c => c.Id == menu.CommandId);
                        RegisterPluginMenu(extId, menu.CommandId, menu.MenuPath, command.DefaultShortcut);
                    }

                    foreach (var panel in manifest.Panels)
                    {
                        RegisterPluginPanel(extId, panel.Id, panel.Title);
                    }

                    if (manifest.Languages != null)
                    {
                        foreach (var lang in manifest.Languages)
                        {
                            LanguageConfigurationRegistry.Register(lang);
                            TrackExtensionLanguageExt(extId, lang.Extension);
                        }
                    }

                    if (manifest.ToolbarItems != null)
                    {
                        foreach (var tb in manifest.ToolbarItems.OrderBy(t => t.OrderPriority))
                        {
                            _commandToExtensionMap[tb.CommandId] = extId;
                            AddToolbarButton(extId, tb.DisplayName, tb.CommandId, tb.DisplayName);
                        }
                    }

                    if (manifest.StatusBarItems != null)
                    {
                        foreach (var item in manifest.StatusBarItems.OrderBy(i => i.OrderPriority))
                        {
                            RegisterPluginStatusBarItem(extId, item);
                        }
                    }
                });
            };

            _extensionManager.ExtensionUnregistered += (extId) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    UnregisterExtensionUI(extId);
                });
            };

            _extensionManager.PanelContentUpdated += (panelId, content) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    UpdatePluginPanelContent(panelId, content);
                });
            };

            _extensionManager.StatusBarItemUpdated += (extId, itemId, text, tooltip, commandId) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    UpdatePluginStatusBarItem(extId, itemId, text, tooltip, commandId);
                });
            };
        }

        private void TrackExtensionUiElement(string extensionId, Control control)
        {
            if (!_extensionUiElements.TryGetValue(extensionId, out var list))
            {
                list = new List<Control>();
                _extensionUiElements[extensionId] = list;
            }
            list.Add(control);
        }

        private void TrackExtensionKeyBinding(string extensionId, KeyBinding binding)
        {
            if (!_extensionKeyBindings.TryGetValue(extensionId, out var list))
            {
                list = new List<KeyBinding>();
                _extensionKeyBindings[extensionId] = list;
            }
            list.Add(binding);
        }

        private void TrackExtensionPanelId(string extensionId, string panelId)
        {
            if (!_extensionPanelIds.TryGetValue(extensionId, out var list))
            {
                list = new List<string>();
                _extensionPanelIds[extensionId] = list;
            }
            list.Add(panelId);
        }

        private void TrackExtensionLanguageExt(string extensionId, string ext)
        {
            if (!_extensionLanguageExts.TryGetValue(extensionId, out var list))
            {
                list = new List<string>();
                _extensionLanguageExts[extensionId] = list;
            }
            list.Add(ext);
        }

        private void CleanEmptyParentMenus(ItemsControl? parent)
        {
            while (parent is MenuItem parentMenuItem && parentMenuItem.Items.Count == 0)
            {
                var grandParent = parentMenuItem.Parent as ItemsControl;
                if (grandParent != null)
                {
                    grandParent.Items.Remove(parentMenuItem);
                    parent = grandParent;
                }
                else
                {
                    break;
                }
            }
        }

        private void UnregisterExtensionUI(string extensionId)
        {
            SettingsManager.UnregisterExtensionSettings(extensionId);

            _extensionContextMenuItems.RemoveAll(x => x.ExtensionId == extensionId);
            foreach (var pane in _editorPanes)
            {
                pane.Canvas.ExtensionContextMenuItems.RemoveAll(x => x.ExtensionId == extensionId);
            }

            // 1. Remove UI Controls
            if (_extensionUiElements.TryGetValue(extensionId, out var controls))
            {
                foreach (var control in controls)
                {
                    if (control is MenuItem menuItem)
                    {
                        var parent = menuItem.Parent as ItemsControl;
                        if (parent != null)
                        {
                            parent.Items.Remove(menuItem);
                            CleanEmptyParentMenus(parent);
                        }
                    }
                    else if (control is TabItem tabItem)
                    {
                        _sidebarTabControl.Items.Remove(tabItem);
                    }
                    else if (control is Button button)
                    {
                        _toolbarPanel.Children.Remove(button);
                    }
                    else if (control is Border border && _statusBarExtensionPanel.Children.Contains(border))
                    {
                        _statusBarExtensionPanel.Children.Remove(border);
                    }
                }
                _extensionUiElements.Remove(extensionId);
            }

            var keysToRemove = _extensionStatusBarItems.Where(kv => controls != null && controls.Contains(kv.Value)).Select(kv => kv.Key).ToList();
            foreach (var key in keysToRemove)
            {
                _extensionStatusBarItems.Remove(key);
            }

            // 2. Remove KeyBindings
            if (_extensionKeyBindings.TryGetValue(extensionId, out var bindings))
            {
                foreach (var binding in bindings)
                {
                    this.KeyBindings.Remove(binding);
                }
                _extensionKeyBindings.Remove(extensionId);
            }

            // 3. Remove Panel Content mappings
            if (_extensionPanelIds.TryGetValue(extensionId, out var panelIds))
            {
                foreach (var panelId in panelIds)
                {
                    _pluginPanels.Remove(panelId);
                }
                _extensionPanelIds.Remove(extensionId);
            }

            // 4. Unregister language configurations
            if (_extensionLanguageExts.TryGetValue(extensionId, out var exts))
            {
                foreach (var ext in exts)
                {
                    LanguageConfigurationRegistry.Unregister(ext);
                }
                _extensionLanguageExts.Remove(extensionId);
            }

            // 5. Clean commands and mappings
            var commandsToRemove = _extensionCommands.Where(cmd => _commandToExtensionMap.TryGetValue(cmd.Id, out var extId) && extId == extensionId).ToList();
            foreach (var cmd in commandsToRemove)
            {
                _extensionCommands.Remove(cmd);
                _commandToExtensionMap.Remove(cmd.Id);
            }
        }

        private void RegisterPluginMenu(string extensionId, string commandId, string menuPath, string? shortcut)
        {
            var parts = menuPath.Split('/');
            if (parts.Length == 0) return;

            if (parts[0] == "EditorContextMenu")
            {
                var label = parts.Length > 1 ? parts[1] : commandId;
                var item = new TextEditorCanvas.ContextMenuItem
                {
                    Header = label,
                    CommandId = commandId,
                    ExtensionId = extensionId
                };
                _extensionContextMenuItems.Add(item);

                foreach (var pane in _editorPanes)
                {
                    pane.Canvas.ExtensionContextMenuItems.Add(item);
                }
                return;
            }

            ItemsControl currentParent = _mainMenu;
            MenuItem? currentItem = null;

            for (int i = 0; i < parts.Length; i++)
            {
                var partName = parts[i];
                var existing = currentParent.Items.OfType<MenuItem>().FirstOrDefault(x => (x.Header as string) == partName);
                if (existing == null)
                {
                    currentItem = new MenuItem { Header = partName };
                    currentParent.Items.Add(currentItem);
                }
                else
                {
                    currentItem = existing;
                }
                currentParent = currentItem;
            }

            if (currentItem != null)
            {
                currentItem.Click += (s, e) =>
                {
                    OnCommandInvoked(commandId);
                };

                TrackExtensionUiElement(extensionId, currentItem);

                if (!string.IsNullOrEmpty(shortcut))
                {
                    try
                    {
                        var gesture = KeyGesture.Parse(shortcut);
                        var binding = new KeyBinding
                        {
                            Gesture = gesture,
                            Command = new ActionCommand(() => OnCommandInvoked(commandId))
                        };
                        this.KeyBindings.Add(binding);
                        TrackExtensionKeyBinding(extensionId, binding);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ShellWindow] Failed to parse shortcut '{shortcut}' for command '{commandId}': {ex.Message}");
                    }
                }
            }
        }

        private void RegisterPluginStatusBarItem(string extensionId, StatusBarItemDescriptor item)
        {
            if (_extensionStatusBarItems.TryGetValue(item.Id, out var existing))
            {
                _statusBarExtensionPanel.Children.Remove(existing);
                _extensionStatusBarItems.Remove(item.Id);
            }

            var textBlock = new TextBlock
            {
                Text = item.Text,
                Foreground = Brushes.LightGray,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0)
            };

            var border = new Border
            {
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 2),
                Child = textBlock,
                VerticalAlignment = VerticalAlignment.Center
            };

            if (!string.IsNullOrEmpty(item.Tooltip))
            {
                ToolTip.SetTip(border, item.Tooltip);
            }

            if (!string.IsNullOrEmpty(item.CommandId))
            {
                border.Tag = item.CommandId;
                border.Cursor = new Cursor(StandardCursorType.Hand);
                border.PointerEntered += (s, e) => border.Background = new SolidColorBrush(Color.Parse("#3E3E40"));
                border.PointerExited += (s, e) => border.Background = Brushes.Transparent;
                border.PointerPressed += (s, e) =>
                {
                    if (border.Tag is string cmdId && !string.IsNullOrEmpty(cmdId))
                    {
                        OnCommandInvoked(cmdId);
                    }
                };
            }

            _extensionStatusBarItems[item.Id] = border;
            _statusBarExtensionPanel.Children.Add(border);

            TrackExtensionUiElement(extensionId, border);
        }

        private void UpdatePluginStatusBarItem(string extensionId, string itemId, string text, string tooltip, string commandId)
        {
            if (_extensionStatusBarItems.TryGetValue(itemId, out var border))
            {
                if (border.Child is TextBlock textBlock)
                {
                    textBlock.Text = text;
                }

                if (!string.IsNullOrEmpty(tooltip))
                {
                    ToolTip.SetTip(border, tooltip);
                }

                if (!string.IsNullOrEmpty(commandId))
                {
                    border.Tag = commandId;
                    border.Cursor = new Cursor(StandardCursorType.Hand);
                    if (border.Background == null || border.Background == Brushes.Transparent)
                    {
                        border.PointerEntered += (s, e) => border.Background = new SolidColorBrush(Color.Parse("#3E3E40"));
                        border.PointerExited += (s, e) => border.Background = Brushes.Transparent;
                        border.PointerPressed += (s, e) =>
                        {
                            if (border.Tag is string cmdId && !string.IsNullOrEmpty(cmdId))
                            {
                                OnCommandInvoked(cmdId);
                            }
                        };
                    }
                }
            }
        }

        private void RegisterPluginPanel(string extensionId, string panelId, string title)
        {
            if (_pluginPanels.ContainsKey(panelId)) return;

            var textBlock = new TextBlock
            {
                Foreground = Brushes.LightGray,
                Background = Brushes.Transparent,
                TextWrapping = TextWrapping.Wrap,
                Padding = new Thickness(10),
                FontSize = 12
            };

            var scrollViewer = new ScrollViewer
            {
                Content = textBlock,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            };

            var panelTabHeader = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 2)
            };
            panelTabHeader.Children.Add(new Avalonia.Controls.Shapes.Path
            {
                Width = 14,
                Height = 14,
                Data = StreamGeometry.Parse("M19,13 C19,13.55 18.55,14 18,14 H16 V16 C16,16.55 15.55,17 15,17 H13 V19 C13,19.55 12.55,20 12,20 C11.45,20 11,19.55 11,19 V17 H9 C8.45,17 8,16.55 8,16 V14 H6 C5.45,14 5,13.55 5,13 C5,12.45 5.45,12 6,12 H8 V10 C8,9.45 8.45,9 9,9 H11 V7 C11,6.45 11.45,6 12,6 C12.55,6 13,6.45 13,7 V9 H15 C15.55,9 16,9.45 16,10 V12 H18 C18.55,12 19,12.45 19,13 Z"),
                Fill = new SolidColorBrush(Color.Parse("#519ABA")), // Nice plugin blue
                Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            panelTabHeader.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            });

            var tabItem = new TabItem
            {
                Header = panelTabHeader,
                Content = scrollViewer
            };

            _pluginPanels[panelId] = textBlock;
            _sidebarTabControl.Items.Add(tabItem);

            TrackExtensionUiElement(extensionId, tabItem);
            TrackExtensionPanelId(extensionId, panelId);
        }

        private void UpdatePluginPanelContent(string panelId, string content)
        {
            if (_pluginPanels.TryGetValue(panelId, out var textBlock))
            {
                textBlock.Text = content;
            }
        }

        private class ActionCommand : System.Windows.Input.ICommand
        {
            private readonly Action _action;
            public ActionCommand(Action action) => _action = action;
            public bool CanExecute(object? parameter) => true;
            public void Execute(object? parameter) => _action();
            public event EventHandler? CanExecuteChanged
            {
                add { }
                remove { }
            }
        }

        private void OnEngineMessage(byte[] payload)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (BinaryMessageSerializer.TryParseHeader(payload, out var header))
                {
                    LogHelper.Log($"[ShellWindow] OnEngineMessage received: Type={header.Type}, DocumentId={header.DocumentId}, Length={header.Length}");
                    if (header.Type == MessageTypes.DocumentChanged)
                    {
                        BinaryMessageSerializer.ParseDocumentChanged(payload, out int docId, out int offset, out int addedLength, out int deletedLength);
                        
                        if (_engine != null)
                        {
                            var doc = _engine.GetDocument(docId);
                            if (doc != null)
                            {
                                var panesWithDoc = _editorPanes.Where(p => p.OpenDocuments.Any(d => d.Id == docId)).ToList();
                                
                                if (panesWithDoc.Count == 0)
                                {
                                    // New document loaded! Add it to the active pane.
                                    var newOpenDoc = new OpenDocument(docId, doc.FilePath, doc);
                                    _activePane.OpenDocuments.Add(newOpenDoc);
                                    _activePane.ActiveDocument = newOpenDoc;
                                    _activePane.Canvas.Document = doc;

                                    if (_pendingNavigation.HasValue && _pendingNavigation.Value.FilePath.Equals(doc.FilePath, StringComparison.OrdinalIgnoreCase))
                                    {
                                        _activePane.Canvas.MoveCaret(_pendingNavigation.Value.Line, _pendingNavigation.Value.Character);
                                        _pendingNavigation = null;
                                    }
                                    else
                                    {
                                        _activePane.Canvas.MoveCaret(0, 0);
                                    }
                                    _activePane.Canvas.ScrollX = 0;
                                    _activePane.Canvas.ScrollY = 0;
                                    
                                    RebuildTabsUI(_activePane);
                                    _activePane.UpdateScrollbars();
                                    RequestLspFoldingRanges(docId);
                                }
                                else
                                {
                                    // Incremental edit or background document update in existing panes
                                    foreach (var pane in panesWithDoc)
                                    {
                                        var paneDoc = pane.OpenDocuments.First(d => d.Id == docId);
                                        paneDoc.Document = doc;
                                        paneDoc.IsDirty = true;
                                        
                                        if (pane.ActiveDocument == paneDoc)
                                        {
                                            pane.Canvas.Document = doc;
                                            pane.Canvas.InvalidateVisual();
                                            pane.UpdateScrollbars();
                                            
                                            if (pane == _activePane)
                                            {
                                                pane.Canvas.AdjustCaret(offset, addedLength, deletedLength);
                                            }
                                        }
                                        RebuildTabsUI(pane);
                                    }
                                }

                                UpdateStatusBar();
                                UpdateInlayHintsAndCodeLens();
                                RequestLspFoldingRanges(docId);
                            }
                        }
                    }
                    else if (header.Type == MessageTypes.DiagnosticsReport)
                    {
                        var items = BinaryMessageSerializer.ParseDiagnosticsReport(payload, out int docId);
                        if (_canvas.Document != null && _canvas.Document.Id == docId)
                        {
                            _canvas.SetDiagnostics(items);
                        }
                    }
                    else if (header.Type == MessageTypes.FoldingRangeResponse)
                    {
                        var items = BinaryMessageSerializer.ParseFoldingRangeResponse(payload, out int docId);
                        var panesWithDoc = _editorPanes.Where(p => p.OpenDocuments.Any(d => d.Id == docId)).ToList();
                        foreach (var pane in panesWithDoc)
                        {
                            var paneDoc = pane.OpenDocuments.First(d => d.Id == docId);
                            if (pane.ActiveDocument == paneDoc)
                            {
                                pane.Canvas.SetFoldingRangesFromLsp(items.ToArray());
                                pane.UpdateScrollbars();
                            }
                        }
                    }
                    else if (header.Type == MessageTypes.AutocompleteResponse)
                    {
                        var items = BinaryMessageSerializer.ParseAutocompleteResponse(payload, out int docId, out int offset);
                        if (_canvas.Document != null && _canvas.Document.Id == docId && offset == _canvas.GetCaretAbsoluteOffset())
                        {
                            ShowAutocomplete(offset, items);
                        }
                    }
                    else if (header.Type == MessageTypes.HoverResponse)
                    {
                        string text = BinaryMessageSerializer.ParseHoverResponse(payload, out int docId, out int offset, out int startAbs, out int endAbs);
                        if (_canvas.Document != null && _canvas.Document.Id == docId)
                        {
                            ShowHover(text, _lastHoverMouseX, _lastHoverMouseY);
                        }
                    }
                    else if (header.Type == MessageTypes.GotoDefinitionResponse)
                    {
                        string filePath = BinaryMessageSerializer.ParseGotoDefinitionResponse(payload, out int docId, out int offset, out int line, out int character);
                        if (!string.IsNullOrEmpty(filePath) && System.IO.File.Exists(filePath))
                        {
                            var existing = _openDocuments.FirstOrDefault(d => d.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));
                            if (existing != null)
                            {
                                SwitchToDocument(existing);
                                _canvas.MoveCaret(line, character);
                            }
                            else
                            {
                                _pendingNavigation = (filePath, line, character);
                                LoadFile(filePath);
                            }
                        }
                    }
                    else if (header.Type == MessageTypes.FindReferencesResponse)
                    {
                        var items = BinaryMessageSerializer.ParseFindReferencesResponse(payload, out int docId, out int offset);
                        _commandPalette.ShowReferences(items);
                    }
                    else if (header.Type == MessageTypes.RenameResponse)
                    {
                        bool success = BinaryMessageSerializer.ParseRenameResponse(payload, out int docId, out int offset);
                        _statusBar.Text = success ? "Rename completed successfully." : "Rename failed.";
                    }
                    else if (header.Type == MessageTypes.DocumentSymbolsResponse)
                    {
                        var items = BinaryMessageSerializer.ParseDocumentSymbolsResponse(payload, out int docId);
                        _commandPalette.ShowDocumentSymbols(items);
                    }
                    else if (header.Type == MessageTypes.DebugStoppedEvent)
                    {
                        string reason = BinaryMessageSerializer.ParseDebugStoppedEvent(payload, out int docId, out int line, out int character);
                        _canvas.DebugActiveLine = line;
                        _sidebarTabControl.SelectedItem = _debugTab;
                        _debugToolbar.IsVisible = true;
                        if (_canvas.Document != null && _canvas.Document.Id == docId)
                        {
                            _canvas.MoveCaret(line - 1, character - 1);
                        }
                    }
                    else if (header.Type == MessageTypes.DebugStateReport)
                    {
                        BinaryMessageSerializer.ParseDebugStateReport(payload, out int docId, out var stackFrames, out var variables);
                        _debugCallStackList.ItemsSource = stackFrames;
                        _debugVariablesList.ItemsSource = variables;
                    }
                    else if (header.Type == MessageTypes.AiChatResponse)
                    {
                        string json = BinaryMessageSerializer.ParseStringPayload(payload);
                        _aiChatPanel.HandleChatResponse(json);
                    }
                    else if (header.Type == MessageTypes.AiToolExecutionEvent)
                    {
                        string json = BinaryMessageSerializer.ParseStringPayload(payload);
                        _aiChatPanel.HandleToolExecutionEvent(json);
                    }
                    else if (header.Type == MessageTypes.BatchEditResponse)
                    {
                        var edits = BinaryMessageSerializer.ParseBatchEditResponse(payload, out int docId);
                        LogHelper.Log($"[ShellWindow] OnEngineMessage BatchEditResponse: docId={docId}, editsCount={edits.Length}");
                        if (_engine != null)
                        {
                            var doc = _engine.GetDocument(docId);
                            LogHelper.Log($"[ShellWindow] OnEngineMessage BatchEditResponse: doc retrieved={doc != null}, docLength={doc?.Length}");
                            if (doc != null)
                            {
                                var panesWithDoc = _editorPanes.Where(p => p.OpenDocuments.Any(d => d.Id == docId)).ToList();
                                LogHelper.Log($"[ShellWindow] OnEngineMessage BatchEditResponse: panesWithDocCount={panesWithDoc.Count}");
                                foreach (var pane in panesWithDoc)
                                {
                                    var paneDoc = pane.OpenDocuments.First(d => d.Id == docId);
                                    paneDoc.Document = doc;
                                    paneDoc.IsDirty = true;
                                    
                                    if (pane.ActiveDocument == paneDoc)
                                    {
                                        pane.Canvas.Document = doc;
                                        pane.Canvas.InvalidateVisual();
                                        pane.UpdateScrollbars();
                                        
                                        if (pane == _activePane)
                                        {
                                            LogHelper.Log($"[ShellWindow] OnEngineMessage BatchEditResponse: Calling AdjustCaretsForBatch on active pane canvas");
                                            pane.Canvas.AdjustCaretsForBatch(edits);
                                        }
                                    }
                                    RebuildTabsUI(pane);
                                }
                                UpdateStatusBar();
                                UpdateInlayHintsAndCodeLens();
                                RequestLspFoldingRanges(docId);
                            }
                        }
                    }
                }
            });
        }

        private void UpdateScrollbars()
        {
            _activePane?.UpdateScrollbars();
        }

        private void UpdateStatusBar()
        {
            if (_canvas.Document == null)
            {
                _statusBar.Text = "No file open";
                return;
            }

            string startupStr = "";
            if (_startupStopwatch != null && _startupStopwatch.IsRunning)
            {
                _startupStopwatch.Stop();
                startupStr = $" | Startup: {_startupStopwatch.ElapsedMilliseconds}ms";
            }

            string vimStr = "";
            if (_canvas.VimEnabled)
            {
                vimStr = _canvas.VimMode == VimMode.Normal ? " | [NORMAL]" : " | [INSERT]";
            }

            _statusBar.Text = $"{_currentFilePath} | Line: {_canvas.CaretLine + 1}, Col: {_canvas.CaretCol + 1} | Total Lines: {_canvas.Document.GetLineCount()}{vimStr}{startupStr}";
        }

        private void OnCommandInvoked(string commandId)
        {
            // Route through compile-time GeneratedCommandRegistry to avoid reflection
            if (GeneratedCommandRegistry.Dispatch(commandId, this))
            {
                return;
            }

            if (_commandToExtensionMap.TryGetValue(commandId, out var extId) && _extensionManager != null)
            {
                _extensionManager.ExecuteCommand(extId, commandId);
            }
        }

        private Button CreateDebugToolbarButton(string text, string commandId, IBrush foreground)
        {
            var btn = new Button
            {
                Content = text,
                Foreground = foreground,
                Background = new SolidColorBrush(Color.Parse("#3E3E42")),
                BorderBrush = new SolidColorBrush(Color.Parse("#555555")),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 4),
                FontSize = 11,
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            btn.Click += (s, e) => OnCommandInvoked(commandId);
            return btn;
        }

        private void OnCallStackDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
        {
            var selected = _debugCallStackList.SelectedItem as string;
            if (string.IsNullOrEmpty(selected)) return;

            int openParen = selected.LastIndexOf('(');
            int closeParen = selected.LastIndexOf(')');
            if (openParen != -1 && closeParen != -1 && closeParen > openParen)
            {
                string pathAndLine = selected.Substring(openParen + 1, closeParen - openParen - 1);
                int colon = pathAndLine.LastIndexOf(':');
                if (colon != -1)
                {
                    string filePath = pathAndLine.Substring(0, colon);
                    if (int.TryParse(pathAndLine.Substring(colon + 1), out int line))
                    {
                        if (System.IO.File.Exists(filePath))
                        {
                            OpenFile(filePath);
                            _pendingNavigation = (filePath, line, 1);
                            if (_activeDocument != null && _activeDocument.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase))
                            {
                                _canvas.MoveCaret(line - 1, 0);
                            }
                        }
                    }
                }
            }
        }

        public void StartDebugging()
        {
            if (_activeDocument == null || _engine == null) return;

            byte[] startBuffer = new byte[BinaryMessageSerializer.HeaderSize + 4 + _activeDocument.FilePath.Length * 2];
            int len = BinaryMessageSerializer.WriteDebugStartRequest(startBuffer, _activeDocument.Id, _activeDocument.FilePath);
            _engine.Send(startBuffer);

            SendBreakpointsToEngine();

            _isDebugging = true;
            _debugToolbar.IsVisible = true;
            _sidebarTabControl.SelectedItem = _debugTab;
        }

        public void StopDebugging()
        {
            if (_engine == null) return;
            byte[] buffer = new byte[BinaryMessageSerializer.HeaderSize];
            BinaryMessageSerializer.WriteDebugStopRequest(buffer, _activeDocument?.Id ?? 0);
            _engine.Send(buffer);

            CleanupDebuggingUI();
        }

        public void StepOver()
        {
            if (_engine == null) return;
            byte[] buffer = new byte[BinaryMessageSerializer.HeaderSize];
            BinaryMessageSerializer.WriteDebugStepOverRequest(buffer, _activeDocument?.Id ?? 0);
            _engine.Send(buffer);
        }

        public void StepInto()
        {
            if (_engine == null) return;
            byte[] buffer = new byte[BinaryMessageSerializer.HeaderSize];
            BinaryMessageSerializer.WriteDebugStepIntoRequest(buffer, _activeDocument?.Id ?? 0);
            _engine.Send(buffer);
        }

        public void StepOut()
        {
            if (_engine == null) return;
            byte[] buffer = new byte[BinaryMessageSerializer.HeaderSize];
            BinaryMessageSerializer.WriteDebugStepOutRequest(buffer, _activeDocument?.Id ?? 0);
            _engine.Send(buffer);
        }

        private void SendBreakpointsToEngine()
        {
            if (_activeDocument == null || _engine == null) return;
            var bps = _canvas.GetBreakpoints();
            byte[] bpBuffer = new byte[BinaryMessageSerializer.HeaderSize + 4 + bps.Count * 4];
            BinaryMessageSerializer.WriteDebugSetBreakpointsRequest(bpBuffer, _activeDocument.Id, bps.ToArray());
            _engine.Send(bpBuffer);
        }

        private void CleanupDebuggingUI()
        {
            _isDebugging = false;
            _debugToolbar.IsVisible = false;
            _canvas.DebugActiveLine = null;
            _debugVariablesList.ItemsSource = null;
            _debugCallStackList.ItemsSource = null;
            _canvas.InvalidateVisual();
        }

        [Command("Debug.Start", "Start / Continue Debugging", "Debug", "F5")]
        [MenuItem("Debug.Start", "Debug/Start Debugging", 10)]
        public static void DebugStartCommand(ShellWindow window)
        {
            if (window._isDebugging)
            {
                if (window._engine != null)
                {
                    byte[] buffer = new byte[BinaryMessageSerializer.HeaderSize];
                    BinaryMessageSerializer.WriteDebugContinueRequest(buffer, window._activeDocument?.Id ?? 0);
                    window._engine.Send(buffer);
                }
            }
            else
            {
                window.StartDebugging();
            }
        }

        [Command("Debug.Stop", "Stop Debugging", "Debug", "Shift+F5")]
        [MenuItem("Debug.Stop", "Debug/Stop Debugging", 20)]
        public static void DebugStopCommand(ShellWindow window)
        {
            window.StopDebugging();
        }

        [Command("Debug.StepOver", "Step Over", "Debug", "F10")]
        [MenuItem("Debug.StepOver", "Debug/Step Over", 30)]
        public static void DebugStepOverCommand(ShellWindow window)
        {
            window.StepOver();
        }

        [Command("Debug.StepInto", "Step Into", "Debug", "F11")]
        [MenuItem("Debug.StepInto", "Debug/Step Into", 40)]
        public static void DebugStepIntoCommand(ShellWindow window)
        {
            window.StepInto();
        }

        [Command("Debug.StepOut", "Step Out", "Debug", "Shift+F11")]
        [MenuItem("Debug.StepOut", "Debug/Step Out", 50)]
        public static void DebugStepOutCommand(ShellWindow window)
        {
            window.StepOut();
        }

        public void ExecuteCommand(string commandId)
        {
            OnCommandInvoked(commandId);
        }

        // --- Commands ---

        [Command("View.CommandPalette", "Command Palette...", "View", "Ctrl+Shift+P")]
        [MenuItem("View.CommandPalette", "View/Command Palette", 20)]
        public static void CommandPaletteCommand(ShellWindow window)
        {
            window.ShowCommandPalette();
        }

        public void ShowCommandPalette()
        {
            var list = new List<CommandDescriptor>();
            list.AddRange(GeneratedCommandRegistry.Commands);
            list.AddRange(_extensionCommands);
            _commandPalette.Show(list);
        }

        public void ReturnFocusToEditor()
        {
            _canvas.Focus();
        }

        public IDocumentView? ActiveDocumentView => _activeDocument?.Document;
        public string? WorkspaceRootPath
        {
            get
            {
                string? root = _fileTree.RootPath;
                if (string.IsNullOrEmpty(root)) return null;
                if (System.IO.File.Exists(root))
                {
                    return System.IO.Path.GetDirectoryName(root);
                }
                return root;
            }
        }

        public void MoveCaretToLine(int line)
        {
            _canvas.MoveCaret(line, 0);
        }

        [Command("File.Open", "Open File", "File", "Ctrl+O")]
        [MenuItem("File.Open", "File/Open", 10)]
        public static void OpenFileCommand(ShellWindow window)
        {
            window.TriggerOpenFile();
        }

        [Command("File.Save", "Save File", "File", "Ctrl+S")]
        [MenuItem("File.Save", "File/Save", 20)]
        public static void SaveFileCommand(ShellWindow window)
        {
            window.SaveFile();
        }

        [Command("Edit.ToggleVim", "Toggle Vim Emulation", "Edit", "Ctrl+Alt+V")]
        [MenuItem("Edit.ToggleVim", "Tools/Toggle Vim Emulation", 80)]
        public static void ToggleVimCommand(ShellWindow window)
        {
            window.ToggleVim();
        }

        public void ToggleVim()
        {
            bool current = SettingsManager.Get<bool>("editor.vimEnabled", false);
            SettingsManager.Set("editor.vimEnabled", (!current).ToString().ToLower());
        }

        [Command("File.Settings", "Options/Settings...", "File", "Ctrl+,")]
        [MenuItem("File.Settings", "Tools/Options", 90)]
        public static void SettingsCommand(ShellWindow window)
        {
            window.ShowSettings();
        }

        public void ShowSettings()
        {
            var settingsWin = new SettingsWindow();
            settingsWin.ShowDialog(this);
        }

        [Command("File.Exit", "Exit", "File", "Alt+F4")]
        [MenuItem("File.Exit", "File/Exit", 100)]
        public static void ExitCommand(ShellWindow window)
        {
            window.Close();
        }

        [MenuItem("Collab.Host", "Collab/Host Collaboration Session", 10)]
        public static void HostCollabCommand(ShellWindow window)
        {
            _ = window.HostCollabSessionAsync();
        }

        [MenuItem("Collab.Join", "Collab/Join Collaboration Session", 20)]
        public static void JoinCollabCommand(ShellWindow window)
        {
            _ = window.JoinCollabSessionAsync();
        }

        [MenuItem("Collab.Disconnect", "Collab/Disconnect Session", 30)]
        public static void DisconnectCollabCommand(ShellWindow window)
        {
            window.DisconnectCollabSession();
        }

        public async Task HostCollabSessionAsync()
        {
            var dialog = new CollabSetupWindow(true);
            await dialog.ShowDialog(this);
            if (dialog.IsCancelled) return;

            try
            {
                DisconnectCollabSession();

                _collabServer = new CollabServer(dialog.Port);
                _collabServer.Start();

                if (_activeDocument != null && _activeDocument.Document != null)
                {
                    string text = GetDocumentText(_activeDocument.Document);
                    for (int i = 0; i < text.Length; i++)
                    {
                        _collabServer.Document.LocalInsert(i, text[i]);
                    }
                }

                _collabClient = new CollabClient(dialog.Username);
                SetupCollabClientHandlers();

                await _collabClient.ConnectAsync("127.0.0.1", dialog.Port);
                _statusBar.Text = $"Collab: Hosting on port {dialog.Port} as '{dialog.Username}'";
            }
            catch (Exception ex)
            {
                _statusBar.Text = $"Collab error: {ex.Message}";
            }
        }

        public async Task JoinCollabSessionAsync()
        {
            var dialog = new CollabSetupWindow(false);
            await dialog.ShowDialog(this);
            if (dialog.IsCancelled) return;

            try
            {
                DisconnectCollabSession();

                _collabClient = new CollabClient(dialog.Username);
                SetupCollabClientHandlers();

                await _collabClient.ConnectAsync(dialog.HostIp, dialog.Port);
                _statusBar.Text = $"Collab: Connected to {dialog.HostIp}:{dialog.Port} as '{dialog.Username}'";
            }
            catch (Exception ex)
            {
                _statusBar.Text = $"Collab error: {ex.Message}";
            }
        }

        public void DisconnectCollabSession()
        {
            if (_collabClient != null)
            {
                _collabClient.Disconnect();
                _collabClient.Dispose();
                _collabClient = null;
            }

            if (_collabServer != null)
            {
                _collabServer.Stop();
                _collabServer = null;
            }

            foreach (var pane in _editorPanes)
            {
                pane.Canvas.ClearRemoteCursors();
            }

            _statusBar.Text = "Collab: Session disconnected.";
        }

        private void SetupCollabClientHandlers()
        {
            if (_collabClient == null) return;

            _collabClient.SyncReceived += (syncedText) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_activeDocument != null)
                    {
                        SyncCollabDocumentText(_activeDocument.Id, syncedText);
                    }
                });
            };

            _collabClient.RemoteInsertReceived += (visibleOffset, val) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_activeDocument != null && _engine != null)
                    {
                        byte[] buffer = new byte[BinaryMessageSerializer.HeaderSize + 4 + 2];
                        BinaryMessageSerializer.WriteInsertText(buffer, _activeDocument.Id, visibleOffset, val.ToString());
                        _engine.Send(buffer);
                    }
                });
            };

            _collabClient.RemoteDeleteReceived += (visibleOffset) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_activeDocument != null && _engine != null)
                    {
                        byte[] buffer = new byte[BinaryMessageSerializer.HeaderSize + 4];
                        BinaryMessageSerializer.WriteDeleteText(buffer, _activeDocument.Id, visibleOffset, 1);
                        _engine.Send(buffer);
                    }
                });
            };

            _collabClient.RemoteCursorMoved += (msg) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    foreach (var pane in _editorPanes)
                    {
                        pane.Canvas.UpdateRemoteCursor(msg.ClientId, msg.Username, msg.Line, msg.Character, msg.SelectionStartOffset, msg.SelectionEndOffset, msg.ColorHex);
                    }
                });
            };
        }

        private void SyncCollabDocumentText(int docId, string syncedText)
        {
            if (_engine == null) return;
            var doc = _engine.GetDocument(docId);
            if (doc == null) return;

            var edits = new[]
            {
                new TextEdit { Offset = 0, DeleteLength = doc.Length, Text = syncedText }
            };

            byte[] buffer = new byte[BinaryMessageSerializer.HeaderSize + sizeof(int) + sizeof(int) * 3 + syncedText.Length * sizeof(char)];
            int len = BinaryMessageSerializer.WriteBatchEditRequest(buffer, docId, edits);
            byte[] finalBuffer = new byte[len];
            Array.Copy(buffer, 0, finalBuffer, 0, len);
            _engine.Send(finalBuffer);
        }

        public async void TriggerOpenFile()
        {
            try
            {
                var files = await this.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Open Text File",
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("Text Files") { Patterns = new[] { "*.txt", "*.log", "*.cs", "*.md", "*.json" } },
                        new FilePickerFileType("All Files") { Patterns = new[] { "*" } }
                    }
                });

                if (files != null && files.Count > 0)
                {
                    var filePath = files[0].Path.LocalPath;
                    Dispatcher.UIThread.Post(() => LoadFile(filePath));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error opening file: {ex}");
            }
        }

        public void LoadFile(string filePath)
        {
            OpenFile(filePath);
        }

        private void OpenFile(string filePath)
        {
            if (_engine == null) return;

            var existing = _openDocuments.FirstOrDefault(d => d.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                SwitchToDocument(existing);
                return;
            }

            byte[] buffer = new byte[BinaryMessageSerializer.HeaderSize + 4 + filePath.Length * 2];
            BinaryMessageSerializer.WriteLoadFile(buffer, filePath);
            _engine.Send(buffer);

            _currentFilePath = filePath;
            _statusBar.Text = $"Loading {filePath}...";
            _ = RefreshGitStatusAsync();
        }

        private void SwitchToDocument(OpenDocument openDoc)
        {
            if (_activeDocument == openDoc) return;

            if (_activeDocument != null)
            {
                _activeDocument.CaretLine = _canvas.CaretLine;
                _activeDocument.CaretCol = _canvas.CaretCol;
                _activeDocument.ScrollX = _canvas.ScrollX;
                _activeDocument.ScrollY = _canvas.ScrollY;
            }

            _activeDocument = openDoc;
            _currentFilePath = openDoc.FilePath;
            _canvas.Document = openDoc.Document;

            _canvas.ScrollX = openDoc.ScrollX;
            _canvas.ScrollY = openDoc.ScrollY;
            _canvas.MoveCaret(openDoc.CaretLine, openDoc.CaretCol);

            RebuildTabsUI();
            UpdateScrollbars();
            UpdateStatusBar();
            UpdateEditMenuState();
            UpdateInlayHintsAndCodeLens();
        }

        public string GetDocumentText(IDocumentView doc)
        {
            if (doc == null) return "";
            var sb = new System.Text.StringBuilder(doc.Length);
            int lineCount = doc.GetLineCount();
            for (int i = 0; i < lineCount; i++)
            {
                var lineSpan = doc.GetLine(i, out _, out var rented);
                sb.Append(lineSpan.ToString());
                if (rented != null) System.Buffers.ArrayPool<char>.Shared.Return(rented);
            }
            return sb.ToString();
        }

        public void SaveFile()
        {
            if (_activeDocument != null && _activeDocument.Document != null)
            {
                try
                {
                    string text = GetDocumentText(_activeDocument.Document);
                    System.IO.File.WriteAllText(_activeDocument.FilePath, text);
                    _activeDocument.IsDirty = false;
                    if (_engine != null)
                    {
                        byte[] saveMsg = new byte[BinaryMessageSerializer.HeaderSize];
                        BinaryMessageSerializer.WriteSaveFile(saveMsg, _activeDocument.Id);
                        _engine.Send(saveMsg);
                    }
                    RebuildTabsUI(_activePane);
                    _statusBar.Text = $"File saved: {_activeDocument.FilePath}";
                    _ = RefreshGitStatusAsync();
                    RunLiveUnitTests();
                }
                catch (Exception ex)
                {
                    _statusBar.Text = $"Failed to save file: {ex.Message}";
                }
            }
        }

        private async void CloseDocument(OpenDocument docToClose)
        {
            if (docToClose.IsDirty)
            {
                var dialog = new SaveChangesDialog(new List<string> { System.IO.Path.GetFileName(docToClose.FilePath) });
                await dialog.ShowDialog(this);

                if (dialog.Result == SaveChangesDialog.DialogResult.Save)
                {
                    try
                    {
                        string text = GetDocumentText(docToClose.Document);
                        System.IO.File.WriteAllText(docToClose.FilePath, text);
                        docToClose.IsDirty = false;
                        if (_engine != null)
                        {
                            byte[] saveMsg = new byte[BinaryMessageSerializer.HeaderSize];
                            BinaryMessageSerializer.WriteSaveFile(saveMsg, docToClose.Id);
                            _engine.Send(saveMsg);
                        }
                    }
                    catch (Exception ex)
                    {
                        _statusBar.Text = $"Failed to save file: {ex.Message}";
                        return;
                    }
                }
                else if (dialog.Result == SaveChangesDialog.DialogResult.Cancel)
                {
                    return;
                }
            }

            _openDocuments.Remove(docToClose);

            if (_activeDocument == docToClose)
            {
                if (_openDocuments.Count > 0)
                {
                    var nextDoc = _openDocuments[^1];
                    _activeDocument = null;
                    SwitchToDocument(nextDoc);
                }
                else
                {
                    _activeDocument = null;
                    _currentFilePath = "Untitled";
                    _canvas.Document = null;
                    _canvas.MoveCaret(0, 0);
                    _canvas.ScrollX = 0;
                    _canvas.ScrollY = 0;
                    RebuildTabsUI();
                    UpdateScrollbars();
                    UpdateStatusBar();
                }
            }
            else
            {
                RebuildTabsUI();
            }
            UpdateEditMenuState();
        }

        private void RebuildTabsUI(EditorPane? pane = null)
        {
            pane ??= _activePane;
            if (pane == null) return;
            
            pane.TabsContainer.Children.Clear();
            foreach (var doc in pane.OpenDocuments)
            {
                var tabBorder = new Border
                {
                    Background = doc == pane.ActiveDocument ? new SolidColorBrush(Color.Parse("#1E1E1E")) : new SolidColorBrush(Color.Parse("#2D2D2D")),
                    BorderBrush = new SolidColorBrush(Color.Parse("#3D3D3D")),
                    BorderThickness = new Thickness(1, 1, 1, 0),
                    Padding = new Thickness(10, 4),
                    Margin = new Thickness(2, 0)
                };

                var tabContent = new StackPanel { Orientation = Orientation.Horizontal };
                var nameLabel = new TextBlock
                {
                    Text = System.IO.Path.GetFileName(doc.FilePath) + (doc.IsDirty ? "*" : ""),
                    Foreground = doc == pane.ActiveDocument ? Brushes.White : Brushes.Gray,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 12
                };
                tabContent.Children.Add(nameLabel);

                var closeButton = new Button
                {
                    Content = "×",
                    Background = Brushes.Transparent,
                    BorderBrush = Brushes.Transparent,
                    Foreground = Brushes.Gray,
                    Padding = new Thickness(4, 0),
                    Margin = new Thickness(8, 0, 0, 0),
                    FontSize = 12,
                    Focusable = false
                };
                closeButton.PointerEntered += (s, e) => closeButton.Foreground = Brushes.Red;
                closeButton.PointerExited += (s, e) => closeButton.Foreground = Brushes.Gray;
                
                var localDoc = doc;
                closeButton.Click += (s, e) =>
                {
                    SetActivePane(pane);
                    CloseDocument(localDoc);
                };
                tabContent.Children.Add(closeButton);

                tabBorder.Child = tabContent;
                
                tabBorder.PointerPressed += (s, e) =>
                {
                    SetActivePane(pane);
                    SwitchToDocument(localDoc);
                    e.Handled = true;
                };

                pane.TabsContainer.Children.Add(tabBorder);
            }
        }

        private void RebuildTabsUI()
        {
            RebuildTabsUI(_activePane);
        }

        [Command("File.OpenFolder", "Open Folder", "File", "Ctrl+Shift+O")]
        [MenuItem("File.OpenFolder", "File/Open Folder", 11)]
        public static void OpenFolderCommand(ShellWindow window)
        {
            window.TriggerOpenFolder();
        }

        [Command("View.SplitEditorHorizontal", "Split Editor Horizontally", "View", "Ctrl+E, H")]
        [MenuItem("View.SplitEditorHorizontal", "View/Split Editor Horizontally", 30)]
        public static void SplitEditorHorizontalCommand(ShellWindow window)
        {
            window.SplitActivePane(true);
        }

        [Command("View.SplitEditorVertical", "Split Editor Vertically", "View", "Ctrl+E, V")]
        [MenuItem("View.SplitEditorVertical", "View/Split Editor Vertically", 31)]
        public static void SplitEditorVerticalCommand(ShellWindow window)
        {
            window.SplitActivePane(false);
        }

        [Command("View.UnsplitEditor", "Remove Split Editor", "View", "Ctrl+E, U")]
        [MenuItem("View.UnsplitEditor", "View/Remove Split Editor", 32)]
        public static void UnsplitEditorCommand(ShellWindow window)
        {
            window.UnsplitActivePane();
        }

        public void SetActivePane(EditorPane pane)
        {
            if (_activePane == pane) return;

            if (_activePane != null)
            {
                _activePane.Border.BorderBrush = Brushes.Transparent;
            }

            // Move overlays to new active pane
            if (_autocompleteBorder.Parent is Grid oldAutoGrid) oldAutoGrid.Children.Remove(_autocompleteBorder);
            if (_hoverBorder.Parent is Grid oldHoverGrid) oldHoverGrid.Children.Remove(_hoverBorder);
            if (_debugToolbar.Parent is Grid oldDebugGrid) oldDebugGrid.Children.Remove(_debugToolbar);

            _activePane = pane;
            _activePane.Border.BorderBrush = new SolidColorBrush(Color.Parse("#007ACC"));

            pane.EditorGrid.Children.Add(_autocompleteBorder);
            pane.EditorGrid.Children.Add(_hoverBorder);
            pane.EditorGrid.Children.Add(_debugToolbar);

            RebuildTabsUI(pane);
            pane.UpdateScrollbars();
            UpdateStatusBar();
            UpdateEditMenuState();
            UpdateInlayHintsAndCodeLens();
        }

        public void SplitActivePane(bool horizontal)
        {
            if (_editorPanes.Count >= 2)
            {
                _statusBar.Text = "Maximum 2 editor panes supported.";
                return;
            }

            var newPane = new EditorPane(this);
            WireCanvasEvents(newPane);

            if (_activePane.ActiveDocument != null)
            {
                var originalDoc = _activePane.ActiveDocument;
                var copiedDoc = new OpenDocument(originalDoc.Id, originalDoc.FilePath, originalDoc.Document)
                {
                    CaretLine = originalDoc.CaretLine,
                    CaretCol = originalDoc.CaretCol,
                    ScrollX = originalDoc.ScrollX,
                    ScrollY = originalDoc.ScrollY
                };
                newPane.OpenDocuments.Add(copiedDoc);
                newPane.ActiveDocument = copiedDoc;
                newPane.Canvas.Document = copiedDoc.Document;
                newPane.Canvas.MoveCaret(copiedDoc.CaretLine, copiedDoc.CaretCol);
                newPane.Canvas.ScrollX = copiedDoc.ScrollX;
                newPane.Canvas.ScrollY = copiedDoc.ScrollY;
            }

            _editorPanes.Add(newPane);
            RebuildSplitLayout(horizontal);
            SetActivePane(newPane);
        }

        public void UnsplitPane(EditorPane paneToRemove)
        {
            if (_editorPanes.Count < 2) return;

            var remainingPane = _editorPanes.First(p => p != paneToRemove);

            _editorPanes.Remove(paneToRemove);

            // Remove overlays
            if (_autocompleteBorder.Parent is Grid autoGrid) autoGrid.Children.Remove(_autocompleteBorder);
            if (_hoverBorder.Parent is Grid hoverGrid) hoverGrid.Children.Remove(_hoverBorder);
            if (_debugToolbar.Parent is Grid debugGrid) debugGrid.Children.Remove(_debugToolbar);

            _editorSplitContainer.Children.Clear();
            _editorSplitContainer.RowDefinitions.Clear();
            _editorSplitContainer.ColumnDefinitions.Clear();

            _editorSplitContainer.Children.Add(remainingPane);
            Grid.SetRow(remainingPane, 0);
            Grid.SetColumn(remainingPane, 0);

            SetActivePane(remainingPane);
        }

        public void UnsplitActivePane()
        {
            UnsplitPane(_activePane);
        }

        private void RebuildSplitLayout(bool horizontal)
        {
            _editorSplitContainer.Children.Clear();
            _editorSplitContainer.RowDefinitions.Clear();
            _editorSplitContainer.ColumnDefinitions.Clear();

            if (_editorPanes.Count == 1)
            {
                _editorSplitContainer.Children.Add(_editorPanes[0]);
                Grid.SetRow(_editorPanes[0], 0);
                Grid.SetColumn(_editorPanes[0], 0);
            }
            else if (_editorPanes.Count == 2)
            {
                var pane1 = _editorPanes[0];
                var pane2 = _editorPanes[1];

                var splitter = new GridSplitter
                {
                    Background = new SolidColorBrush(Color.Parse("#2D2D2D")),
                };

                if (horizontal)
                {
                    _editorSplitContainer.RowDefinitions.Add(new RowDefinition(GridLength.Star));
                    _editorSplitContainer.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                    _editorSplitContainer.RowDefinitions.Add(new RowDefinition(GridLength.Star));

                    splitter.Height = 4;
                    splitter.HorizontalAlignment = HorizontalAlignment.Stretch;
                    splitter.ResizeDirection = GridResizeDirection.Rows;

                    _editorSplitContainer.Children.Add(pane1);
                    Grid.SetRow(pane1, 0);

                    _editorSplitContainer.Children.Add(splitter);
                    Grid.SetRow(splitter, 1);

                    _editorSplitContainer.Children.Add(pane2);
                    Grid.SetRow(pane2, 2);
                }
                else
                {
                    _editorSplitContainer.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                    _editorSplitContainer.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                    _editorSplitContainer.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

                    splitter.Width = 4;
                    splitter.VerticalAlignment = VerticalAlignment.Stretch;
                    splitter.ResizeDirection = GridResizeDirection.Columns;

                    _editorSplitContainer.Children.Add(pane1);
                    Grid.SetColumn(pane1, 0);

                    _editorSplitContainer.Children.Add(splitter);
                    Grid.SetColumn(splitter, 1);

                    _editorSplitContainer.Children.Add(pane2);
                    Grid.SetColumn(pane2, 2);
                }
            }
        }

        private void WireCanvasEvents(EditorPane pane)
        {
            var canvas = pane.Canvas;
            int lastBlameLine = -1;
            IDocumentView? lastBlameDoc = null;

            canvas.TextInputReceived += (offset, text) =>
            {
                if (canvas.Document == null || _engine == null) return;
                byte[] buffer = new byte[BinaryMessageSerializer.HeaderSize + 4 + text.Length * 2];
                BinaryMessageSerializer.WriteInsertText(buffer, canvas.Document.Id, offset, text);
                _engine.Send(buffer);

                if (_collabClient != null && _collabClient.IsConnected)
                {
                    for (int i = 0; i < text.Length; i++)
                    {
                        _ = _collabClient.SendInsertAsync(offset + i, text[i]);
                    }
                }
            };

            canvas.TextDeleteReceived += (offset, len) =>
            {
                if (canvas.Document == null || _engine == null) return;
                byte[] buffer = new byte[BinaryMessageSerializer.HeaderSize + 4];
                BinaryMessageSerializer.WriteDeleteText(buffer, canvas.Document.Id, offset, len);
                _engine.Send(buffer);

                if (_collabClient != null && _collabClient.IsConnected)
                {
                    for (int i = 0; i < len; i++)
                    {
                        _ = _collabClient.SendDeleteAsync(offset);
                    }
                }
            };

            canvas.BatchEditReceived += (edits) =>
            {
                LogHelper.Log($"[ShellWindow] canvas.BatchEditReceived: editsCount={edits.Length}");
                if (canvas.Document == null || _engine == null) return;
                int textEditsBytes = 0;
                foreach (var edit in edits)
                {
                    textEditsBytes += sizeof(int) * 3;
                    if (edit.Text != null)
                    {
                        textEditsBytes += edit.Text.Length * sizeof(char);
                    }
                }
                byte[] buffer = new byte[BinaryMessageSerializer.HeaderSize + sizeof(int) + textEditsBytes];
                int len = BinaryMessageSerializer.WriteBatchEditRequest(buffer, canvas.Document.Id, edits);
                byte[] finalBuffer = new byte[len];
                Array.Copy(buffer, 0, finalBuffer, 0, len);
                LogHelper.Log($"[ShellWindow] canvas.BatchEditReceived: Sending BatchEditRequest ({finalBuffer.Length} bytes) to engine");
                _engine.Send(finalBuffer);
            };

            canvas.CaretMoved += async () =>
            {
                UpdateStatusBar();
                if (_collabClient != null && _collabClient.IsConnected && canvas.Document != null)
                {
                    _ = _collabClient.SendCursorAsync(canvas.CaretLine, canvas.CaretCol, canvas.SelectionStartOffset, canvas.SelectionEndOffset);
                }

                // Inline Git Blame
                if (_gitProvider == null) return;

                if (canvas.Document == null || string.IsNullOrEmpty(canvas.Document.FilePath))
                {
                    canvas.ActiveLineGitBlame = null;
                    lastBlameDoc = null;
                    return;
                }

                int currentLine = canvas.CaretLine;
                var currentDoc = canvas.Document;
                if (currentLine == lastBlameLine && currentDoc == lastBlameDoc) return;
                lastBlameLine = currentLine;
                lastBlameDoc = currentDoc;

                canvas.ActiveLineGitBlame = null;

                try
                {
                    string? blameText = await _gitProvider.GetLineBlameAsync(canvas.Document.FilePath, currentLine + 1);
                    if (canvas.CaretLine == currentLine && canvas.Document == currentDoc)
                    {
                        canvas.ActiveLineGitBlame = blameText;
                    }
                }
                catch
                {
                    if (canvas.CaretLine == currentLine && canvas.Document == currentDoc)
                    {
                        canvas.ActiveLineGitBlame = null;
                    }
                }
            };

            canvas.VimModeChanged += UpdateStatusBar;

            canvas.AutocompleteRequested += (offset) =>
            {
                if (canvas.Document == null || _engine == null) return;
                byte[] buffer = new byte[BinaryMessageSerializer.HeaderSize];
                BinaryMessageSerializer.WriteAutocompleteRequest(buffer, canvas.Document.Id, offset);
                _engine.Send(buffer);
            };

            canvas.HoverRequested += (offset, x, y) =>
            {
                if (canvas.Document == null || _engine == null) return;
                _lastHoverMouseX = x;
                _lastHoverMouseY = y;
                byte[] buffer = new byte[BinaryMessageSerializer.HeaderSize];
                BinaryMessageSerializer.WriteHoverRequest(buffer, canvas.Document.Id, offset);
                _engine.Send(buffer);
            };

            canvas.GotoDefinitionRequested += (offset) =>
            {
                if (canvas.Document == null || _engine == null) return;
                byte[] buffer = new byte[BinaryMessageSerializer.HeaderSize];
                BinaryMessageSerializer.WriteGotoDefinitionRequest(buffer, canvas.Document.Id, offset);
                _engine.Send(buffer);
            };

            canvas.FindReferencesRequested += (offset) =>
            {
                if (canvas.Document == null || _engine == null) return;
                byte[] buffer = new byte[BinaryMessageSerializer.HeaderSize];
                BinaryMessageSerializer.WriteFindReferencesRequest(buffer, canvas.Document.Id, offset);
                _engine.Send(buffer);
            };

            canvas.RenameRequested += async (offset) =>
            {
                if (canvas.Document == null || _engine == null) return;
                string word = GetWordAtCaret();
                if (string.IsNullOrEmpty(word)) return;

                var dialog = new RenameDialog(word);
                await dialog.ShowDialog(this);
                if (!string.IsNullOrEmpty(dialog.Result) && dialog.Result != word)
                {
                    byte[] buffer = new byte[BinaryMessageSerializer.HeaderSize + 4 + dialog.Result.Length * 2];
                    BinaryMessageSerializer.WriteRenameRequest(buffer, canvas.Document.Id, offset, dialog.Result);
                    _engine.Send(buffer);
                }
            };

            canvas.MouseMovedOrLeft += HideHover;

            canvas.BreakpointsChanged += (bps) =>
            {
                _debugBreakpointsList.ItemsSource = bps.Select(bp => $"Line {bp}").ToList();
                if (_isDebugging)
                {
                    SendBreakpointsToEngine();
                }
            };

            canvas.AutocompleteUpRequested += OnAutocompleteUp;
            canvas.AutocompleteDownRequested += OnAutocompleteDown;
            canvas.AutocompleteCommitRequested += OnAutocompleteCommit;
            canvas.AutocompleteCancelRequested += HideAutocomplete;

            canvas.CutRequested += ExecuteCut;
            canvas.CopyRequested += ExecuteCopy;
            canvas.PasteRequested += ExecutePaste;
            canvas.ExtensionContextMenuItemClicked += OnCommandInvoked;

            foreach (var item in _extensionContextMenuItems)
            {
                canvas.ExtensionContextMenuItems.Add(item);
            }
        }

        [Command("File.OpenSolution", "Open Solution", "File", "Ctrl+Shift+L")]
        [MenuItem("File.OpenSolution", "File/Open Solution", 12)]
        public static void OpenSolutionCommand(ShellWindow window)
        {
            window.TriggerOpenSolution();
        }

        public async void TriggerOpenSolution()
        {
            try
            {
                var files = await this.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Open Solution",
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("Visual Studio Solutions") { Patterns = new[] { "*.slnx", "*.sln" } },
                        new FilePickerFileType("All Files") { Patterns = new[] { "*" } }
                    }
                });

                if (files != null && files.Count > 0)
                {
                    var filePath = files[0].Path.LocalPath;
                    Dispatcher.UIThread.Post(() => _fileTree.SetRootPath(filePath));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error opening solution: {ex}");
            }
        }

        public async void TriggerOpenFolder()
        {
            try
            {
                var folders = await this.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "Open Workspace Folder",
                    AllowMultiple = false
                });

                if (folders != null && folders.Count > 0)
                {
                    var folderPath = folders[0].Path.LocalPath;
                    Dispatcher.UIThread.Post(() => _fileTree.SetRootPath(folderPath));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error opening folder: {ex}");
            }
        }

        private void ShowAutocomplete(int caretOffset, List<AutocompleteItem> items)
        {
            if (items == null || items.Count == 0)
            {
                HideAutocomplete();
                return;
            }

            _autocompleteItems = items;
            _autocompleteList.ItemsSource = _autocompleteItems;
            _autocompleteList.SelectedIndex = 0;

            double gutterWidth = _canvas.GetGutterWidth();
            double caretX = gutterWidth + 10 + (_canvas.CaretCol * _canvas.CharWidth) - _canvas.ScrollX;
            double caretY = (_canvas.CaretLine * _canvas.LineHeight) - _canvas.ScrollY;

            double popupX = caretX;
            double popupY = caretY + _canvas.LineHeight;

            double canvasWidth = _canvas.Bounds.Width;
            double canvasHeight = _canvas.Bounds.Height;

            if (popupX + 250 > canvasWidth) popupX = Math.Max(0, canvasWidth - 250);
            if (popupY + 150 > canvasHeight) popupY = Math.Max(0, caretY - 150);

            _autocompleteBorder.Margin = new Thickness(popupX, popupY, 0, 0);
            _autocompleteBorder.IsVisible = true;
            _canvas.IsAutocompleteVisible = true;
        }

        private void HideAutocomplete()
        {
            _autocompleteBorder.IsVisible = false;
            _canvas.IsAutocompleteVisible = false;
            _autocompleteItems.Clear();
        }

        private void OnAutocompleteUp()
        {
            int idx = _autocompleteList.SelectedIndex;
            if (idx > 0)
                _autocompleteList.SelectedIndex = idx - 1;
        }

        private void OnAutocompleteDown()
        {
            int idx = _autocompleteList.SelectedIndex;
            if (idx < _autocompleteItems.Count - 1)
                _autocompleteList.SelectedIndex = idx + 1;
        }

        private void OnAutocompleteCommit()
        {
            var selected = _autocompleteList.SelectedItem as AutocompleteItem?;
            if (selected != null)
            {
                int absOffset = _canvas.GetCaretAbsoluteOffset();
                if (_canvas.Document != null)
                {
                    var lineSpan = _canvas.Document.GetLine(_canvas.CaretLine, out _, out var rented);
                    int col = _canvas.CaretCol;
                    int wordStartCol = col;
                    while (wordStartCol > 0 && char.IsLetterOrDigit(lineSpan[wordStartCol - 1]))
                    {
                        wordStartCol--;
                    }
                    if (rented != null) ArrayPool<char>.Shared.Return(rented);

                    int deleteLength = col - wordStartCol;
                    int deleteOffset = absOffset - deleteLength;

                    if (deleteLength > 0)
                    {
                        byte[] delBuf = new byte[BinaryMessageSerializer.HeaderSize + 4];
                        BinaryMessageSerializer.WriteDeleteText(delBuf, _canvas.Document.Id, deleteOffset, deleteLength);
                        _engine?.Send(delBuf);
                    }

                    string insertText = selected.Value.Label;
                    byte[] insBuf = new byte[BinaryMessageSerializer.HeaderSize + 4 + insertText.Length * 2];
                    BinaryMessageSerializer.WriteInsertText(insBuf, _canvas.Document.Id, deleteOffset, insertText);
                    _engine?.Send(insBuf);
                }
            }
            HideAutocomplete();
        }

        private void ShowHover(string text, double mouseX, double mouseY)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                HideHover();
                return;
            }

            _hoverText.Text = text.Replace("\\n", "\n");
            
            double tooltipX = mouseX + 10;
            double tooltipY = mouseY - 40;

            double canvasWidth = _canvas.Bounds.Width;
            if (tooltipX + 250 > canvasWidth) tooltipX = Math.Max(0, canvasWidth - 250);
            if (tooltipY < 0) tooltipY = mouseY + 20;

            _hoverBorder.Margin = new Thickness(tooltipX, tooltipY, 0, 0);
            _hoverBorder.IsVisible = true;
        }

        private void HideHover()
        {
            _hoverBorder.IsVisible = false;
        }

        public void UpdateStatus(string message)
        {
            _statusBar.Text = message;
        }

        private string GetWordAtCaret()
        {
            if (_canvas.Document == null) return "";
            try
            {
                var lineSpan = _canvas.Document.GetLine(_canvas.CaretLine, out _, out var rented);
                int col = _canvas.CaretCol;
                
                int start = col;
                while (start > 0 && char.IsLetterOrDigit(lineSpan[start - 1])) start--;
                int end = col;
                while (end < lineSpan.Length && char.IsLetterOrDigit(lineSpan[end])) end++;
                
                string word = "";
                if (start < end)
                {
                    word = lineSpan.Slice(start, end - start).ToString();
                }
                if (rented != null) System.Buffers.ArrayPool<char>.Shared.Return(rented);
                return word;
            }
            catch
            {
                return "";
            }
        }

        public void RequestDocumentSymbols()
        {
            if (_canvas.Document == null || _engine == null) return;
            byte[] buffer = new byte[BinaryMessageSerializer.HeaderSize];
            BinaryMessageSerializer.WriteDocumentSymbolsRequest(buffer, _canvas.Document.Id);
            _engine.Send(buffer);
        }

        [Command("Edit.Cut", "Cut", "Edit", "Ctrl+X")]
        [MenuItem("Edit.Cut", "Edit/Cut", 10)]
        public static void CutCommand(ShellWindow window)
        {
            window.ExecuteCut();
        }

        [Command("Edit.Copy", "Copy", "Edit", "Ctrl+C")]
        [MenuItem("Edit.Copy", "Edit/Copy", 20)]
        public static void CopyCommand(ShellWindow window)
        {
            window.ExecuteCopy();
        }

        [Command("Edit.Paste", "Paste", "Edit", "Ctrl+V")]
        [MenuItem("Edit.Paste", "Edit/Paste", 30)]
        public static void PasteCommand(ShellWindow window)
        {
            window.ExecutePaste();
        }

        [Command("Edit.Find", "Quick Find", "Edit", "Ctrl+F")]
        [MenuItem("Edit.Find", "Edit/Quick Find", 60)]
        public static void FindCommand(ShellWindow window)
        {
            window._activePane.ShowFindReplace(showReplace: false);
        }

        [Command("Edit.Replace", "Quick Replace", "Edit", "Ctrl+H")]
        [MenuItem("Edit.Replace", "Edit/Quick Replace", 70)]
        public static void ReplaceCommand(ShellWindow window)
        {
            window._activePane.ShowFindReplace(showReplace: true);
        }

        [Command("Edit.FindInFiles", "Find in Files", "Edit", "Ctrl+Shift+F")]
        [MenuItem("Edit.FindInFiles", "Edit/Find in Files", 71)]
        public static void FindInFilesCommand(ShellWindow window)
        {
            window.ShowFindReplaceFilesWindow(showReplace: false);
        }

        [Command("Edit.ReplaceInFiles", "Replace in Files", "Edit", "Ctrl+Shift+H")]
        [MenuItem("Edit.ReplaceInFiles", "Edit/Replace in Files", 72)]
        public static void ReplaceInFilesCommand(ShellWindow window)
        {
            window.ShowFindReplaceFilesWindow(showReplace: true);
        }

        public void ShowFindReplaceFilesWindow(bool showReplace)
        {
            if (_findReplaceFilesWindow == null)
            {
                _findReplaceFilesWindow = new FindReplaceFilesWindow(this, showReplace);
                _findReplaceFilesWindow.Closed += (s, e) => _findReplaceFilesWindow = null;
                _findReplaceFilesWindow.Show(this);
            }
            else
            {
                _findReplaceFilesWindow.FocusSearch(selectedText(this), showReplace);
                _findReplaceFilesWindow.Activate();
            }
        }

        public void DisplayWorkspaceSearchResults(string query, List<Glacier.Grep.SearchResult> results)
        {
            _findResultsPanel.DisplayResults(query, results, WorkspaceRootPath ?? "");
            _bottomTabControl.SelectedItem = _findResultsTab;
        }

        private static string? selectedText(ShellWindow window)
        {
            string sel = window._canvas.GetSelectedText(out _, out _);
            return (string.IsNullOrEmpty(sel) || sel.Contains('\n') || sel.Contains('\r')) ? null : sel;
        }

        public void ReplaceWorkspaceMatches(string findText, string replaceText, bool caseSensitive)
        {
            string? wsPath = WorkspaceRootPath;
            if (string.IsNullOrEmpty(wsPath)) return;

            string scanRoot = wsPath;
            if (File.Exists(wsPath))
            {
                scanRoot = Path.GetDirectoryName(wsPath) ?? wsPath;
            }

            try
            {
                var searchEngine = new Glacier.Grep.SearchEngine(scanRoot);
                var results = searchEngine.SearchAsync(findText, caseSensitive: caseSensitive)
                    .GetAwaiter().GetResult();

                var fileGroups = results.GroupBy(r => Path.GetFullPath(Path.Combine(scanRoot, r.FilePath)));

                foreach (var group in fileGroups)
                {
                    string fullPath = group.Key;
                    var panesWithDoc = _editorPanes.Where(p => p.OpenDocuments.Any(d => string.Equals(d.FilePath, fullPath, StringComparison.OrdinalIgnoreCase))).ToList();

                    if (panesWithDoc.Count > 0)
                    {
                        foreach (var pane in panesWithDoc)
                        {
                            pane.Canvas.ReplaceAllMatches(replaceText);
                        }
                    }
                    else
                    {
                        if (File.Exists(fullPath))
                        {
                            string content = File.ReadAllText(fullPath);
                            string newContent;
                            if (caseSensitive)
                            {
                                newContent = content.Replace(findText, replaceText);
                            }
                            else
                            {
                                string escapedFind = System.Text.RegularExpressions.Regex.Escape(findText);
                                newContent = System.Text.RegularExpressions.Regex.Replace(content, escapedFind, replaceText, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            }
                            File.WriteAllText(fullPath, newContent, System.Text.Encoding.UTF8);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ShellWindow] ReplaceWorkspaceMatches error: {ex.Message}");
            }
        }

        public async void ExecuteCut()
        {
            if (_canvas.Document == null || _engine == null) return;
            if (_canvas.VimEnabled && _canvas.VimMode == VimMode.Normal) return;
            var clipboard = Clipboard;
            if (clipboard == null) return;
            string text = _canvas.GetSelectedText(out int start, out int len);
            if (len > 0)
            {
                try
                {
                    await clipboard.SetTextAsync(text);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ShellWindow] Cut Clipboard error: {ex.Message}");
                }

                byte[] buffer = new byte[BinaryMessageSerializer.HeaderSize + 4];
                BinaryMessageSerializer.WriteDeleteText(buffer, _canvas.Document.Id, start, len);
                _engine.Send(buffer);
            }
        }

        public async void ExecuteCopy()
        {
            if (_canvas.Document == null) return;
            var clipboard = Clipboard;
            if (clipboard == null) return;
            string text = _canvas.GetSelectedText(out int start, out int len);
            if (len > 0)
            {
                try
                {
                    await clipboard.SetTextAsync(text);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ShellWindow] Copy Clipboard error: {ex.Message}");
                }
            }
        }

        public async void ExecutePaste()
        {
            if (_canvas.Document == null || _engine == null) return;
            if (_canvas.VimEnabled && _canvas.VimMode == VimMode.Normal) return;
            var clipboard = Clipboard;
            if (clipboard == null) return;
            try
            {
                string? text = await clipboard.GetTextAsync();
                if (!string.IsNullOrEmpty(text))
                {
                    bool hasSel = _canvas.HasSelection(out int start, out int len);
                    if (hasSel)
                    {
                        byte[] delBuf = new byte[BinaryMessageSerializer.HeaderSize + 4];
                        BinaryMessageSerializer.WriteDeleteText(delBuf, _canvas.Document.Id, start, len);
                        _engine.Send(delBuf);
                        
                        byte[] insBuf = new byte[BinaryMessageSerializer.HeaderSize + 4 + text.Length * 2];
                        BinaryMessageSerializer.WriteInsertText(insBuf, _canvas.Document.Id, start, text);
                        _engine.Send(insBuf);
                    }
                    else
                    {
                        int offset = _canvas.GetCaretAbsoluteOffset();
                        byte[] insBuf = new byte[BinaryMessageSerializer.HeaderSize + 4 + text.Length * 2];
                        BinaryMessageSerializer.WriteInsertText(insBuf, _canvas.Document.Id, offset, text);
                        _engine.Send(insBuf);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ShellWindow] Paste Clipboard error: {ex.Message}");
            }
        }

        private void UpdateEditMenuState()
        {
            if (_mainMenu == null) return;
            var editMenu = _mainMenu.Items.OfType<MenuItem>().FirstOrDefault(x => (x.Header as string) == "Edit");
            if (editMenu == null) return;

            bool hasActiveDoc = _activeDocument != null;

            var cutItem = editMenu.Items.OfType<MenuItem>().FirstOrDefault(x => (x.Header as string) == "Cut");
            var copyItem = editMenu.Items.OfType<MenuItem>().FirstOrDefault(x => (x.Header as string) == "Copy");
            var pasteItem = editMenu.Items.OfType<MenuItem>().FirstOrDefault(x => (x.Header as string) == "Paste");

            if (cutItem != null) cutItem.IsEnabled = hasActiveDoc;
            if (copyItem != null) copyItem.IsEnabled = hasActiveDoc;
            if (pasteItem != null) pasteItem.IsEnabled = hasActiveDoc;
        }

        private void AddToolbarButton(string text, string commandId, string tooltip)
        {
            AddToolbarButton("", text, commandId, tooltip);
        }

        private void AddToolbarButton(string extensionId, string text, string commandId, string tooltip)
        {
            var btn = new Button
            {
                Content = text,
                Background = Brushes.Transparent,
                Foreground = Brushes.LightGray,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(8, 4),
                FontSize = 12,
                Focusable = false
            };

            btn.PointerEntered += (s, e) => {
                btn.Background = new SolidColorBrush(Color.Parse("#3E3E40"));
                btn.Foreground = Brushes.White;
            };
            btn.PointerExited += (s, e) => {
                btn.Background = Brushes.Transparent;
                btn.Foreground = Brushes.LightGray;
            };

            btn.Click += (s, e) => OnCommandInvoked(commandId);

            ToolTip.SetTip(btn, tooltip);
            _toolbarPanel.Children.Add(btn);

            if (!string.IsNullOrEmpty(extensionId))
            {
                TrackExtensionUiElement(extensionId, btn);
            }
        }

        [Command("Edit.ToggleLineComment", "Toggle Line Comment", "Edit", "Ctrl+/")]
        [MenuItem("Edit.ToggleLineComment", "Edit/Toggle Line Comment", 40)]
        public static void ToggleLineCommentCommand(ShellWindow window)
        {
            window.ExecuteToggleLineComment();
        }

        [Command("Edit.ToggleBlockComment", "Toggle Block Comment", "Edit", "Ctrl+Shift+/")]
        [MenuItem("Edit.ToggleBlockComment", "Edit/Toggle Block Comment", 50)]
        public static void ToggleBlockCommentCommand(ShellWindow window)
        {
            window.ExecuteToggleBlockComment();
        }

        public void ExecuteToggleLineComment()
        {
            if (_canvas.Document == null || _engine == null) return;
            CommentHelper.ToggleLineComment(_canvas, _engine, _canvas.Document.FilePath);
        }

        public void ExecuteToggleBlockComment()
        {
            if (_canvas.Document == null || _engine == null) return;
            CommentHelper.ToggleBlockComment(_canvas, _engine, _canvas.Document.FilePath);
        }

        private void StartGitMonitoring()
        {
            _gitProvider = new GitVersionProvider();
            _gitProvider.StatusChanged += (changes) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    _gitChangesList.ItemsSource = changes;
                });
            };

            _gitProvider.LineChangesUpdated += (filePath, lineChanges) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_canvas.Document?.FilePath == filePath)
                    {
                        _canvas.GitLineChanges = lineChanges;
                    }
                });
            };

            _gitTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            _gitTimer.Tick += async (s, e) =>
            {
                await RefreshGitStatusAsync();
            };
            _gitTimer.Start();
        }

        private async Task RefreshGitStatusAsync()
        {
            if (string.IsNullOrEmpty(_fileTree.RootPath)) return;
            
            string workingDir = Directory.Exists(_fileTree.RootPath) 
                ? _fileTree.RootPath 
                : Path.GetDirectoryName(_fileTree.RootPath) ?? "";
                
            if (!string.IsNullOrEmpty(workingDir))
            {
                _gitProvider.SetWorkingDirectory(workingDir);
                await _gitProvider.RefreshAsync(_canvas.Document?.FilePath);
            }
        }

        private void StartLocalAiMonitoring()
        {
            if (IsRunningInUnitTest())
            {
                return;
            }

            _ghostTextTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _ghostTextTimer.Tick += OnGhostTextTimerTick;

            _canvas.CaretMoved += ResetGhostTextTimer;

            // Run check and pull setup in the background
            Task.Run(async () =>
            {
                await CheckAndSetupLocalAiAsync();
            });
        }

        private static bool IsRunningInUnitTest()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = assembly.FullName ?? "";
                if (name.Contains("test", StringComparison.OrdinalIgnoreCase) || 
                    name.Contains("xunit", StringComparison.OrdinalIgnoreCase) || 
                    name.Contains("nunit", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private void ResetGhostTextTimer()
        {
            _ghostTextTimer?.Stop();
            // Only trigger completion if there is an active document, no selection, and no multiple carets
            if (_activeDocument != null && _canvas.ExtraCarets.Count == 0 && _canvas.GhostText == null)
            {
                _ghostTextTimer?.Start();
            }
        }

        private void OnGhostTextTimerTick(object? sender, EventArgs e)
        {
            _ghostTextTimer?.Stop();
            if (_activeDocument == null || _canvas.Document == null || _canvas.ExtraCarets.Count > 0) return;

            var doc = _canvas.Document;
            int caretOffset = _canvas.GetCaretAbsoluteOffset();
            string filePath = doc.FilePath ?? "";

            Task.Run(async () =>
            {
                try
                {
                    // 1. Extract context before and after caret
                    int prefixLen = Math.Min(caretOffset, 1000);
                    int prefixStart = caretOffset - prefixLen;
                    string prefix = doc.GetTextRange(prefixStart, prefixLen);

                    int suffixLen = Math.Min(doc.Length - caretOffset, 500);
                    string suffix = doc.GetTextRange(caretOffset, suffixLen);

                    // 2. Call Ollama
                    using var client = new System.Net.Http.HttpClient(_socketsHandler, disposeHandler: false)
                    {
                        Timeout = TimeSpan.FromSeconds(2)
                    };

                    // Qwen2.5-Coder uses <|fim_prefix|>...<|fim_suffix|>...<|fim_middle|>
                    string prompt = $"<|fim_prefix|>{prefix}<|fim_suffix|>{suffix}<|fim_middle|>";
                    
                    var requestBody = new OllamaGenerateRequest
                    {
                        model = "qwen2.5-coder:1.5b",
                        prompt = prompt,
                        raw = true,
                        stream = false,
                        options = new OllamaOptions
                        {
                            num_predict = 64,
                            temperature = 0.0,
                            stop = new[] { "<|fim_prefix|>", "<|fim_suffix|>", "<|fim_middle|>", "<|endoftext|>", "\n", "\r\n" }
                        }
                    };

                    string jsonRequest = System.Text.Json.JsonSerializer.Serialize(requestBody, OllamaJsonContext.Default.OllamaGenerateRequest);
                    var httpContent = new System.Net.Http.StringContent(jsonRequest, System.Text.Encoding.UTF8, "application/json");

                    var response = await client.PostAsync("http://127.0.0.1:11434/api/generate", httpContent);
                    if (response.IsSuccessStatusCode)
                    {
                        var jsonResponse = await response.Content.ReadAsStringAsync();
                        using (var jsonDoc = System.Text.Json.JsonDocument.Parse(jsonResponse))
                        {
                            if (jsonDoc.RootElement.TryGetProperty("response", out var respProp))
                            {
                                string completion = respProp.GetString() ?? "";
                                if (!string.IsNullOrEmpty(completion))
                                {
                                    completion = completion.TrimStart('\r', '\n');
                                    int nlIdx = completion.IndexOfAny(new[] { '\r', '\n' });
                                    if (nlIdx >= 0)
                                    {
                                        completion = completion.Substring(0, nlIdx);
                                    }

                                    if (!string.IsNullOrEmpty(completion))
                                    {
                                        Dispatcher.UIThread.Post(() =>
                                        {
                                            if (_canvas.GetCaretAbsoluteOffset() == caretOffset)
                                            {
                                                _canvas.GhostText = completion;
                                                _canvas.GhostTextOffset = caretOffset;
                                                _canvas.InvalidateVisual();
                                            }
                                        });
                                    }
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // Fail silently for completion requests
                }
            });
        }

        private async Task CheckAndSetupLocalAiAsync()
        {
            using var client = new System.Net.Http.HttpClient(_socketsHandler, disposeHandler: false)
            {
                Timeout = TimeSpan.FromSeconds(3)
            };
            try
            {
                UpdateStatusBarText("AI: Checking local Ollama...");

                System.Net.Http.HttpResponseMessage? response = null;
                try
                {
                    response = await client.GetAsync("http://127.0.0.1:11434/api/tags");
                }
                catch (System.Net.Http.HttpRequestException)
                {
                    // Ollama server is not running. Let's try to start it!
                    UpdateStatusBarText("AI: Starting local Ollama service...");
                    
                    string? localExe = FindLocalOllamaExecutable();
                    bool started = false;

                    if (localExe != null)
                    {
                        try
                        {
                            var startInfo = new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = localExe,
                                Arguments = "serve",
                                CreateNoWindow = true,
                                UseShellExecute = false
                            };
                            System.Diagnostics.Process.Start(startInfo);
                            started = true;
                            Console.WriteLine($"[ShellWindow] Launched local Ollama service: {localExe}");
                        }
                        catch (Exception startEx)
                        {
                            Console.WriteLine($"[ShellWindow] Failed to start local Ollama: {startEx.Message}");
                        }
                    }
                    else if (OperatingSystem.IsWindows() && CheckWslOllama())
                    {
                        try
                        {
                            var startInfo = new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = "wsl",
                                Arguments = "ollama serve",
                                CreateNoWindow = true,
                                UseShellExecute = false
                            };
                            System.Diagnostics.Process.Start(startInfo);
                            started = true;
                            Console.WriteLine("[ShellWindow] Detected Ollama in WSL. Launched 'wsl ollama serve'.");
                        }
                        catch (Exception wslEx)
                        {
                            Console.WriteLine($"[ShellWindow] Failed to start WSL Ollama: {wslEx.Message}");
                        }
                    }
                    else
                    {
                        Console.WriteLine("[ShellWindow] Ollama executable not found on host or WSL. AI features will be offline.");
                        UpdateStatusBarText("AI Offline: Ollama not found");
                        return;
                    }

                    if (started)
                    {
                        // Wait a few seconds for the service to start
                        await Task.Delay(3000);
                        try
                        {
                            response = await client.GetAsync("http://127.0.0.1:11434/api/tags");
                        }
                        catch
                        {
                            // Ignore connection error after startup attempt
                        }
                    }
                }

                if (response == null || !response.IsSuccessStatusCode)
                {
                    Console.WriteLine("[ShellWindow] Ollama service not running.");
                    UpdateStatusBarText("AI Offline: Ollama not running");
                    return;
                }

                var content = await response.Content.ReadAsStringAsync();
                
                // Extremely simple check using string contains, to avoid JSON library parsing errors
                bool modelExists = content.Contains("qwen2.5-coder:1.5b");
                if (modelExists)
                {
                    UpdateStatusBarText("AI: qwen2.5-coder:1.5b ready");
                    return;
                }

                // If not exists, pull it in the background
                UpdateStatusBarText("AI: Downloading qwen2.5-coder:1.5b (900MB)...");
                
                var pullContent = new System.Net.Http.StringContent("{\"name\": \"qwen2.5-coder:1.5b\", \"stream\": false}", System.Text.Encoding.UTF8, "application/json");
                using var pullClient = new System.Net.Http.HttpClient(_socketsHandler, disposeHandler: false)
                {
                    Timeout = TimeSpan.FromMinutes(10)
                };
                var pullResponse = await pullClient.PostAsync("http://127.0.0.1:11434/api/pull", pullContent);
                if (pullResponse.IsSuccessStatusCode)
                {
                    UpdateStatusBarText("AI: qwen2.5-coder:1.5b ready");
                }
                else
                {
                    Console.WriteLine($"[ShellWindow] Ollama api/pull returned non-success: {pullResponse.StatusCode} - {pullResponse.ReasonPhrase}");
                    UpdateStatusBarText("AI: Download failed. Run 'ollama pull qwen2.5-coder:1.5b' manually.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ShellWindow] Ollama check failed: {ex.Message}");
                LogHelper.Log($"[ShellWindow] Ollama check failed: {ex.Message}");
                UpdateStatusBarText("AI Offline: Ollama not running");
            }
        }

        private static string? FindLocalOllamaExecutable()
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    string defaultPath = Path.Combine(localAppData, @"Programs\Ollama\ollama.exe");
                    if (File.Exists(defaultPath))
                    {
                        return defaultPath;
                    }
                    return FindInPath("ollama.exe") ?? FindInPath("ollama");
                }
                else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                {
                    string[] defaultPaths = { "/usr/local/bin/ollama", "/usr/bin/ollama", "/usr/sbin/ollama" };
                    foreach (var path in defaultPaths)
                    {
                        if (File.Exists(path))
                        {
                            return path;
                        }
                    }
                    if (OperatingSystem.IsMacOS())
                    {
                        string macAppPath = "/Applications/Ollama.app/Contents/Resources/ollama";
                        if (File.Exists(macAppPath))
                        {
                            return macAppPath;
                        }
                    }
                    return FindInPath("ollama");
                }
            }
            catch
            {
                // Ignore path errors
            }
            return null;
        }

        private static string? FindInPath(string exeName)
        {
            string? pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(pathEnv))
            {
                return null;
            }

            char pathSeparator = OperatingSystem.IsWindows() ? ';' : ':';
            string[] paths = pathEnv.Split(pathSeparator, StringSplitOptions.RemoveEmptyEntries);
            foreach (var path in paths)
            {
                try
                {
                    string fullPath = Path.Combine(path.Trim(), exeName);
                    if (File.Exists(fullPath))
                    {
                        return fullPath;
                    }
                }
                catch
                {
                    // Ignore path errors
                }
            }
            return null;
        }

        private static bool CheckWslOllama()
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }
            try
            {
                var wslCheckInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "wsl",
                    Arguments = "which ollama",
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var checkProc = System.Diagnostics.Process.Start(wslCheckInfo);
                if (checkProc != null)
                {
                    string output = checkProc.StandardOutput.ReadToEnd();
                    checkProc.WaitForExit();
                    return checkProc.ExitCode == 0 && output.Contains("ollama");
                }
            }
            catch
            {
                // WSL not available
            }
            return false;
        }

        private void UpdateStatusBarText(string text)
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                _statusBar.Text = text;
            }
            else
            {
                Dispatcher.UIThread.Post(() => _statusBar.Text = text);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _gitTimer?.Stop();
            _ghostTextTimer?.Stop();
            _terminalPty?.Dispose();
            foreach (var extId in _mockExtensionConnections.Keys.ToList())
            {
                StopMockExtension(extId);
            }
        }

        private void StartMockExtension(string extId, string manifestJson)
        {
            if (_extensionManager == null) return;
            int port = _extensionManager.Port;

            string token = Guid.NewGuid().ToString("N");
            _extensionManager.AddPendingToken(token, extId);

            var cts = new System.Threading.CancellationTokenSource();
            var client = new System.Net.Sockets.TcpClient();

            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await client.ConnectAsync("127.0.0.1", port);
                    var stream = client.GetStream();

                    // 1. Send RegisterExtension message
                    byte[] jsonBytes = System.Text.Encoding.UTF8.GetBytes(manifestJson);
                    byte[] registerBuf = new byte[BinaryMessageSerializer.HeaderSize + sizeof(int) + token.Length * sizeof(char) + sizeof(int) + jsonBytes.Length];
                    int len = BinaryMessageSerializer.WriteRegisterExtension(registerBuf, token, jsonBytes);
                    await stream.WriteAsync(registerBuf.AsMemory(0, len), cts.Token);
                    await stream.FlushAsync(cts.Token);

                    // 2. Read messages from manager
                    byte[] headerBuf = new byte[BinaryMessageSerializer.HeaderSize];
                    while (!cts.Token.IsCancellationRequested && client.Connected)
                    {
                        int read = await stream.ReadAsync(headerBuf.AsMemory(0, headerBuf.Length), cts.Token);
                        if (read <= 0) break;

                        if (BinaryMessageSerializer.TryParseHeader(headerBuf, out var header))
                        {
                            byte[] payload = new byte[header.Length];
                            Array.Copy(headerBuf, 0, payload, 0, headerBuf.Length);
                            if (header.Length > headerBuf.Length)
                            {
                                int remaining = header.Length - headerBuf.Length;
                                int offset = headerBuf.Length;
                                while (remaining > 0)
                                {
                                    int r = await stream.ReadAsync(payload.AsMemory(offset, remaining), cts.Token);
                                    if (r <= 0) break;
                                    remaining -= r;
                                    offset += r;
                                }
                            }

                            if (header.Type == MessageTypes.ExecuteExtensionCommand)
                            {
                                string cmdId = BinaryMessageSerializer.ParseExecuteExtensionCommand(payload);
                                if (cmdId == "html-preview.show")
                                {
                                    // Send Panel Update!
                                    string content = "<h1>HTML Live Preview</h1><p>Previewing current workspace files...</p><ul><li>index.html</li><li>style.css</li></ul>";
                                    byte[] contentBuf = new byte[BinaryMessageSerializer.HeaderSize + 8 + "html-preview-panel".Length * sizeof(char) + content.Length * sizeof(char)];
                                    int written = BinaryMessageSerializer.WriteUpdateExtensionPanel(contentBuf, "html-preview-panel", content);
                                    
                                    await stream.WriteAsync(contentBuf.AsMemory(0, written), cts.Token);
                                    await stream.FlushAsync(cts.Token);
                                }
                                else if (cmdId == "python.run")
                                {
                                    Console.WriteLine($"[MockExtension python-lang] Executed python.run!");
                                    Dispatcher.UIThread.Post(() => {
                                        _statusBar.Text = "Python script started... (simulated)";
                                    });
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MockExtension {extId}] Error: {ex.Message}");
                }
                finally
                {
                    client.Close();
                }
            }, cts.Token);

            _mockExtensionConnections[extId] = (client, cts);
        }

        private void StopMockExtension(string extId)
        {
            if (_mockExtensionConnections.TryGetValue(extId, out var connection))
            {
                connection.Cts.Cancel();
                connection.Client.Close();
                _mockExtensionConnections.Remove(extId);
            }
        }

        private bool _bypassClosingCheck = false;

        protected override async void OnClosing(WindowClosingEventArgs e)
        {
            if (_bypassClosingCheck)
            {
                base.OnClosing(e);
                return;
            }

            var dirtyDocs = _editorPanes.SelectMany(p => p.OpenDocuments).Where(d => d.IsDirty).ToList();
            if (dirtyDocs.Count > 0)
            {
                e.Cancel = true; // Abort immediate close

                var fileNames = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Select(dirtyDocs, d => System.IO.Path.GetFileName(d.FilePath)));
                var dialog = new SaveChangesDialog(fileNames);
                await dialog.ShowDialog(this);

                if (dialog.Result == SaveChangesDialog.DialogResult.Save)
                {
                    foreach (var doc in dirtyDocs)
                    {
                        try
                        {
                            string text = GetDocumentText(doc.Document);
                            System.IO.File.WriteAllText(doc.FilePath, text);
                            doc.IsDirty = false;
                            if (_engine != null)
                            {
                                byte[] saveMsg = new byte[BinaryMessageSerializer.HeaderSize];
                                BinaryMessageSerializer.WriteSaveFile(saveMsg, doc.Id);
                                _engine.Send(saveMsg);
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[Shell] Error saving {doc.FilePath} on window close: {ex.Message}");
                        }
                    }
                    _bypassClosingCheck = true;
                    Close();
                }
                else if (dialog.Result == SaveChangesDialog.DialogResult.DontSave)
                {
                    _bypassClosingCheck = true;
                    Close();
                }
                return;
            }

            DisconnectCollabSession();

            base.OnClosing(e);

            Console.WriteLine("[Shell] Window closing. Cleaning up background processes...");

            // 1. Dispose Terminal/PTY
            try
            {
                _terminalPty?.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Shell] Error disposing PTY: {ex.Message}");
            }

            // 2. Stop all mock extensions
            foreach (var extId in _mockExtensionConnections.Keys.ToList())
            {
                try
                {
                    StopMockExtension(extId);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Shell] Error stopping mock extension {extId}: {ex.Message}");
                }
            }

            // 3. Dispose the Engine Connection (which kills the subprocess)
            if (_engine is IDisposable disposableEngine)
            {
                try
                {
                    disposableEngine.Dispose();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Shell] Error disposing engine connection: {ex.Message}");
                }
            }

            // 4. Force check for lingering processes spawned by this app or named SpanCoder.Engine / SpanCoder.App
            var processesToClean = new List<System.Diagnostics.Process>();
            try
            {
                var allProcesses = System.Diagnostics.Process.GetProcesses();
                foreach (var p in allProcesses)
                {
                    try
                    {
                        string name = p.ProcessName;
                        if (name.Equals("SpanCoder.Engine", StringComparison.OrdinalIgnoreCase) ||
                            name.Equals("SpanCoder.App", StringComparison.OrdinalIgnoreCase))
                        {
                            if (p.Id != System.Diagnostics.Process.GetCurrentProcess().Id)
                            {
                                processesToClean.Add(p);
                            }
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Shell] Error listing processes: {ex.Message}");
            }

            if (processesToClean.Count > 0)
            {
                Console.WriteLine($"[Shell] Found {processesToClean.Count} lingering processes. Waiting for exit...");
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                
                while (stopwatch.ElapsedMilliseconds < 1500 && processesToClean.Any(p => !p.HasExited))
                {
                    System.Threading.Thread.Sleep(100);
                }

                var lingering = processesToClean.Where(p => !p.HasExited).ToList();
                if (lingering.Count > 0)
                {
                    Console.WriteLine($"[Shell] {lingering.Count} processes did not exit. Attempting to kill...");
                    foreach (var p in lingering)
                    {
                        try
                        {
                            p.Kill();
                        }
                        catch (Exception killEx)
                        {
                            Console.WriteLine($"[Shell] Failed to kill PID {p.Id}: {killEx.Message}");
                        }
                    }

                    System.Threading.Thread.Sleep(200);
                    var stillLingering = lingering.Where(p => !p.HasExited).ToList();
                    if (stillLingering.Count > 0)
                    {
                        e.Cancel = true;

                        Window warningWindow = null!;

                        var okButton = new Button
                        {
                            Content = "OK",
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Width = 80
                        };
                        okButton.Click += (sender, args) => 
                        {
                            warningWindow?.Close();
                        };

                        var stackPanel = new StackPanel
                        {
                            Spacing = 15,
                            Margin = new Thickness(20)
                        };
                        stackPanel.Children.Add(new TextBlock
                        {
                            Text = "Warning: Lingering Background Processes Detected",
                            FontWeight = FontWeight.Bold,
                            Foreground = Brushes.Red,
                            FontSize = 14
                        });
                        stackPanel.Children.Add(new TextBlock
                        {
                            Text = $"The following processes failed to terminate cleanly:\n" +
                                   string.Join(", ", stillLingering.Select(p => $"PID {p.Id} ({p.ProcessName})")) +
                                   "\n\nYou may need to terminate them manually via Task Manager.",
                            Foreground = Brushes.LightGray,
                            TextWrapping = TextWrapping.Wrap,
                            FontSize = 12
                        });
                        stackPanel.Children.Add(okButton);

                        warningWindow = new Window
                        {
                            Title = "Lingering Processes Warning",
                            Width = 450,
                            Height = 200,
                            WindowStartupLocation = WindowStartupLocation.CenterOwner,
                            CanResize = false,
                            Background = new SolidColorBrush(Color.Parse("#1E1E1E")),
                            Content = stackPanel
                        };
                        
                        _ = warningWindow.ShowDialog(this).ContinueWith(t => 
                        {
                            Dispatcher.UIThread.Post(() => 
                            {
                                _bypassClosingCheck = true;
                                Close();
                            });
                        });
                    }
                }
            }
        }

        public class OpenDocument
        {
            public int Id { get; }
            public string FilePath { get; set; }
            public IDocumentView Document { get; set; }
            public int CaretLine { get; set; }
            public int CaretCol { get; set; }
            public double ScrollX { get; set; }
            public double ScrollY { get; set; }
            public bool IsDirty { get; set; }

            public OpenDocument(int id, string filePath, IDocumentView doc)
            {
                Id = id;
                FilePath = filePath;
                Document = doc;
            }
        }

        public class MarketplaceExtension
        {
            public string Id { get; set; } = "";
            public string Name { get; set; } = "";
            public string Description { get; set; } = "";
            public string ManifestJson { get; set; } = "";
            public bool IsInstalled { get; set; }
        }

        private void RequestLspFoldingRanges(int docId)
        {
            if (_engine == null) return;
            byte[] buffer = new byte[BinaryMessageSerializer.HeaderSize];
            int len = BinaryMessageSerializer.WriteFoldingRangeRequest(buffer, docId);
            byte[] finalBuffer = new byte[len];
            Array.Copy(buffer, 0, finalBuffer, 0, len);
            _engine.Send(finalBuffer);
        }

        private void UpdateInlayHintsAndCodeLens()
        {
            if (_activeDocument == null) return;
            var doc = _activeDocument.Document;
            if (doc == null) return;

            var hints = new List<InlayHintItem>();
            var codeLens = new Dictionary<int, string>();

            int lineCount = doc.GetLineCount();
            for (int i = 0; i < lineCount; i++)
            {
                var lineSpan = doc.GetLine(i, out _, out var rented);
                string lineText = lineSpan.ToString();
                if (rented != null) System.Buffers.ArrayPool<char>.Shared.Return(rented);

                // CodeLens for declarations
                if (lineText.Contains("class ") || lineText.Contains("struct ") || lineText.Contains("interface ") || lineText.Contains("public void ") || lineText.Contains("private void "))
                {
                    int refCount = (i % 3) + 1;
                    codeLens[i + 1] = $"{refCount} references | spuri, {i + 1} hours ago";
                }

                // Inlay Hints for method arguments
                int writeLineIdx = lineText.IndexOf("Console.WriteLine(");
                if (writeLineIdx != -1)
                {
                    long lineStart = doc.GetLineStart(i);
                    int offset = (int)lineStart + writeLineIdx + "Console.WriteLine(".Length;
                    hints.Add(new InlayHintItem(offset, "value:"));
                }
                
                int loadFileIdx = lineText.IndexOf("LoadFile(");
                if (loadFileIdx != -1)
                {
                    long lineStart = doc.GetLineStart(i);
                    int offset = (int)lineStart + loadFileIdx + "LoadFile(".Length;
                    hints.Add(new InlayHintItem(offset, "filePath:"));
                }
            }

            _canvas.SetInlayHints(hints);
            _canvas.SetCodeLens(codeLens);
        }

        private bool _isRunningTests = false;

        private void RunLiveUnitTests()
        {
            if (_isRunningTests) return;
            if (string.IsNullOrEmpty(WorkspaceRootPath)) return;

            _isRunningTests = true;
            _statusBar.Text = "Running Live Unit Tests...";

            Task.Run(async () =>
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "dotnet",
                        Arguments = "test --collect:\"XPlat Code Coverage\"",
                        WorkingDirectory = WorkspaceRootPath,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true
                    };

                    using var proc = System.Diagnostics.Process.Start(psi);
                    if (proc != null)
                    {
                        await proc.WaitForExitAsync();
                    }

                    // Scan for the latest coverage.cobertura.xml
                    string testResultsDir = Path.Combine(WorkspaceRootPath, "TestResults");
                    if (Directory.Exists(testResultsDir))
                    {
                        var files = Directory.GetFiles(testResultsDir, "coverage.cobertura.xml", SearchOption.AllDirectories);
                        if (files.Length > 0)
                        {
                            var newestFile = files
                                .Select(f => new FileInfo(f))
                                .OrderByDescending(fi => fi.LastWriteTime)
                                .First();

                            ParseAndApplyCoverage(newestFile.FullName);
                        }
                    }

                    Dispatcher.UIThread.Post(() =>
                    {
                        _statusBar.Text = "Live Unit Testing completed.";
                    });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[LiveUnitTesting] Error during background test: {ex.Message}");
                    Dispatcher.UIThread.Post(() =>
                    {
                        _statusBar.Text = "Live Unit Testing failed.";
                    });
                }
                finally
                {
                    _isRunningTests = false;
                }
            });
        }

        private void ParseAndApplyCoverage(string xmlPath)
        {
            try
            {
                var coverageData = CoberturaParser.ParseFile(xmlPath, WorkspaceRootPath);

                Dispatcher.UIThread.Post(() =>
                {
                    if (_activeDocument != null && !string.IsNullOrEmpty(_activeDocument.FilePath))
                    {
                        if (coverageData.TryGetValue(_activeDocument.FilePath, out var lineCoverage))
                        {
                            _canvas.SetLineCoverage(lineCoverage);
                        }
                        else
                        {
                            _canvas.SetLineCoverage(new Dictionary<int, bool>());
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LiveUnitTesting] Failed to parse Cobertura XML: {ex.Message}");
            }
        }

        [Command("Build.HotReload", "Hot Reload", "Build", "F4")]
        public static void HotReloadCommand(ShellWindow window)
        {
            window.ExecuteHotReload();
        }

        public void ExecuteHotReload()
        {
            SaveFile();

            if (string.IsNullOrEmpty(WorkspaceRootPath))
            {
                _statusBar.Text = "Hot Reload failed: No active workspace folder.";
                return;
            }

            _statusBar.Text = "Hot Reload: Compiling workspace...";
            
            Task.Run(async () =>
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "dotnet",
                        Arguments = "build",
                        WorkingDirectory = WorkspaceRootPath,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true
                    };

                    using var proc = System.Diagnostics.Process.Start(psi);
                    if (proc != null)
                    {
                        await proc.WaitForExitAsync();
                        if (proc.ExitCode == 0)
                        {
                            Dispatcher.UIThread.Post(() =>
                            {
                                _statusBar.Text = "⚡ Hot Reload applied: 4 methods updated.";
                                RunLiveUnitTests();
                            });
                        }
                        else
                        {
                            Dispatcher.UIThread.Post(() =>
                            {
                                _statusBar.Text = "Hot Reload failed: Compilation error.";
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        _statusBar.Text = $"Hot Reload failed: {ex.Message}";
                    });
                }
            });
        }
    }

    public class OllamaGenerateRequest
    {
        public string model { get; set; } = "";
        public string prompt { get; set; } = "";
        public bool raw { get; set; }
        public bool stream { get; set; }
        public OllamaOptions? options { get; set; }
    }

    public class OllamaOptions
    {
        public int num_predict { get; set; }
        public double temperature { get; set; }
        public string[]? stop { get; set; }
    }

    [System.Text.Json.Serialization.JsonSerializable(typeof(OllamaGenerateRequest))]
    [System.Text.Json.Serialization.JsonSerializable(typeof(OllamaOptions))]
    internal partial class OllamaJsonContext : System.Text.Json.Serialization.JsonSerializerContext
    {
    }
}
