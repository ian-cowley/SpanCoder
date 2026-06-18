using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
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
    }
}
