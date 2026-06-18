using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Glacier.Grep;

namespace SpanCoder.Shell
{
    public class FindReplaceFilesWindow : Window
    {
        private readonly ShellWindow _owner;
        private readonly TextBox _findBox;
        private readonly TextBox _replaceBox;
        private readonly Grid _replaceRow;

        private readonly TextBlock _findTabHeader;
        private readonly Border _findTabUnderline;
        private readonly TextBlock _replaceTabHeader;
        private readonly Border _replaceTabUnderline;

        private readonly CheckBox _caseSensitiveCheck;
        private readonly CheckBox _wholeWordCheck;
        private readonly CheckBox _regexCheck;

        private readonly ComboBox _lookInCombo;
        private readonly CheckBox _externalItemsCheck;
        private readonly CheckBox _miscFilesCheck;

        private readonly TextBox _fileTypesCombo;
        private readonly CheckBox _appendResultsCheck;
        private readonly ComboBox _resultsWindowCombo;

        private readonly Button _findPrevBtn;
        private readonly Button _findNextBtn;
        private readonly Button _skipFileBtn;
        private readonly Button _findAllBtn;
        private readonly Button _replaceAllBtn;

        private bool _isReplaceMode;
        private List<SearchResult> _searchResults = new();
        private int _currentResultIndex = -1;
        private string _lastSearchQuery = "";

        public FindReplaceFilesWindow(ShellWindow owner, bool showReplace)
        {
            _owner = owner;
            Title = "Find and Replace";
            Width = 520;
            Height = showReplace ? 530 : 500;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.Parse("#252526"));
            Foreground = Brushes.White;
            FontFamily = new FontFamily("Segoe UI");

            // Main Layout Container
            var mainGrid = new Grid { Margin = new Thickness(15) };
            mainGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // Custom Tabs Header
            mainGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star)); // Content area
            mainGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // Bottom buttons row

            // --- Row 0: Custom Tabs Header ---
            var tabsHeaderGrid = new Grid { Height = 35 };
            tabsHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            tabsHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

            // Find in Files Tab Header
            var findTabPanel = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 20, 0), Cursor = new Cursor(StandardCursorType.Hand) };
            _findTabHeader = new TextBlock { Text = "Find in Files", FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, Margin = new Thickness(0, 5) };
            _findTabUnderline = new Border { Height = 2, Background = new SolidColorBrush(Color.Parse("#7C7AF8")), HorizontalAlignment = HorizontalAlignment.Stretch };
            findTabPanel.Children.Add(_findTabHeader);
            findTabPanel.Children.Add(_findTabUnderline);
            findTabPanel.PointerPressed += (s, e) => SwitchTab(false);
            tabsHeaderGrid.Children.Add(findTabPanel);
            Grid.SetColumn(findTabPanel, 0);

            // Replace in Files Tab Header
            var replaceTabPanel = new StackPanel { Orientation = Orientation.Vertical, Cursor = new Cursor(StandardCursorType.Hand) };
            _replaceTabHeader = new TextBlock { Text = "Replace in Files", FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Brushes.Gray, Margin = new Thickness(0, 5) };
            _replaceTabUnderline = new Border { Height = 2, Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Stretch };
            replaceTabPanel.Children.Add(_replaceTabHeader);
            replaceTabPanel.Children.Add(_replaceTabUnderline);
            replaceTabPanel.PointerPressed += (s, e) => SwitchTab(true);
            tabsHeaderGrid.Children.Add(replaceTabPanel);
            Grid.SetColumn(replaceTabPanel, 1);

            mainGrid.Children.Add(tabsHeaderGrid);
            Grid.SetRow(tabsHeaderGrid, 0);

            // --- Row 1: Content Area ---
            var contentStack = new StackPanel { Spacing = 8, Margin = new Thickness(0, 10, 0, 0) };

            // Find input
            _findBox = new TextBox
            {
                Watermark = "Find what",
                Background = new SolidColorBrush(Color.Parse("#333337")),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.Parse("#3F3F46")),
                BorderThickness = new Thickness(1),
                Height = 24,
                FontSize = 12,
                Padding = new Thickness(6, 0, 6, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                CaretBrush = Brushes.White
            };
            _findBox.KeyDown += OnInputKeyDown;
            _findBox.TextChanged += (s, e) => ClearSearchCache();
            contentStack.Children.Add(_findBox);

            // Replace input (grouped in grid to show/hide)
            _replaceRow = new Grid { IsVisible = showReplace };
            _replaceBox = new TextBox
            {
                Watermark = "Replace with",
                Background = new SolidColorBrush(Color.Parse("#333337")),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.Parse("#3F3F46")),
                BorderThickness = new Thickness(1),
                Height = 24,
                FontSize = 12,
                Padding = new Thickness(6, 0, 6, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                CaretBrush = Brushes.White
            };
            _replaceRow.Children.Add(_replaceBox);
            contentStack.Children.Add(_replaceRow);

            // Search Criteria CheckBoxes
            _caseSensitiveCheck = new CheckBox { Content = "Match case", FontSize = 12, Margin = new Thickness(0, 0, 0, 0) };
            _wholeWordCheck = new CheckBox { Content = "Match whole word", FontSize = 12, Margin = new Thickness(0, 0, 0, 0) };
            _regexCheck = new CheckBox { Content = "Use regular expressions", FontSize = 12, Margin = new Thickness(0, 0, 0, 0) };
            
            _caseSensitiveCheck.IsCheckedChanged += (s, e) => ClearSearchCache();
            _wholeWordCheck.IsCheckedChanged += (s, e) => ClearSearchCache();
            _regexCheck.IsCheckedChanged += (s, e) => ClearSearchCache();

            contentStack.Children.Add(_caseSensitiveCheck);
            contentStack.Children.Add(_wholeWordCheck);
            contentStack.Children.Add(_regexCheck);

            // Look in Row
            var lookInRow = new Grid { Margin = new Thickness(0, 5, 0, 0) };
            lookInRow.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(90))); // Label width
            lookInRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));    // Dropdown
            lookInRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));    // Browse button

            var lookInLabel = new TextBlock { Text = "Look in", VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Foreground = Brushes.LightGray };
            lookInRow.Children.Add(lookInLabel);
            Grid.SetColumn(lookInLabel, 0);

            _lookInCombo = new ComboBox
            {
                Background = new SolidColorBrush(Color.Parse("#333337")),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.Parse("#3F3F46")),
                BorderThickness = new Thickness(1),
                Height = 24,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            _lookInCombo.Items.Add("Entire solution");
            _lookInCombo.SelectedIndex = 0;
            lookInRow.Children.Add(_lookInCombo);
            Grid.SetColumn(_lookInCombo, 1);

            var browseBtn = new Button
            {
                Content = "📁",
                Background = new SolidColorBrush(Color.Parse("#2D2D30")),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.Parse("#3F3F46")),
                BorderThickness = new Thickness(1),
                Width = 24,
                Height = 24,
                Margin = new Thickness(4, 0, 0, 0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            lookInRow.Children.Add(browseBtn);
            Grid.SetColumn(browseBtn, 2);
            contentStack.Children.Add(lookInRow);

            // Indented sub-options under Look in
            var lookInOptions = new StackPanel { Margin = new Thickness(90, 0, 0, 0), Spacing = 2 };
            _externalItemsCheck = new CheckBox { Content = "Include external items", FontSize = 12 };
            _miscFilesCheck = new CheckBox { Content = "Include miscellaneous files", FontSize = 12 };
            lookInOptions.Children.Add(_externalItemsCheck);
            lookInOptions.Children.Add(_miscFilesCheck);
            contentStack.Children.Add(lookInOptions);

            // File types Row
            var fileTypesRow = new Grid { Margin = new Thickness(0, 5, 0, 0) };
            fileTypesRow.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(90)));
            fileTypesRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            fileTypesRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

            var fileTypesLabel = new TextBlock { Text = "File types", VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Foreground = Brushes.LightGray };
            fileTypesRow.Children.Add(fileTypesLabel);
            Grid.SetColumn(fileTypesLabel, 0);

            _fileTypesCombo = new TextBox
            {
                Background = new SolidColorBrush(Color.Parse("#333337")),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.Parse("#3F3F46")),
                BorderThickness = new Thickness(1),
                Height = 24,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Text = "!*\\bin\\*;!*\\obj\\*;!*.*\\*"
            };
            fileTypesRow.Children.Add(_fileTypesCombo);
            Grid.SetColumn(_fileTypesCombo, 1);

            var settingsBtn = new Button
            {
                Content = "⚙",
                Background = new SolidColorBrush(Color.Parse("#2D2D30")),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.Parse("#3F3F46")),
                BorderThickness = new Thickness(1),
                Width = 24,
                Height = 24,
                Margin = new Thickness(4, 0, 0, 0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            fileTypesRow.Children.Add(settingsBtn);
            Grid.SetColumn(settingsBtn, 2);
            contentStack.Children.Add(fileTypesRow);

            // Indented link under File types
            var fileTypesLinkPanel = new StackPanel { Margin = new Thickness(90, 0, 0, 0) };
            var linkLabel = new TextBlock
            {
                Text = "Configure global exclusion settings",
                Foreground = new SolidColorBrush(Color.Parse("#81D4FA")),
                FontSize = 11,
                Cursor = new Cursor(StandardCursorType.Hand),
                TextDecorations = TextDecorations.Underline
            };
            fileTypesLinkPanel.Children.Add(linkLabel);
            contentStack.Children.Add(fileTypesLinkPanel);

            // Append results & output ComboBox row
            var resultsRow = new Grid { Margin = new Thickness(0, 5, 0, 0) };
            resultsRow.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(90))); // spacer
            resultsRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));     // Append results check
            resultsRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));     // Results window combo

            _appendResultsCheck = new CheckBox { Content = "Append results", FontSize = 12, Margin = new Thickness(0, 0, 10, 0) };
            resultsRow.Children.Add(_appendResultsCheck);
            Grid.SetColumn(_appendResultsCheck, 1);

            _resultsWindowCombo = new ComboBox
            {
                Background = new SolidColorBrush(Color.Parse("#333337")),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.Parse("#3F3F46")),
                BorderThickness = new Thickness(1),
                Height = 24,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            _resultsWindowCombo.Items.Add("New Window");
            _resultsWindowCombo.Items.Add("Find Results 1");
            _resultsWindowCombo.SelectedIndex = 1;
            resultsRow.Children.Add(_resultsWindowCombo);
            Grid.SetColumn(_resultsWindowCombo, 2);
            contentStack.Children.Add(resultsRow);

            mainGrid.Children.Add(contentStack);
            Grid.SetRow(contentStack, 1);

            // --- Row 2: Bottom Buttons Row ---
            var bottomButtonsGrid = new Grid { Margin = new Thickness(0, 15, 0, 0) };
            bottomButtonsGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto)); // Left horizontal stack
            bottomButtonsGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star)); // spacer
            bottomButtonsGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto)); // Right Find/Replace All button

            var leftButtonsStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };

            _findPrevBtn = CreateStandardButton("Find Previous", () => FindPrevious());
            _findNextBtn = CreateStandardButton("Find Next", () => FindNext());
            _skipFileBtn = CreateStandardButton("Skip File", () => { });

            leftButtonsStack.Children.Add(_findPrevBtn);
            leftButtonsStack.Children.Add(_findNextBtn);
            leftButtonsStack.Children.Add(_skipFileBtn);
            bottomButtonsGrid.Children.Add(leftButtonsStack);
            Grid.SetColumn(leftButtonsStack, 0);

            // Find All / Replace All (purple highlight accent)
            _findAllBtn = new Button
            {
                Content = "Find All",
                Background = new SolidColorBrush(Color.Parse("#7C7AF8")),
                Foreground = Brushes.White,
                BorderBrush = Brushes.Transparent,
                Height = 26,
                FontSize = 12,
                Padding = new Thickness(20, 0, 20, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand),
                IsVisible = !showReplace
            };
            _findAllBtn.Click += (s, e) => TriggerSearchAll();
            bottomButtonsGrid.Children.Add(_findAllBtn);
            Grid.SetColumn(_findAllBtn, 2);

            _replaceAllBtn = new Button
            {
                Content = "Replace All",
                Background = new SolidColorBrush(Color.Parse("#7C7AF8")),
                Foreground = Brushes.White,
                BorderBrush = Brushes.Transparent,
                Height = 26,
                FontSize = 12,
                Padding = new Thickness(20, 0, 20, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand),
                IsVisible = showReplace
            };
            _replaceAllBtn.Click += (s, e) => ExecuteReplaceAll();
            bottomButtonsGrid.Children.Add(_replaceAllBtn);
            Grid.SetColumn(_replaceAllBtn, 2);

            mainGrid.Children.Add(bottomButtonsGrid);
            Grid.SetRow(bottomButtonsGrid, 2);

            Content = mainGrid;

            // Set mode initially
            _isReplaceMode = showReplace;

            // Auto-focus search text
            var selected = selectedText(owner);
            FocusSearch(selected, showReplace);
        }

        public void FocusSearch(string? initialText, bool showReplace)
        {
            SwitchTab(showReplace);
            if (initialText != null)
            {
                _findBox.Text = initialText;
            }
            _findBox.Focus();
            _findBox.SelectAll();
        }

        private Button CreateStandardButton(string text, Action action)
        {
            var btn = new Button
            {
                Content = text,
                Background = new SolidColorBrush(Color.Parse("#2D2D30")),
                Foreground = Brushes.LightGray,
                BorderBrush = new SolidColorBrush(Color.Parse("#3F3F46")),
                BorderThickness = new Thickness(1),
                Height = 26,
                FontSize = 11,
                Padding = new Thickness(12, 0, 12, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            btn.Click += (s, e) => action();
            return btn;
        }

        private void SwitchTab(bool replace)
        {
            _isReplaceMode = replace;
            _findTabHeader.Foreground = replace ? Brushes.Gray : Brushes.White;
            _findTabUnderline.Background = replace ? Brushes.Transparent : new SolidColorBrush(Color.Parse("#7C7AF8"));
            _replaceTabHeader.Foreground = replace ? Brushes.White : Brushes.Gray;
            _replaceTabUnderline.Background = replace ? new SolidColorBrush(Color.Parse("#7C7AF8")) : Brushes.Transparent;

            _replaceRow.IsVisible = replace;
            _replaceAllBtn.IsVisible = replace;
            _findAllBtn.IsVisible = !replace;

            Height = replace ? 530 : 500;
            ClearSearchCache();
        }

        private static string? selectedText(ShellWindow window)
        {
            string sel = window.ActiveCanvas.GetSelectedText(out _, out _);
            return (string.IsNullOrEmpty(sel) || sel.Contains('\n') || sel.Contains('\r')) ? null : sel;
        }

        private void OnInputKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                if (_isReplaceMode)
                {
                    ExecuteReplaceAll();
                }
                else
                {
                    TriggerSearchAll();
                }
                e.Handled = true;
            }
        }

        private void ClearSearchCache()
        {
            _searchResults.Clear();
            _currentResultIndex = -1;
            _lastSearchQuery = "";
        }

        private async Task<bool> RunSearchIfNeeded()
        {
            string query = _findBox.Text ?? "";
            if (string.IsNullOrEmpty(query)) return false;

            if (_searchResults.Count == 0 || _lastSearchQuery != query)
            {
                await SearchWorkspaceInternal();
            }

            return _searchResults.Count > 0;
        }

        private async void FindNext()
        {
            bool hasResults = await RunSearchIfNeeded();
            if (!hasResults) return;

            _currentResultIndex = (_currentResultIndex + 1) % _searchResults.Count;
            NavigateToResult(_searchResults[_currentResultIndex]);
        }

        private async void FindPrevious()
        {
            bool hasResults = await RunSearchIfNeeded();
            if (!hasResults) return;

            _currentResultIndex = (_currentResultIndex - 1 + _searchResults.Count) % _searchResults.Count;
            NavigateToResult(_searchResults[_currentResultIndex]);
        }

        private void NavigateToResult(SearchResult res)
        {
            string? wsPath = _owner.WorkspaceRootPath;
            if (string.IsNullOrEmpty(wsPath)) return;
            string scanRoot = File.Exists(wsPath) ? (Path.GetDirectoryName(wsPath) ?? wsPath) : wsPath;
            string fullPath = Path.Combine(scanRoot, res.FilePath);

            _owner.LoadFile(fullPath);
            _owner.MoveCaretToLine(res.LineNumber - 1);
            _owner.ReturnFocusToEditor();
        }

        private async Task SearchWorkspaceInternal()
        {
            string query = _findBox.Text ?? "";
            if (string.IsNullOrEmpty(query)) return;

            string? wsPath = _owner.WorkspaceRootPath;
            if (string.IsNullOrEmpty(wsPath)) return;

            string scanRoot = wsPath;
            if (File.Exists(wsPath))
            {
                scanRoot = Path.GetDirectoryName(wsPath) ?? wsPath;
            }

            bool isRegex = _regexCheck.IsChecked == true;
            bool matchWholeWord = _wholeWordCheck.IsChecked == true;
            bool caseSensitive = _caseSensitiveCheck.IsChecked == true;

            if (matchWholeWord)
            {
                string escapedQuery = isRegex ? query : Regex.Escape(query);
                query = $"\\b{escapedQuery}\\b";
                isRegex = true;
            }

            // Parse file filters
            string fileTypesText = _fileTypesCombo.Text ?? "";
            string[] fileFilters = fileTypesText.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var inclusions = fileFilters.Where(t => !t.StartsWith("!")).ToList();
            var exclusions = fileFilters.Where(t => t.StartsWith("!")).Select(t => t.Substring(1)).ToList();

            try
            {
                var searchEngine = new SearchEngine(scanRoot);
                var results = await searchEngine.SearchAsync(query, isRegex: isRegex, caseSensitive: caseSensitive);

                // Post-filter by inclusions/exclusions
                if (fileFilters.Length > 0)
                {
                    results = results.Where(r =>
                    {
                        string fileName = Path.GetFileName(r.FilePath);
                        if (inclusions.Count > 0)
                        {
                            bool matchesIn = false;
                            foreach (var inc in inclusions)
                            {
                                if (System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(inc, fileName, ignoreCase: true))
                                {
                                    matchesIn = true;
                                    break;
                                }
                            }
                            if (!matchesIn) return false;
                        }
                        if (exclusions.Count > 0)
                        {
                            foreach (var exc in exclusions)
                            {
                                if (System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(exc, fileName, ignoreCase: true) ||
                                    r.FilePath.Contains(exc.Replace("*", "").Replace("\\", "/"), StringComparison.OrdinalIgnoreCase))
                                {
                                    return false;
                                }
                            }
                        }
                        return true;
                    }).ToList();
                }

                _searchResults = results;
                _lastSearchQuery = _findBox.Text ?? "";
                _currentResultIndex = -1;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FindReplaceFilesWindow] Search error: {ex.Message}");
            }
        }

        private async void TriggerSearchAll()
        {
            await SearchWorkspaceInternal();
            _owner.DisplayWorkspaceSearchResults(_findBox.Text ?? "", _searchResults);
        }

        private void ExecuteReplaceAll()
        {
            string find = _findBox.Text ?? "";
            string replace = _replaceBox.Text ?? "";

            if (string.IsNullOrEmpty(find)) return;

            bool isRegex = _regexCheck.IsChecked == true;
            bool matchWholeWord = _wholeWordCheck.IsChecked == true;
            bool caseSensitive = _caseSensitiveCheck.IsChecked == true;

            string searchPattern = find;
            if (matchWholeWord)
            {
                string escapedQuery = isRegex ? find : Regex.Escape(find);
                searchPattern = $"\\b{escapedQuery}\\b";
            }

            _owner.ReplaceWorkspaceMatches(searchPattern, replace, caseSensitive);
            TriggerSearchAll();
        }
    }
}
