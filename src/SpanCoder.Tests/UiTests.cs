using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using SpanCoder.Shell;
using Xunit;

namespace SpanCoder.Tests
{
    public class UiTests
    {
        [AvaloniaFact]
        public void TestEditorSettingsCanvasBinding()
        {
            var window = new ShellWindow();
            window.InitializeLayout();

            // Locate editor canvas
            var canvas = window.ActiveCanvas;

            Assert.NotNull(canvas);

            // Set settings font size
            SettingsManager.Set("editor.fontSize", "20");
            
            // Wait for dispatch loop
            Dispatcher.UIThread.RunJobs();

            // Check that LineHeight updated (LineHeight = fontSize + 6.0)
            Assert.Equal(26.0, canvas.LineHeight);

            // Change font family
            SettingsManager.Set("editor.fontFamily", "Arial");
            Dispatcher.UIThread.RunJobs();

            // Revert settings to default
            SettingsManager.Set("editor.fontSize", "14");
            SettingsManager.Set("editor.fontFamily", "Consolas");
            Dispatcher.UIThread.RunJobs();
        }

        [AvaloniaFact]
        public void TestSettingsWindowUIConstruction()
        {
            var settingsWindow = new SettingsWindow();
            
            // Verify search box and list box are created
            var mainGrid = settingsWindow.Content as Grid;
            Assert.NotNull(mainGrid);

            var contentGrid = mainGrid.Children.OfType<Grid>().First();
            var categoryList = contentGrid.Children.OfType<ListBox>().First();
            
            // Should contain default categories
            var categories = categoryList.ItemsSource?.Cast<string>().ToList();
            Assert.NotNull(categories);
            Assert.Contains("All", categories);
            Assert.Contains("Text Editor", categories);
            Assert.Contains("Workbench", categories);
            Assert.Contains("Extensions", categories);

            settingsWindow.Close();
        }

        [AvaloniaFact]
        public void TestSidebarFileTreeHorizontalScrollDisabled()
        {
            var fileTree = new SidebarFileTree();
            
            // Assert that horizontal scrollbar is explicitly disabled to prevent jumping
            var horizontalScrollVisible = fileTree.GetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty);
            Assert.Equal(Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, horizontalScrollVisible);
        }

        [AvaloniaFact]
        public void TestPerformanceGraphsControlInit()
        {
            var perfControl = new PerformanceGraphsControl();
            
            // Verify it inherits from Control and does not crash on initialization
            Assert.NotNull(perfControl);
            Assert.True(perfControl.ClipToBounds);
        }

        [AvaloniaFact]
        public void TestShellWindowOnClosingNoLingering()
        {
            var window = new ShellWindow();
            window.InitializeLayout();
            
            var ctor = typeof(WindowClosingEventArgs).GetConstructors(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .OrderBy(c => c.GetParameters().Length)
                .FirstOrDefault();
            Assert.NotNull(ctor);
            var parameters = ctor.GetParameters().Select(p => p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null).ToArray();
            var e = (WindowClosingEventArgs)ctor.Invoke(parameters);

            var method = typeof(ShellWindow).GetMethod("OnClosing", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(method);
            method.Invoke(window, new object[] { e });
            
            Assert.False(e.Cancel);
        }

        [AvaloniaFact]
        public void TestViewAppearanceToggles()
        {
            var window = new ShellWindow();
            window.InitializeLayout();

            // 1. Menu Bar Toggle
            Assert.True(window._mainMenu.IsVisible);
            window.ToggleMenu();
            Assert.False(window._mainMenu.IsVisible);
            window.ToggleMenu();
            Assert.True(window._mainMenu.IsVisible);

            // 2. Toolbar Toggle
            Assert.True(window._toolbarBorder.IsVisible);
            window.ToggleToolbar();
            Assert.False(window._toolbarBorder.IsVisible);
            window.ToggleToolbar();
            Assert.True(window._toolbarBorder.IsVisible);

            // 3. Sidebar Toggle
            Assert.True(window._sidebarCol.Width.Value > 0);
            Assert.True(window._sidebarSplitter.IsVisible);
            Assert.True(window._sidebarTabControl.IsVisible);
            window.ToggleSidebar();
            Assert.Equal(0, window._sidebarCol.Width.Value);
            Assert.False(window._sidebarSplitter.IsVisible);
            Assert.False(window._sidebarTabControl.IsVisible);
            window.ToggleSidebar();
            Assert.True(window._sidebarCol.Width.Value > 0);
            Assert.True(window._sidebarSplitter.IsVisible);
            Assert.True(window._sidebarTabControl.IsVisible);

            // 4. Bottom Panel Toggle
            Assert.True(window._editorPaneGrid.RowDefinitions[2].Height.Value > 0);
            Assert.True(window._bottomSplitter.IsVisible);
            Assert.True(window._bottomTabControl.IsVisible);
            window.ToggleBottomPanel();
            Assert.Equal(0, window._editorPaneGrid.RowDefinitions[2].Height.Value);
            Assert.False(window._bottomSplitter.IsVisible);
            Assert.False(window._bottomTabControl.IsVisible);
            window.ToggleBottomPanel();
            Assert.True(window._editorPaneGrid.RowDefinitions[2].Height.Value > 0);
            Assert.True(window._bottomSplitter.IsVisible);
            Assert.True(window._bottomTabControl.IsVisible);

            // 5. Status Bar Toggle
            Assert.True(window._statusBarGrid.IsVisible);
            window.ToggleStatusBar();
            Assert.False(window._statusBarGrid.IsVisible);
            window.ToggleStatusBar();
            Assert.True(window._statusBarGrid.IsVisible);
        }

        [AvaloniaFact]
        public void TestToggleZenMode()
        {
            var window = new ShellWindow();
            window.InitializeLayout();

            // Initial state: not in Zen mode
            Assert.False(window._zenMode);
            Assert.True(window._activePane.Canvas.IsGutterVisible);
            Assert.True(window._mainMenu.IsVisible);
            Assert.True(window._toolbarBorder.IsVisible);
            Assert.True(window._statusBarGrid.IsVisible);
            Assert.True(window._sidebarTabControl.IsVisible);
            Assert.True(window._bottomTabControl.IsVisible);

            // Toggle Zen mode ON
            window.ToggleZenMode();
            Assert.True(window._zenMode);
            Assert.False(window._activePane.Canvas.IsGutterVisible);
            Assert.False(window._mainMenu.IsVisible);
            Assert.False(window._toolbarBorder.IsVisible);
            Assert.False(window._statusBarGrid.IsVisible);
            Assert.False(window._sidebarTabControl.IsVisible);
            Assert.False(window._bottomTabControl.IsVisible);

            // Toggle Zen mode OFF
            window.ToggleZenMode();
            Assert.False(window._zenMode);
            Assert.True(window._activePane.Canvas.IsGutterVisible);
            Assert.True(window._mainMenu.IsVisible);
            Assert.True(window._toolbarBorder.IsVisible);
            Assert.True(window._statusBarGrid.IsVisible);
            Assert.True(window._sidebarTabControl.IsVisible);
            Assert.True(window._bottomTabControl.IsVisible);
        }

        [Fact]
        public void TestDiffAlgorithm()
        {
            string[] left = new[] { "line1", "line2", "line3" };
            string[] right = new[] { "line1", "line2-mod", "line3", "line4" };

            var diff = DiffAlgorithm.ComputeDiff(left, right);

            // Expect:
            // 1. Unchanged: line1
            // 2. Deleted: line2 (from left)
            // 3. Added: line2-mod (to right)
            // 4. Unchanged: line3
            // 5. Added: line4 (to right)
            Assert.Equal(5, diff.Count);

            Assert.Equal(DiffType.Unchanged, diff[0].Type);
            Assert.Equal(1, diff[0].LeftLineNumber);
            Assert.Equal(1, diff[0].RightLineNumber);

            Assert.Equal(DiffType.Deleted, diff[1].Type);
            Assert.Equal(2, diff[1].LeftLineNumber);
            Assert.Null(diff[1].RightLineNumber);

            Assert.Equal(DiffType.Added, diff[2].Type);
            Assert.Null(diff[2].LeftLineNumber);
            Assert.Equal(2, diff[2].RightLineNumber);

            Assert.Equal(DiffType.Unchanged, diff[3].Type);
            Assert.Equal(3, diff[3].LeftLineNumber);
            Assert.Equal(3, diff[3].RightLineNumber);

            Assert.Equal(DiffType.Added, diff[4].Type);
            Assert.Null(diff[4].LeftLineNumber);
            Assert.Equal(4, diff[4].RightLineNumber);
        }

        [AvaloniaFact]
        public void TestOpenGitDiffPage()
        {
            var window = new ShellWindow();
            window.InitializeLayout();

            Assert.Empty(window._activePane.OpenDocuments);

            window.OpenGitDiffPage("src/SpanCoder.Shell/ShellWindow.cs");

            Assert.Single(window._activePane.OpenDocuments);
            var doc = window._activePane.OpenDocuments[0];
            Assert.Equal("gitdiff://src/SpanCoder.Shell/ShellWindow.cs", doc.FilePath);
            Assert.Equal(-888, doc.Id);
        }

        [AvaloniaFact]
        public void TestCommandPaletteCustomMode()
        {
            var window = new ShellWindow();
            window.InitializeLayout();

            var palette = window.CommandPalette;
            Assert.NotNull(palette);

            // Test custom mode activation
            bool callbackExecuted = false;
            SearchItem? selectedItem = null;
            var items = new List<SearchItem>
            {
                new SearchItem("Branch A", "Local branch", "", "Action", "branch-a"),
                new SearchItem("Branch B", "Local branch", "", "Action", "branch-b")
            };

            palette.ShowCustom("Select branch", items, (selected) =>
            {
                callbackExecuted = true;
                selectedItem = selected;
            });

            Assert.True(palette.IsCustomMode);
            Assert.NotNull(palette.ListBox.ItemsSource);
            
            // Check filtered items
            Assert.Equal(2, palette.FilteredItems.Count);

            // Select first item and commit
            palette.ListBox.SelectedIndex = 0;
            palette.TriggerCommitSelection();

            Assert.True(callbackExecuted);
            Assert.NotNull(selectedItem);
            Assert.Equal("Branch A", selectedItem.DisplayName);
            Assert.Equal("branch-a", selectedItem.AssociatedData);
        }

        [AvaloniaFact]
        public async Task TestGitBranchStatusBlockOpensSwitcher()
        {
            var window = new ShellWindow();
            window.InitializeLayout();

            // Set a dummy workspace root to initialize git provider working directory
            var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tempDir);
            try
            {
                window.OpenFile(Path.Combine(tempDir, "test.txt"));
                
                // Manually trigger the branch switcher popup
                await window.OpenBranchSwitcherPaletteAsync();

                // Assert command palette shows custom mode with branches
                Assert.True(window.CommandPalette.IsCustomMode);
                Assert.Contains(window.CommandPalette.FilteredItems, item => item.DisplayName == "Create new branch...");
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [AvaloniaFact]
        public void TestGitToolbarButtonsInstantiationAndHandlers()
        {
            var window = new ShellWindow();
            window.InitializeLayout();

            Assert.NotNull(window.BtnGitPull);
            Assert.NotNull(window.BtnGitStageAll);
            Assert.NotNull(window.BtnGitUnstageAll);

            // Verify they have Hand cursors
            Assert.NotNull(window.BtnGitPull.Cursor);
            Assert.NotNull(window.BtnGitStageAll.Cursor);
            Assert.NotNull(window.BtnGitUnstageAll.Cursor);
        }

        [AvaloniaFact]
        public void TestEditorSplitDraggableControl()
        {
            var window = new ShellWindow();
            window.InitializeLayout();
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // 1. Get the current active EditorPane
            var pane = window._activePane;
            Assert.NotNull(pane);

            // 2. Verify SplitHandle exists with correct properties
            Assert.NotNull(pane.SplitHandle);
            Assert.Equal(8, pane.SplitHandle.Height);
            Assert.NotNull(pane.SplitHandle.Cursor);

            // 3. Verify horizontal split button is removed from tabs action panel
            var actionPanelButtons = pane.GetLogicalDescendants().OfType<Button>().ToList();
            Assert.DoesNotContain(actionPanelButtons, b => b.Content as string == "—");

            // 4. Verify splitting logic via pointer drag simulation
            var panesBefore = window.GetLogicalDescendants().OfType<EditorPane>().ToList();
            Assert.Single(panesBefore);

            // Simulate pointer pressed
            var pressedArgs = new PointerPressedEventArgs(
                pane.SplitHandle,
                new Pointer(0, PointerType.Mouse, true),
                pane.SplitHandle,
                new Point(0, 0),
                0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
                KeyModifiers.None
            );
            pane.SplitHandle.RaiseEvent(pressedArgs);

            // Simulate pointer moved down by 40 pixels (threshold is 30)
            var movedArgs = new PointerEventArgs(
                InputElement.PointerMovedEvent,
                pane.SplitHandle,
                new Pointer(0, PointerType.Mouse, true),
                pane,
                new Point(0, 40),
                0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other),
                KeyModifiers.None
            );
            pane.SplitHandle.RaiseEvent(movedArgs);

            // Simulate pointer released to trigger the split
            var releasedArgs = new PointerReleasedEventArgs(
                pane.SplitHandle,
                new Pointer(0, PointerType.Mouse, true),
                pane,
                new Point(0, 40),
                0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonReleased),
                KeyModifiers.None,
                MouseButton.Left
            );
            pane.SplitHandle.RaiseEvent(releasedArgs);

            // Run UI jobs to process the split layout changes
            Dispatcher.UIThread.RunJobs();

            var panesAfter = window.GetLogicalDescendants().OfType<EditorPane>().ToList();
            Assert.Equal(2, panesAfter.Count);

            // Clean up window
            window.Close();
        }
    }
}
