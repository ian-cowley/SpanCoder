using System;
using System.IO;
using System.Linq;
using Xunit;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using SpanCoder.Contracts;
using SpanCoder.Engine;
using SpanCoder.Shell;

namespace SpanCoder.Tests
{
    public class FoldingTests
    {
        [AvaloniaFact]
        public void TestLocalScannerBracesAndRegions()
        {
            var code = @"using System;
#region MyRegion
class Test
{
    void Method()
    {
        // comment with { braces }
        string s = ""string { with braces }"";
    }
}
#endregion";

            var canvas = new TextEditorCanvas();
            var doc = new Document(1, code.AsMemory());
            doc.FilePath = "test.cs";
            canvas.Document = doc; // This triggers RecomputeFoldingRanges()

            var ranges = canvas.FoldingRanges;

            // We expect 3 folding ranges:
            // 1. #region / #endregion (line 1 to 10)
            // 2. class braces (line 3 to 9)
            // 3. void Method() braces (line 5 to 8)
            // The braces inside comments and strings must be ignored!

            Assert.Equal(3, ranges.Count);

            // Region range (1 to 10)
            var regionRange = ranges.FirstOrDefault(r => r.StartLine == 1);
            Assert.NotNull(regionRange);
            Assert.Equal(10, regionRange.EndLine);

            // Class range (3 to 9)
            var classRange = ranges.FirstOrDefault(r => r.StartLine == 3);
            Assert.NotNull(classRange);
            Assert.Equal(9, classRange.EndLine);

            // Method range (5 to 8)
            var methodRange = ranges.FirstOrDefault(r => r.StartLine == 5);
            Assert.NotNull(methodRange);
            Assert.Equal(8, methodRange.EndLine);
        }

        [AvaloniaFact]
        public void TestFoldingLayoutMapping()
        {
            var code = @"line 0
line 1 {
line 2
line 3
}
line 5";
            var canvas = new TextEditorCanvas();
            var doc = new Document(1, code.AsMemory());
            doc.FilePath = "test.cs";
            canvas.Document = doc;

            // Ensure the range is found
            Assert.Single(canvas.FoldingRanges);
            var range = canvas.FoldingRanges[0];
            Assert.Equal(1, range.StartLine);
            Assert.Equal(4, range.EndLine);

            // Initially nothing is folded
            Assert.Equal(6, canvas.GetVisibleLineCount());
            for (int i = 0; i < 6; i++)
            {
                Assert.Equal(i, canvas.VisibleLines[i]);
                Assert.Equal(i, canvas.DocToVisualLineMap[i]);
            }

            // Now fold the range (hiding lines 2, 3, 4)
            range.IsFolded = true;
            canvas.UpdateFoldingLayout();

            // Visible lines should be: 0, 1, 5 (total 3 lines)
            Assert.Equal(3, canvas.GetVisibleLineCount());
            Assert.Equal(0, canvas.VisibleLines[0]);
            Assert.Equal(1, canvas.VisibleLines[1]);
            Assert.Equal(5, canvas.VisibleLines[2]);

            // Mapping:
            // Line 0 -> Visual 0
            // Line 1 -> Visual 1
            // Line 2 (hidden) -> maps to visual line index of header (Line 1) -> Visual 1
            // Line 3 (hidden) -> Visual 1
            // Line 4 (hidden) -> Visual 1
            // Line 5 -> Visual 2
            Assert.Equal(0, canvas.DocToVisualLineMap[0]);
            Assert.Equal(1, canvas.DocToVisualLineMap[1]);
            Assert.Equal(1, canvas.DocToVisualLineMap[2]);
            Assert.Equal(1, canvas.DocToVisualLineMap[3]);
            Assert.Equal(1, canvas.DocToVisualLineMap[4]);
            Assert.Equal(2, canvas.DocToVisualLineMap[5]);
        }

        [AvaloniaFact]
        public void TestCaretNavigationSkipsFoldedLines()
        {
            var code = @"line 0
line 1 {
line 2
line 3
}
line 5";
            var canvas = new TextEditorCanvas();
            var doc = new Document(1, code.AsMemory());
            doc.FilePath = "test.cs";
            canvas.Document = doc;

            // Fold the range
            canvas.FoldingRanges[0].IsFolded = true;
            canvas.UpdateFoldingLayout();

            // Get private navigation methods
            var bindingFlags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var getNewCaretPosDown = typeof(TextEditorCanvas).GetMethod("GetNewCaretPosDown", bindingFlags);
            var getNewCaretPosUp = typeof(TextEditorCanvas).GetMethod("GetNewCaretPosUp", bindingFlags);
            
            Assert.NotNull(getNewCaretPosDown);
            Assert.NotNull(getNewCaretPosUp);

            // 1. Navigating DOWN from Line 1 should skip folded lines 2, 3, 4 and go straight to Line 5
            var downResult = ((int Line, int Col))getNewCaretPosDown.Invoke(canvas, new object[] { 1, 0 })!;
            Assert.Equal(5, downResult.Line);

            // 2. Navigating UP from Line 5 should skip folded lines 4, 3, 2 and go straight to Line 1
            var upResult = ((int Line, int Col))getNewCaretPosUp.Invoke(canvas, new object[] { 5, 0 })!;
            Assert.Equal(1, upResult.Line);
        }

        [Fact]
        public void TestFoldingMessageSerialization()
        {
            var originalItems = new[]
            {
                new FoldingRangeItem { StartLine = 10, EndLine = 20 },
                new FoldingRangeItem { StartLine = 35, EndLine = 45 }
            };

            byte[] buffer = new byte[1024];

            // Serialize Request
            int reqLen = BinaryMessageSerializer.WriteFoldingRangeRequest(buffer, 42);
            Assert.True(BinaryMessageSerializer.TryParseHeader(buffer.AsSpan(0, reqLen), out var reqHeader));
            Assert.Equal(MessageTypes.FoldingRangeRequest, reqHeader.Type);
            Assert.Equal(42, reqHeader.DocumentId);

            // Serialize Response
            int respLen = BinaryMessageSerializer.WriteFoldingRangeResponse(buffer, 42, originalItems);
            Assert.True(BinaryMessageSerializer.TryParseHeader(buffer.AsSpan(0, respLen), out var respHeader));
            Assert.Equal(MessageTypes.FoldingRangeResponse, respHeader.Type);
            Assert.Equal(42, respHeader.DocumentId);

            // Deserialize Response
            var deserializedItems = BinaryMessageSerializer.ParseFoldingRangeResponse(buffer.AsSpan(0, respLen), out int docId);
            Assert.Equal(42, docId);
            Assert.Equal(originalItems.Length, deserializedItems.Count);
            
            Assert.Equal(10, deserializedItems[0].StartLine);
            Assert.Equal(20, deserializedItems[0].EndLine);
            Assert.Equal(35, deserializedItems[1].StartLine);
            Assert.Equal(45, deserializedItems[1].EndLine);
        }
    }
}
