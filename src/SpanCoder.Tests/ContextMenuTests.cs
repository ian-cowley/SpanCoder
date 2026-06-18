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
    public class ContextMenuTests
    {
        private class TestCanvas : TextEditorCanvas
        {
            public void TriggerPointerPressed(PointerPressedEventArgs e) => OnPointerPressed(e);

            public bool CallIsCaretOnWord()
            {
                return (bool)typeof(TextEditorCanvas)
                    .GetMethod("IsCaretOnWord", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    ?.Invoke(this, null)!;
            }
        }

        [AvaloniaFact]
        public void TestIsCaretOnWord()
        {
            var canvas = new TestCanvas();
            var doc = new Document(1, "int value = 42; // comment".AsMemory());
            canvas.Document = doc;

            // 1. On "int" (word character)
            canvas.MoveCaret(0, 1);
            Assert.True(canvas.CallIsCaretOnWord());

            // 2. On space (non-word character)
            canvas.MoveCaret(0, 3);
            Assert.False(canvas.CallIsCaretOnWord());

            // 3. On "value" (word character)
            canvas.MoveCaret(0, 5);
            Assert.True(canvas.CallIsCaretOnWord());

            // 4. On "=" (non-word character)
            canvas.MoveCaret(0, 10);
            Assert.False(canvas.CallIsCaretOnWord());

            // 5. On "42" (word character)
            canvas.MoveCaret(0, 13);
            Assert.True(canvas.CallIsCaretOnWord());

            // 6. On ";" (non-word character)
            canvas.MoveCaret(0, 14);
            Assert.False(canvas.CallIsCaretOnWord());
        }

        [AvaloniaFact]
        public void TestExtensionContextMenuItems()
        {
            var canvas = new TestCanvas();

            // Register items manually
            canvas.ExtensionContextMenuItems.Add(new TextEditorCanvas.ContextMenuItem
            {
                Header = "Format Document",
                CommandId = "ext.format",
                ExtensionId = "formatter-plugin"
            });

            Assert.Single(canvas.ExtensionContextMenuItems);
            var item = canvas.ExtensionContextMenuItems.First();
            Assert.Equal("Format Document", item.Header);
            Assert.Equal("ext.format", item.CommandId);
            Assert.Equal("formatter-plugin", item.ExtensionId);

            // Cleanup
            canvas.ExtensionContextMenuItems.RemoveAll(x => x.ExtensionId == "formatter-plugin");
            Assert.Empty(canvas.ExtensionContextMenuItems);
        }

        [AvaloniaFact]
        public void TestSelectWordAt()
        {
            var canvas = new TestCanvas();
            var doc = new Document(1, "int myVariable = 42;".AsMemory());
            canvas.Document = doc;

            // 1. Select "myVariable" (cols 4 to 13)
            // Select inside "myVariable" at col 6
            canvas.SelectWordAt(0, 6);

            // Verify selection matches "myVariable"
            bool hasSelection = canvas.HasSelection(out int start, out int len);
            Assert.True(hasSelection);
            string selectedText = canvas.GetSelectedText(out _, out _);
            Assert.Equal("myVariable", selectedText);

            // Verify caret moved to the end of the selection (offset 14)
            Assert.Equal(14, canvas.CaretAbsoluteOffset);

            // 2. Select "42" (cols 17 to 18)
            canvas.SelectWordAt(0, 17);
            hasSelection = canvas.HasSelection(out start, out len);
            Assert.True(hasSelection);
            selectedText = canvas.GetSelectedText(out _, out _);
            Assert.Equal("42", selectedText);
            Assert.Equal(19, canvas.CaretAbsoluteOffset);
        }

        [AvaloniaFact]
        public void TestSelectLineAt()
        {
            var canvas = new TestCanvas();
            var doc = new Document(1, "line 1\nline 2 with text\nline 3".AsMemory());
            canvas.Document = doc;

            // Select line 1 (index 1)
            canvas.SelectLineAt(1);

            bool hasSelection = canvas.HasSelection(out int start, out int len);
            Assert.True(hasSelection);
            string selectedText = canvas.GetSelectedText(out _, out _);
            // Selected line 1 starts at "line 2 with text\n" (including trailing newline)
            Assert.Equal("line 2 with text\n", selectedText);
            
            // Caret should move to the end of the line (beginning of line 2, offset 24)
            Assert.Equal(24, canvas.CaretAbsoluteOffset);
        }
    }
}
