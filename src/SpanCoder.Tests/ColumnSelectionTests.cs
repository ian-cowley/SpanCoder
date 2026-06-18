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
    public class ColumnSelectionTests
    {
        private class TestCanvas : TextEditorCanvas
        {
            public void TriggerKeyDown(KeyEventArgs e) => OnKeyDown(e);
            public void TriggerTextInput(TextInputEventArgs e) => OnTextInput(e);
            
            // Expose a helper to directly set block dragging state for testing
            public void SetBlockDraggingState(int startLine, int startCol)
            {
                var doc = Document;
                if (doc == null) return;
                
                // Let's populate some extra carets
                for (int line = startLine; line < doc.GetLineCount(); line++)
                {
                    if (line == startLine) continue;
                    long lineStart = doc.GetLineStart(line);
                    long end = (line + 1 < doc.GetLineCount()) ? doc.GetLineStart(line + 1) : doc.Length;
                    
                    var lineSpan = doc.GetLine(line, out _, out var rented);
                    int printableLen = (int)(end - lineStart);
                    if (printableLen > 0 && lineSpan[printableLen - 1] == '\n') printableLen--;
                    if (printableLen > 0 && lineSpan[printableLen - 1] == '\r') printableLen--;
                    if (rented != null) System.Buffers.ArrayPool<char>.Shared.Return(rented);
                    
                    int col = Math.Min(startCol, printableLen);
                    col = Math.Max(0, col);
                    
                    var field = typeof(TextEditorCanvas).GetField("_extraCarets", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    var list = (List<ExtraCaret>?)field?.GetValue(this);
                    list?.Add(new ExtraCaret
                    {
                        Line = line,
                        Col = col,
                        AbsoluteOffset = (int)lineStart + col
                    });
                }
            }
        }

        [AvaloniaFact]
        public void TestCaretAdjustmentMath()
        {
            var canvas = new TestCanvas();
            var doc = new Document(1, "Line 1\nLine 2\nLine 3".AsMemory());
            canvas.Document = doc;

            // Set main caret: Line 0, Col 2 (offset 2)
            canvas.MoveCaret(0, 2);

            // Add extra carets at Line 1 Col 2 and Line 2 Col 2
            canvas.SetBlockDraggingState(0, 2);

            Assert.Equal(2, canvas.ExtraCarets.Count);
            Assert.Equal(9, canvas.ExtraCarets[0].AbsoluteOffset);
            Assert.Equal(16, canvas.ExtraCarets[1].AbsoluteOffset);

            // Simulate Batch Edit response: insert 'x' at offset 2, offset 9, and offset 16
            var edits = new[]
            {
                new TextEdit { Offset = 2, DeleteLength = 0, Text = "x" },
                new TextEdit { Offset = 9, DeleteLength = 0, Text = "x" },
                new TextEdit { Offset = 16, DeleteLength = 0, Text = "x" }
            };

            canvas.AdjustCaretsForBatch(edits);

            // Main caret: original offset 2, edit at 2 is insert of length 1. New offset = 3.
            Assert.Equal(3, canvas.CaretAbsoluteOffset);

            // Extra caret 1: original offset 9.
            // Edit at 2 <= 9: +1
            // Edit at 9 <= 9: +1
            // Edit at 16 > 9: +0
            // New offset = 9 + 2 = 11.
            Assert.Equal(11, canvas.ExtraCarets[0].AbsoluteOffset);

            // Extra caret 2: original offset 16.
            // All edits <= 16: +3
            // New offset = 16 + 3 = 19.
            Assert.Equal(19, canvas.ExtraCarets[1].AbsoluteOffset);
        }

        [AvaloniaFact]
        public void TestEscapeClearsCarets()
        {
            var canvas = new TestCanvas();
            var doc = new Document(1, "Line 1\nLine 2\nLine 3".AsMemory());
            canvas.Document = doc;

            canvas.MoveCaret(0, 2);
            canvas.SetBlockDraggingState(0, 2);

            Assert.Equal(2, canvas.ExtraCarets.Count);

            // Trigger Escape key
            var keyEventArgs = new KeyEventArgs
            {
                Key = Key.Escape,
                Route = RoutingStrategies.Bubble
            };
            canvas.TriggerKeyDown(keyEventArgs);

            // Extra carets should be cleared
            Assert.Empty(canvas.ExtraCarets);
        }

        [AvaloniaFact]
        public void TestArrowKeysMoveCarets()
        {
            var canvas = new TestCanvas();
            var doc = new Document(1, "Line 1\nLine 2\nLine 3".AsMemory());
            canvas.Document = doc;

            canvas.MoveCaret(0, 2);
            canvas.SetBlockDraggingState(0, 2);

            Assert.Equal(2, canvas.ExtraCarets.Count);
            Assert.Equal(9, canvas.ExtraCarets[0].AbsoluteOffset); // Line 1, Col 2
            Assert.Equal(16, canvas.ExtraCarets[1].AbsoluteOffset); // Line 2, Col 2

            // Move Right
            var keyEventArgs = new KeyEventArgs
            {
                Key = Key.Right,
                Route = RoutingStrategies.Bubble
            };
            canvas.TriggerKeyDown(keyEventArgs);

            // Main caret should move to Col 3 (offset 3)
            Assert.Equal(3, canvas.CaretAbsoluteOffset);
            // Extra caret 1 should move to Col 3 (offset 10)
            Assert.Equal(10, canvas.ExtraCarets[0].AbsoluteOffset);
            // Extra caret 2 should move to Col 3 (offset 17)
            Assert.Equal(17, canvas.ExtraCarets[1].AbsoluteOffset);
        }

        [AvaloniaFact]
        public void TestBatchEditReceivedOnInput()
        {
            var canvas = new TestCanvas();
            var doc = new Document(1, "Line 1\nLine 2\nLine 3".AsMemory());
            canvas.Document = doc;

            canvas.MoveCaret(0, 2);
            canvas.SetBlockDraggingState(0, 2);

            TextEdit[]? receivedEdits = null;
            canvas.BatchEditReceived += (edits) =>
            {
                receivedEdits = edits;
            };

            // Trigger typing "x"
            var textInputEventArgs = new TextInputEventArgs
            {
                Text = "x",
                Route = RoutingStrategies.Bubble
            };
            canvas.TriggerTextInput(textInputEventArgs);

            Assert.NotNull(receivedEdits);
            Assert.Equal(3, receivedEdits.Length);
            Assert.Equal(2, receivedEdits[0].Offset);
            Assert.Equal("x", receivedEdits[0].Text);
            Assert.Equal(9, receivedEdits[1].Offset);
            Assert.Equal("x", receivedEdits[1].Text);
            Assert.Equal(16, receivedEdits[2].Offset);
            Assert.Equal("x", receivedEdits[2].Text);
        }

        [AvaloniaFact]
        public void TestArrowKeysNonWrapping()
        {
            var canvas = new TestCanvas();
            var doc = new Document(1, "Line 1\nLine 2\nLine 3".AsMemory());
            canvas.Document = doc;

            canvas.MoveCaret(0, 0);
            canvas.SetBlockDraggingState(0, 0);

            Assert.Equal(2, canvas.ExtraCarets.Count);
            Assert.Equal(7, canvas.ExtraCarets[0].AbsoluteOffset); // Line 1 Col 0
            Assert.Equal(14, canvas.ExtraCarets[1].AbsoluteOffset); // Line 2 Col 0

            // Trigger Left key
            var keyEventArgs = new KeyEventArgs
            {
                Key = Key.Left,
                Route = RoutingStrategies.Bubble
            };
            canvas.TriggerKeyDown(keyEventArgs);

            // Assert they did not wrap (they should stay at Col 0 on their respective lines)
            Assert.Equal(0, canvas.CaretAbsoluteOffset);
            Assert.Equal(7, canvas.ExtraCarets[0].AbsoluteOffset);
            Assert.Equal(14, canvas.ExtraCarets[1].AbsoluteOffset);
        }

        [AvaloniaFact]
        public void TestCaretDeduplication()
        {
            var canvas = new TestCanvas();
            var doc = new Document(1, "Line 1\nLine 2\nLine 3".AsMemory());
            canvas.Document = doc;

            // Main caret at Line 1 Col 2
            canvas.MoveCaret(1, 2);

            // Extra carets at Line 0 Col 2 and Line 2 Col 2
            canvas.SetBlockDraggingState(0, 2);

            Assert.Equal(2, canvas.ExtraCarets.Count);

            // Trigger Up key
            var keyEventArgs = new KeyEventArgs
            {
                Key = Key.Up,
                Route = RoutingStrategies.Bubble
            };
            canvas.TriggerKeyDown(keyEventArgs);

            // Main caret should move to Line 0 Col 2 (offset 2)
            Assert.Equal(0, canvas.CaretLine);
            Assert.Equal(2, canvas.CaretAbsoluteOffset);

            // Extra carets should be deduplicated (only Line 1 Col 2 remains)
            Assert.Single(canvas.ExtraCarets);
            Assert.Equal(1, canvas.ExtraCarets[0].Line);
            Assert.Equal(2, canvas.ExtraCarets[0].Col);
            Assert.Equal(9, canvas.ExtraCarets[0].AbsoluteOffset);
        }

        [AvaloniaFact]
        public void TestDeleteAndBackspaceKeys()
        {
            var canvas = new TestCanvas();
            var doc = new Document(1, "Line 1\nLine 2\nLine 3".AsMemory());
            canvas.Document = doc;

            canvas.MoveCaret(0, 2);
            canvas.SetBlockDraggingState(0, 2);

            TextEdit[]? receivedEdits = null;
            canvas.BatchEditReceived += (edits) =>
            {
                receivedEdits = edits;
            };

            // Trigger Backspace key
            var backKeyArgs = new KeyEventArgs
            {
                Key = Key.Back,
                Route = RoutingStrategies.Bubble
            };
            canvas.TriggerKeyDown(backKeyArgs);

            Assert.NotNull(receivedEdits);
            Assert.Equal(3, receivedEdits.Length);
            // Main caret Backspace (Line 0 Col 2 -> offset 1)
            Assert.Equal(1, receivedEdits[0].Offset);
            Assert.Equal(1, receivedEdits[0].DeleteLength);
            // Extra caret 1 Backspace (Line 1 Col 2 -> offset 8)
            Assert.Equal(8, receivedEdits[1].Offset);
            Assert.Equal(1, receivedEdits[1].DeleteLength);
            // Extra caret 2 Backspace (Line 2 Col 2 -> offset 15)
            Assert.Equal(15, receivedEdits[2].Offset);
            Assert.Equal(1, receivedEdits[2].DeleteLength);

            // Reset and test Delete key
            receivedEdits = null;
            var delKeyArgs = new KeyEventArgs
            {
                Key = Key.Delete,
                Route = RoutingStrategies.Bubble
            };
            canvas.TriggerKeyDown(delKeyArgs);

            Assert.NotNull(receivedEdits);
            Assert.Equal(3, receivedEdits.Length);
            // Main caret Delete (Line 0 Col 2 -> offset 2)
            Assert.Equal(2, receivedEdits[0].Offset);
            Assert.Equal(1, receivedEdits[0].DeleteLength);
            // Extra caret 1 Delete (Line 1 Col 2 -> offset 9)
            Assert.Equal(9, receivedEdits[1].Offset);
            Assert.Equal(1, receivedEdits[1].DeleteLength);
            // Extra caret 2 Delete (Line 2 Col 2 -> offset 16)
            Assert.Equal(16, receivedEdits[2].Offset);
            Assert.Equal(1, receivedEdits[2].DeleteLength);
        }

        [AvaloniaFact]
        public void TestAutoIndentationOnEnter()
        {
            var canvas = new TestCanvas();
            var doc = new Document(1, "    Indented line\n  Another indent".AsMemory());
            canvas.Document = doc;

            // Move caret to end of first line (offset 17, Col 17)
            canvas.MoveCaret(0, 17);

            string? textReceived = null;
            canvas.TextInputReceived += (offset, text) =>
            {
                textReceived = text;
            };

            // Trigger Enter
            var enterKeyArgs = new KeyEventArgs
            {
                Key = Key.Enter,
                Route = RoutingStrategies.Bubble
            };
            canvas.TriggerKeyDown(enterKeyArgs);

            // Expect newline + 4 spaces of indentation
            Assert.Equal("\n    ", textReceived);
        }
    }
}
