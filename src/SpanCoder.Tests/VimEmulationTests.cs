using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Headless.XUnit;
using SpanCoder.Shell;
using SpanCoder.Engine;
using SpanCoder.Contracts;

namespace SpanCoder.Tests
{
    public class VimEmulationTests : IDisposable
    {
        private class TestCanvas : TextEditorCanvas
        {
            public void TriggerKeyDown(KeyEventArgs e) => OnKeyDown(e);
            public void TriggerTextInput(TextInputEventArgs e) => OnTextInput(e);

            public void SetVimEnabled(bool enabled)
            {
                SettingsManager.Set("editor.vimEnabled", enabled.ToString().ToLower());
                // Force sync load
                typeof(TextEditorCanvas)
                    .GetMethod("LoadSettings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    ?.Invoke(this, null);
            }
        }

        public VimEmulationTests()
        {
            // Reset setting to a clean starting state
            SettingsManager.Set("editor.vimEnabled", "false");
        }

        public void Dispose()
        {
            // Reset setting after test runs to not affect other test classes
            SettingsManager.Set("editor.vimEnabled", "false");
        }

        [AvaloniaFact]
        public void TestVimModeToggleAndDefaultState()
        {
            var canvas = new TestCanvas();
            var doc = new Document(1, "line one\nline two".AsMemory());
            canvas.Document = doc;

            // Initially, Vim should be disabled and mode is Insert
            Assert.False(canvas.VimEnabled);
            Assert.Equal(VimMode.Insert, canvas.VimMode);

            // Enable Vim
            canvas.SetVimEnabled(true);
            Assert.True(canvas.VimEnabled);
            // Enabling Vim transitions it to Normal Mode
            Assert.Equal(VimMode.Normal, canvas.VimMode);

            // Disable Vim
            canvas.SetVimEnabled(false);
            Assert.False(canvas.VimEnabled);
            Assert.Equal(VimMode.Insert, canvas.VimMode);
        }

        [AvaloniaFact]
        public void TestVimNormalModeNavigationKeys()
        {
            var canvas = new TestCanvas();
            var doc = new Document(1, "line one\nline two\nline three".AsMemory());
            canvas.Document = doc;
            canvas.SetVimEnabled(true); // Normal Mode

            // Move Caret to (1, 5)
            canvas.MoveCaret(1, 5);
            Assert.Equal(1, canvas.CaretLine);
            Assert.Equal(5, canvas.CaretCol);

            // Trigger 'h' (move left)
            canvas.TriggerKeyDown(new KeyEventArgs { Key = Key.H, Route = RoutingStrategies.Bubble });
            Assert.Equal(1, canvas.CaretLine);
            Assert.Equal(4, canvas.CaretCol);

            // Trigger 'l' (move right)
            canvas.TriggerKeyDown(new KeyEventArgs { Key = Key.L, Route = RoutingStrategies.Bubble });
            Assert.Equal(1, canvas.CaretLine);
            Assert.Equal(5, canvas.CaretCol);

            // Trigger 'k' (move up)
            canvas.TriggerKeyDown(new KeyEventArgs { Key = Key.K, Route = RoutingStrategies.Bubble });
            Assert.Equal(0, canvas.CaretLine);
            Assert.Equal(5, canvas.CaretCol);

            // Trigger 'j' (move down)
            canvas.TriggerKeyDown(new KeyEventArgs { Key = Key.J, Route = RoutingStrategies.Bubble });
            Assert.Equal(1, canvas.CaretLine);
            Assert.Equal(5, canvas.CaretCol);
        }

        [AvaloniaFact]
        public void TestVimWordNavigation()
        {
            var canvas = new TestCanvas();
            var doc = new Document(1, "public void MethodName()".AsMemory());
            canvas.Document = doc;
            canvas.SetVimEnabled(true); // Normal Mode

            canvas.MoveCaret(0, 0);

            // Trigger 'w' (word forward)
            canvas.TriggerKeyDown(new KeyEventArgs { Key = Key.W, Route = RoutingStrategies.Bubble });
            // Should jump to 'void' (index 7)
            Assert.Equal(7, canvas.CaretCol);

            // Trigger 'w' again
            canvas.TriggerKeyDown(new KeyEventArgs { Key = Key.W, Route = RoutingStrategies.Bubble });
            // Should jump to 'MethodName' (index 12)
            Assert.Equal(12, canvas.CaretCol);

            // Trigger 'b' (word backward)
            canvas.TriggerKeyDown(new KeyEventArgs { Key = Key.B, Route = RoutingStrategies.Bubble });
            // Should jump back to 'void' (index 7)
            Assert.Equal(7, canvas.CaretCol);
        }

        [AvaloniaFact]
        public void TestVimModeTransitions()
        {
            var canvas = new TestCanvas();
            var doc = new Document(1, "text".AsMemory());
            canvas.Document = doc;
            canvas.SetVimEnabled(true); // starts in Normal mode

            // 1. Trigger 'i' to enter Insert mode
            canvas.TriggerKeyDown(new KeyEventArgs { Key = Key.I, Route = RoutingStrategies.Bubble });
            Assert.Equal(VimMode.Insert, canvas.VimMode);

            // 2. Trigger 'Escape' to go back to Normal mode
            canvas.TriggerKeyDown(new KeyEventArgs { Key = Key.Escape, Route = RoutingStrategies.Bubble });
            Assert.Equal(VimMode.Normal, canvas.VimMode);

            // 3. Trigger 'a' to enter Insert mode (should advance column if possible)
            canvas.MoveCaret(0, 0);
            canvas.TriggerKeyDown(new KeyEventArgs { Key = Key.A, Route = RoutingStrategies.Bubble });
            Assert.Equal(VimMode.Insert, canvas.VimMode);
            Assert.Equal(1, canvas.CaretCol); // advanced from 0 to 1
        }

        [AvaloniaFact]
        public void TestVimCharacterAndLineDeletion()
        {
            var canvas = new TestCanvas();
            var doc = new Document(1, "line one\nline two\nline three".AsMemory());
            canvas.Document = doc;
            canvas.SetVimEnabled(true); // Normal Mode

            int? deletedOffset = null;
            int? deletedLen = null;

            canvas.TextDeleteReceived += (offset, len) =>
            {
                deletedOffset = offset;
                deletedLen = len;
            };

            // 1. Test 'x' deletion
            canvas.MoveCaret(0, 5); // caret on 'o' of line one
            canvas.TriggerKeyDown(new KeyEventArgs { Key = Key.X, Route = RoutingStrategies.Bubble });
            Assert.Equal(5, deletedOffset);
            Assert.Equal(1, deletedLen);

            // 2. Test 'dd' deletion
            canvas.MoveCaret(1, 2); // line two
            deletedOffset = null;
            deletedLen = null;

            // Trigger first 'd'
            canvas.TriggerKeyDown(new KeyEventArgs { Key = Key.D, Route = RoutingStrategies.Bubble });
            // Trigger second 'd'
            canvas.TriggerKeyDown(new KeyEventArgs { Key = Key.D, Route = RoutingStrategies.Bubble });

            // Line 1 start is 9, next line start is 18 (line two + newline = 9 chars)
            Assert.Equal(9, deletedOffset);
            Assert.Equal(9, deletedLen);
        }

        [AvaloniaFact]
        public void TestVimNewLineInsertion()
        {
            var canvas = new TestCanvas();
            var doc = new Document(1, "  line".AsMemory());
            canvas.Document = doc;
            canvas.SetVimEnabled(true); // Normal Mode

            int? insertedOffset = null;
            string? insertedText = null;

            canvas.TextInputReceived += (offset, text) =>
            {
                insertedOffset = offset;
                insertedText = text;
            };

            // Trigger 'o' (newline below)
            canvas.TriggerKeyDown(new KeyEventArgs { Key = Key.O, Route = RoutingStrategies.Bubble });
            
            // Expected newline + leading 2 spaces (auto-indent)
            Assert.Equal("\n  ", insertedText);
            Assert.Equal(VimMode.Insert, canvas.VimMode);
        }

        [AvaloniaFact]
        public void TestVimNormalModeKeysNoDocEdit()
        {
            var canvas = new TestCanvas();
            var doc = new Document(1, "line one\nline two\nline three".AsMemory());
            canvas.Document = doc;
            canvas.SetVimEnabled(true); // Normal Mode

            canvas.MoveCaret(0, 4);

            bool editReceived = false;
            canvas.TextInputReceived += (offset, text) => editReceived = true;
            canvas.TextDeleteReceived += (offset, len) => editReceived = true;
            canvas.BatchEditReceived += (edits) => editReceived = true;

            // Trigger Enter
            var enterEventArgs = new KeyEventArgs { Key = Key.Enter, Route = RoutingStrategies.Bubble };
            canvas.TriggerKeyDown(enterEventArgs);
            Assert.True(enterEventArgs.Handled);
            Assert.False(editReceived);
            // In Normal mode, Enter should move the caret down to line 1
            Assert.Equal(1, canvas.CaretLine);

            // Move back up
            canvas.MoveCaret(0, 4);

            // Trigger Back
            var backEventArgs = new KeyEventArgs { Key = Key.Back, Route = RoutingStrategies.Bubble };
            canvas.TriggerKeyDown(backEventArgs);
            Assert.True(backEventArgs.Handled);
            Assert.False(editReceived);

            // Trigger Delete
            var deleteEventArgs = new KeyEventArgs { Key = Key.Delete, Route = RoutingStrategies.Bubble };
            canvas.TriggerKeyDown(deleteEventArgs);
            Assert.True(deleteEventArgs.Handled);
            Assert.False(editReceived);

            // Trigger Tab
            var tabEventArgs = new KeyEventArgs { Key = Key.Tab, Route = RoutingStrategies.Bubble };
            canvas.TriggerKeyDown(tabEventArgs);
            Assert.True(tabEventArgs.Handled);
            Assert.False(editReceived);
        }
    }
}
