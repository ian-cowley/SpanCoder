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
            var mainGrid = window.Content as Grid;
            Assert.NotNull(mainGrid);

            var workspaceGrid = mainGrid.Children.OfType<Grid>().First(g => Grid.GetRow(g) == 2);
            var editorPane = workspaceGrid.Children.OfType<Grid>().First(g => Grid.GetColumn(g) == 2);
            var editorGrid = editorPane.Children.OfType<Grid>().First(g => Grid.GetRow(g) == 1);
            var canvas = editorGrid.Children.OfType<TextEditorCanvas>().First();

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
    }
}
