using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Layout;
using SpanCoder.Contracts;

namespace SpanCoder.Shell
{
    public class SearchItem
    {
        public string DisplayName { get; }
        public string Detail { get; }
        public string Shortcut { get; }
        public string Category { get; } // "Action", "File", "Symbol"
        public object AssociatedData { get; } // commandId (string), filePath (string), lineIndex (int)

        public SearchItem(string displayName, string detail, string shortcut, string category, object data)
        {
            DisplayName = displayName;
            Detail = detail;
            Shortcut = shortcut;
            Category = category;
            AssociatedData = data;
        }
    }

    public class CommandPalette : Border
    {
        private readonly ShellWindow _window;
        private readonly TextBox _searchBox;
        private readonly ListBox _listBox;
        private readonly StackPanel _tabBar;
        
        // Tab TextBlocks
        private readonly TextBlock _tabAll;
        private readonly TextBlock _tabFiles;
        private readonly TextBlock _tabActions;
        private readonly TextBlock _tabSymbols;
        private readonly TextBlock _tabFind;

        private System.Threading.CancellationTokenSource? _grepCts;

        private readonly List<SearchItem> _staticActions = new();
        private readonly List<SearchItem> _workspaceFiles = new();
        private readonly List<SearchItem> _filteredItems = new();
        private readonly List<SearchItem> _lspSymbols = new();
        private bool _hasLspSymbols;
        
        private string? _lastScannedWorkspace;
        private readonly object _filesLock = new();

        public CommandPalette(ShellWindow window)
        {
            _window = window;

            // UI Styling & Window-Level Anchoring
            Width = 550;
            HorizontalAlignment = HorizontalAlignment.Center;
            VerticalAlignment = VerticalAlignment.Top;
            Margin = new Thickness(0, 40, 0, 0); // Spaced slightly from top edge
            ZIndex = 200;
            IsVisible = false;

            Background = new SolidColorBrush(Color.Parse("#1E1E1E"));
            BorderBrush = new SolidColorBrush(Color.Parse("#007ACC")); // Sleek blue outline
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(6);
            BoxShadow = BoxShadows.Parse("0 12 36 0 #90000000"); // Rich premium shadow

            var mainPanel = new StackPanel();

            // 1. Input Box Area (with 🔍 icon)
            var inputGrid = new Grid();
            inputGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto)); // Icon
            inputGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star)); // TextBox

            var searchIcon = new Avalonia.Controls.Shapes.Path
            {
                Width = 14,
                Height = 14,
                Data = StreamGeometry.Parse("M15.5,14 L14.71,14 L14.43,13.72 C15.41,12.59 16,11.11 16,9.5 C16,5.91 13.09,3 9.5,3 C5.91,3 3,5.91 3,9.5 C3,13.09 5.91,16 9.5,16 C11.11,16 12.59,15.41 13.72,14.43 L14,14.71 L14,15.5 L19,20.5 L20.5,19 L15.5,14 Z M9.5,14 C7.01,14 5,11.99 5,9.5 C5,7.01 7.01,5 9.5,5 C11.99,5 14,7.01 14,9.5 C14,11.99 11.99,14 9.5,14 Z"),
                Fill = new SolidColorBrush(Color.Parse("#858585")),
                Stretch = Stretch.Uniform,
                Margin = new Thickness(12, 10, 4, 10),
                VerticalAlignment = VerticalAlignment.Center
            };
            inputGrid.Children.Add(searchIcon);
            Grid.SetColumn(searchIcon, 0);

            _searchBox = new TextBox
            {
                Watermark = "Search files, symbols (@), or run commands (>)...",
                Background = Brushes.Transparent,
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Margin = new Thickness(4, 6, 12, 6),
                FontSize = 13,
                CaretBrush = Brushes.White
            };
            inputGrid.Children.Add(_searchBox);
            Grid.SetColumn(_searchBox, 1);

            mainPanel.Children.Add(inputGrid);

            // 2. JetBrains-style Tab Bar
            _tabBar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Background = new SolidColorBrush(Color.Parse("#252526")),
                Height = 26
            };

            _tabAll = CreateTabHeader("All", "all");
            _tabAll.Margin = new Thickness(10, 0, 8, 0); // extra margin on first tab
            _tabFiles = CreateTabHeader("Files", "files");
            _tabActions = CreateTabHeader("Actions", "actions");
            _tabSymbols = CreateTabHeader("Symbols", "symbols");
            _tabFind = CreateTabHeader("Find", "find");

            _tabBar.Children.Add(_tabAll);
            _tabBar.Children.Add(_tabFiles);
            _tabBar.Children.Add(_tabActions);
            _tabBar.Children.Add(_tabSymbols);
            _tabBar.Children.Add(_tabFind);

            mainPanel.Children.Add(_tabBar);

            // 3. Separator Line
            mainPanel.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.Parse("#2D2D2D")) });

            // 4. Results List Box
            _listBox = new ListBox
            {
                Background = Brushes.Transparent,
                Foreground = Brushes.LightGray,
                MaxHeight = 280,
                BorderThickness = new Thickness(0),
                ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<SearchItem>((item, names) =>
                {
                    var grid = new Grid();
                    grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto)); // Badge
                    grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star)); // Name + path
                    grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto)); // Shortcut

                    // Category Badge
                    string badgeColor = item.Category switch
                    {
                        "File" => "#4EC9B0",    // Teal green
                        "Action" => "#569CD6",  // Blue
                        "Symbol" => "#DCDCAA",  // Yellow-green
                        "Find" => "#D19A66",    // Orange/coral
                        _ => "#808080"
                    };

                    var badgeBorder = new Border
                    {
                        Background = new SolidColorBrush(Color.Parse(badgeColor)) { Opacity = 0.15 },
                        BorderBrush = new SolidColorBrush(Color.Parse(badgeColor)) { Opacity = 0.4 },
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(3),
                        Padding = new Thickness(4, 1),
                        Margin = new Thickness(4, 2, 8, 2),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    badgeBorder.Child = new TextBlock
                    {
                        Text = item.Category.ToUpper(),
                        Foreground = new SolidColorBrush(Color.Parse(badgeColor)),
                        FontSize = 9,
                        FontWeight = FontWeight.Bold
                    };
                    grid.Children.Add(badgeBorder);
                    Grid.SetColumn(badgeBorder, 0);

                    // Name and Detail Stack
                    var textStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                    
                    var nameText = new TextBlock
                    {
                        Text = item.DisplayName,
                        Foreground = Brushes.White,
                        FontSize = 12,
                        FontWeight = FontWeight.Normal
                    };
                    textStack.Children.Add(nameText);

                    if (!string.IsNullOrEmpty(item.Detail))
                    {
                        var detailText = new TextBlock
                        {
                            Text = $"  —  {item.Detail}",
                            Foreground = Brushes.Gray,
                            FontSize = 11,
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        textStack.Children.Add(detailText);
                    }

                    grid.Children.Add(textStack);
                    Grid.SetColumn(textStack, 1);

                    // Key Shortcut
                    if (!string.IsNullOrEmpty(item.Shortcut))
                    {
                        var shortcutText = new TextBlock
                        {
                            Text = item.Shortcut,
                            Foreground = Brushes.Gray,
                            FontSize = 11,
                            Margin = new Thickness(10, 2),
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        grid.Children.Add(shortcutText);
                        Grid.SetColumn(shortcutText, 2);
                    }

                    return grid;
                }, true)
            };

            mainPanel.Children.Add(_listBox);

            Child = mainPanel;

            // Wire Up Actions
            _searchBox.TextChanged += (s, e) => FilterItems();

            _searchBox.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Up)
                {
                    int idx = _listBox.SelectedIndex;
                    if (idx > 0)
                    {
                        _listBox.SelectedIndex = idx - 1;
                        _listBox.ScrollIntoView(_listBox.SelectedIndex);
                    }
                    e.Handled = true;
                }
                else if (e.Key == Key.Down)
                {
                    int idx = _listBox.SelectedIndex;
                    if (idx < _filteredItems.Count - 1)
                    {
                        _listBox.SelectedIndex = idx + 1;
                        _listBox.ScrollIntoView(_listBox.SelectedIndex);
                    }
                    e.Handled = true;
                }
                else if (e.Key == Key.Enter)
                {
                    CommitSelection();
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    Hide();
                    e.Handled = true;
                }
            };

            _listBox.DoubleTapped += (s, e) => CommitSelection();
        }

        private TextBlock CreateTabHeader(string text, string tag)
        {
            var tb = new TextBlock
            {
                Text = text,
                Foreground = Brushes.Gray,
                FontSize = 11,
                FontWeight = FontWeight.Normal,
                Margin = new Thickness(8, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand),
                Tag = tag
            };

            tb.PointerPressed += (s, e) =>
            {
                switch (tag)
                {
                    case "all":
                        _searchBox.Text = "";
                        break;
                    case "files":
                        // Clear prefix to search files
                        if (_searchBox.Text != null && (_searchBox.Text.StartsWith(">") || _searchBox.Text.StartsWith("@")))
                            _searchBox.Text = "";
                        break;
                    case "actions":
                        _searchBox.Text = ">";
                        break;
                    case "symbols":
                        _searchBox.Text = "@";
                        break;
                    case "find":
                        _searchBox.Text = "?";
                        break;
                }
                _searchBox.Focus();
                e.Handled = true;
            };

            return tb;
        }

        public void Show(IEnumerable<CommandDescriptor> commands)
        {
            _staticActions.Clear();
            foreach (var cmd in commands)
            {
                _staticActions.Add(new SearchItem(cmd.DisplayName, cmd.Id, cmd.DefaultShortcut, "Action", cmd.Id));
            }

            IsVisible = true;
            _searchBox.Text = "";
            _listBox.ItemsSource = null;
            _hasLspSymbols = false;
            _lspSymbols.Clear();

            // Trigger workspace scan asynchronously
            string? wsPath = _window.WorkspaceRootPath;
            if (!string.IsNullOrEmpty(wsPath))
            {
                if (_lastScannedWorkspace != wsPath)
                {
                    _lastScannedWorkspace = wsPath;
                    Task.Run(() => ScanWorkspaceFiles(wsPath));
                }
            }
            else
            {
                lock (_filesLock)
                {
                    _workspaceFiles.Clear();
                }
            }

            FilterItems();
            _searchBox.Focus();
        }

        public void Hide()
        {
            IsVisible = false;
            _window.ReturnFocusToEditor();
        }

        private void ScanWorkspaceFiles(string wsPath)
        {
            var files = new List<SearchItem>();
            try
            {
                string scanRoot = wsPath;
                if (System.IO.File.Exists(wsPath))
                {
                    scanRoot = System.IO.Path.GetDirectoryName(wsPath) ?? wsPath;
                }
                TraverseDir(scanRoot, scanRoot, files);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CommandPalette] File scan error: {ex.Message}");
            }

            lock (_filesLock)
            {
                _workspaceFiles.Clear();
                _workspaceFiles.AddRange(files);
            }

            // Refresh filter on UI thread
            Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                if (IsVisible) FilterItems();
            });
        }

        private void TraverseDir(string path, string basePath, List<SearchItem> list)
        {
            foreach (var file in System.IO.Directory.GetFiles(path))
            {
                string relPath = System.IO.Path.GetRelativePath(basePath, file);
                string name = System.IO.Path.GetFileName(file);
                list.Add(new SearchItem(name, relPath, "", "File", file));
            }

            foreach (var dir in System.IO.Directory.GetDirectories(path))
            {
                string name = System.IO.Path.GetFileName(dir);
                if (name == "bin" || name == "obj" || name == ".git" || name == ".vs" || name == "node_modules")
                    continue;

                TraverseDir(dir, basePath, list);
            }
        }

        private void FilterItems()
        {
            _grepCts?.Cancel();
            _grepCts = null;

            string queryText = _searchBox.Text ?? "";
            string cleanQuery = queryText;

            string mode = "all";
            if (queryText.StartsWith(">"))
            {
                mode = "actions";
                cleanQuery = queryText.Substring(1);
            }
            else if (queryText.StartsWith("@"))
            {
                mode = "symbols";
                cleanQuery = queryText.Substring(1);
            }
            else if (queryText.StartsWith("?"))
            {
                mode = "find";
                cleanQuery = queryText.Substring(1);
            }
            else if (!string.IsNullOrEmpty(queryText))
            {
                // Plain query can search files
                mode = "files";
            }

            UpdateTabsHighlight(mode);

            _filteredItems.Clear();

            if (mode == "all")
            {
                // Search both files & actions
                var matches = new List<SearchItem>();
                
                // Add actions matching query
                matches.AddRange(_staticActions.Where(x => Match(x.DisplayName, cleanQuery) || Match(x.Category, cleanQuery)));
                
                // Add files matching query
                lock (_filesLock)
                {
                    matches.AddRange(_workspaceFiles.Where(x => Match(x.DisplayName, cleanQuery)));
                }

                _filteredItems.AddRange(matches.Take(50));
                _listBox.ItemsSource = null;
                _listBox.ItemsSource = _filteredItems;
                if (_filteredItems.Count > 0) _listBox.SelectedIndex = 0;
            }
            else if (mode == "actions")
            {
                _filteredItems.AddRange(_staticActions.Where(x => Match(x.DisplayName, cleanQuery) || Match(x.Detail, cleanQuery)).Take(50));
                _listBox.ItemsSource = null;
                _listBox.ItemsSource = _filteredItems;
                if (_filteredItems.Count > 0) _listBox.SelectedIndex = 0;
            }
            else if (mode == "files")
            {
                lock (_filesLock)
                {
                    _filteredItems.AddRange(_workspaceFiles.Where(x => Match(x.DisplayName, cleanQuery) || Match(x.Detail, cleanQuery)).Take(50));
                }
                _listBox.ItemsSource = null;
                _listBox.ItemsSource = _filteredItems;
                if (_filteredItems.Count > 0) _listBox.SelectedIndex = 0;
            }
            else if (mode == "symbols")
            {
                if (!_hasLspSymbols)
                {
                    _window.RequestDocumentSymbols();
                    _filteredItems.Add(new SearchItem("Loading symbols...", "", "", "Symbol", 0));
                    _listBox.ItemsSource = null;
                    _listBox.ItemsSource = _filteredItems;
                    _listBox.SelectedIndex = 0;
                    return;
                }

                _filteredItems.AddRange(_lspSymbols.Where(x => Match(x.DisplayName, cleanQuery) || Match(x.Detail, cleanQuery)).Take(50));
                _listBox.ItemsSource = null;
                _listBox.ItemsSource = _filteredItems;
                if (_filteredItems.Count > 0) _listBox.SelectedIndex = 0;
            }
            else if (mode == "find")
            {
                if (cleanQuery.Length < 3)
                {
                    _filteredItems.Add(new SearchItem("Type at least 3 characters to search...", "", "", "Find", ""));
                    _listBox.ItemsSource = null;
                    _listBox.ItemsSource = _filteredItems;
                    _listBox.SelectedIndex = 0;
                }
                else
                {
                    string? wsPath = _window.WorkspaceRootPath;
                    if (string.IsNullOrEmpty(wsPath))
                    {
                        _filteredItems.Add(new SearchItem("No active workspace directory", "", "", "Find", ""));
                        _listBox.ItemsSource = null;
                        _listBox.ItemsSource = _filteredItems;
                        _listBox.SelectedIndex = 0;
                        return;
                    }

                    string scanRoot = wsPath;
                    if (System.IO.File.Exists(wsPath))
                    {
                        scanRoot = System.IO.Path.GetDirectoryName(wsPath) ?? wsPath;
                    }

                    var cts = new System.Threading.CancellationTokenSource();
                    _grepCts = cts;
                    var token = cts.Token;

                    Task.Run(async () =>
                    {
                        try
                        {
                            var searchEngine = new Glacier.Grep.SearchEngine(scanRoot);
                            var results = await searchEngine.SearchAsync(cleanQuery);
                            if (token.IsCancellationRequested) return;

                            var searchItems = results.Select(r => new SearchItem(
                                r.MatchContent.Trim(),
                                $"{r.FilePath}:{r.LineNumber}",
                                "",
                                "Find",
                                new ValueTuple<string, int>(System.IO.Path.GetFullPath(System.IO.Path.Combine(scanRoot, r.FilePath)), r.LineNumber)
                            )).ToList();

                            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                            {
                                if (token.IsCancellationRequested) return;
                                _filteredItems.Clear();
                                _filteredItems.AddRange(searchItems.Take(50));
                                _listBox.ItemsSource = null;
                                _listBox.ItemsSource = _filteredItems;
                                if (_filteredItems.Count > 0)
                                {
                                    _listBox.SelectedIndex = 0;
                                }
                            });
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[CommandPalette] Grep search error: {ex.Message}");
                        }
                    });
                }
            }

            if (_filteredItems.Count > 0)
            {
                _listBox.SelectedIndex = 0;
            }
        }

        public void ShowDocumentSymbols(IEnumerable<DocumentSymbolItem> items)
        {
            _lspSymbols.Clear();
            foreach (var item in items)
            {
                _lspSymbols.Add(new SearchItem(item.Name, item.Detail, $"Line {item.Line + 1}", "Symbol", item.Line));
            }
            _hasLspSymbols = true;
            FilterItems();
        }

        public void ShowReferences(IEnumerable<ReferenceItem> items)
        {
            IsVisible = true;
            _searchBox.Text = "?references";
            UpdateTabsHighlight("find");
            
            _filteredItems.Clear();
            var list = items.ToList();
            if (list.Count == 0)
            {
                _filteredItems.Add(new SearchItem("No references found", "", "", "Find", ""));
            }
            else
            {
                foreach (var refItem in list)
                {
                    string fileName = System.IO.Path.GetFileName(refItem.FilePath);
                    _filteredItems.Add(new SearchItem(
                        $"{fileName}:{refItem.Line + 1}",
                        refItem.FilePath,
                        "",
                        "Find",
                        new ValueTuple<string, int>(refItem.FilePath, refItem.Line + 1)
                    ));
                }
            }
            
            _listBox.ItemsSource = null;
            _listBox.ItemsSource = _filteredItems;
            if (_filteredItems.Count > 0) _listBox.SelectedIndex = 0;
            _searchBox.Focus();
        }

        private bool Match(string text, string query)
        {
            if (string.IsNullOrEmpty(query)) return true;
            return text.Contains(query, StringComparison.OrdinalIgnoreCase);
        }

        private List<SearchItem> GetLocalSymbols(string query)
        {
            var list = new List<SearchItem>();
            var activeDoc = _window.ActiveDocumentView;
            if (activeDoc == null) return list;

            int count = activeDoc.GetLineCount();
            for (int i = 0; i < count; i++)
            {
                var lineSpan = activeDoc.GetLine(i, out _, out var rented);
                string text = lineSpan.ToString().Trim();
                if (rented != null) System.Buffers.ArrayPool<char>.Shared.Return(rented);

                // Skip simple code structures and comment noise
                if (text.StartsWith("//") || text.StartsWith("/*") || text.StartsWith("*"))
                    continue;

                if (text.Contains("class ") || text.Contains("struct ") || text.Contains("interface ") ||
                    text.Contains("void ") || text.Contains("public ") || text.Contains("private ") || 
                    text.Contains("protected ") || text.Contains("internal "))
                {
                    int braceIdx = text.IndexOf('{');
                    if (braceIdx >= 0) text = text.Substring(0, braceIdx).Trim();
                    int semiIdx = text.IndexOf(';');
                    if (semiIdx >= 0) text = text.Substring(0, semiIdx).Trim();

                    if (!string.IsNullOrEmpty(text) && Match(text, query))
                    {
                        list.Add(new SearchItem(text, $"Line {i + 1}", "", "Symbol", i));
                    }
                }
            }

            return list;
        }

        private void UpdateTabsHighlight(string activeMode)
        {
            // All tabs gray by default
            _tabAll.Foreground = Brushes.Gray;
            _tabAll.FontWeight = FontWeight.Normal;
            _tabFiles.Foreground = Brushes.Gray;
            _tabFiles.FontWeight = FontWeight.Normal;
            _tabActions.Foreground = Brushes.Gray;
            _tabActions.FontWeight = FontWeight.Normal;
            _tabSymbols.Foreground = Brushes.Gray;
            _tabSymbols.FontWeight = FontWeight.Normal;
            _tabFind.Foreground = Brushes.Gray;
            _tabFind.FontWeight = FontWeight.Normal;

            switch (activeMode)
            {
                case "all":
                    _tabAll.Foreground = Brushes.White;
                    _tabAll.FontWeight = FontWeight.Bold;
                    break;
                case "files":
                    _tabFiles.Foreground = Brushes.White;
                    _tabFiles.FontWeight = FontWeight.Bold;
                    break;
                case "actions":
                    _tabActions.Foreground = Brushes.White;
                    _tabActions.FontWeight = FontWeight.Bold;
                    break;
                case "symbols":
                    _tabSymbols.Foreground = Brushes.White;
                    _tabSymbols.FontWeight = FontWeight.Bold;
                    break;
                case "find":
                    _tabFind.Foreground = Brushes.White;
                    _tabFind.FontWeight = FontWeight.Bold;
                    break;
            }
        }

        private void CommitSelection()
        {
            var selected = _listBox.SelectedItem as SearchItem;
            if (selected == null) return;

            Hide();

            if (selected.Category == "Action")
            {
                string commandId = (string)selected.AssociatedData;
                _window.ExecuteCommand(commandId);
            }
            else if (selected.Category == "File")
            {
                string path = (string)selected.AssociatedData;
                _window.LoadFile(path);
            }
            else if (selected.Category == "Symbol")
            {
                int line = (int)selected.AssociatedData;
                _window.MoveCaretToLine(line);
                _window.ReturnFocusToEditor();
            }
            else if (selected.Category == "Find")
            {
                if (selected.AssociatedData is ValueTuple<string, int> data)
                {
                    _window.LoadFile(data.Item1);
                    _window.MoveCaretToLine(data.Item2 - 1);
                    _window.ReturnFocusToEditor();
                }
            }
        }
    }
}
