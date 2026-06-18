using Avalonia.Headless.XUnit;
using System;
using Xunit;
using SpanCoder.Shell;
using SpanCoder.Engine;

namespace SpanCoder.Tests;

public class MultiCaretTests
{
    [AvaloniaFact]
    public void TestAddAndToggleExtraCarets()
    {
        var canvas = new TextEditorCanvas();
        var doc = new Document(1, "Hello World\nLine 2\nLine 3".AsMemory());
        canvas.Document = doc;

        // Set primary caret
        canvas.MoveCaretToOffset(5); // end of "Hello"
        Assert.Equal(5, canvas.GetCaretAbsoluteOffset());

        // Add extra carets
        canvas.AddExtraCaretForTest(12); // start of "Line 2"
        canvas.AddExtraCaretForTest(20); // start of "Line 3"

        Assert.Equal(2, canvas.ExtraCarets.Count);
        Assert.Equal(12, canvas.ExtraCarets[0].AbsoluteOffset);
        Assert.Equal(20, canvas.ExtraCarets[1].AbsoluteOffset);
    }

    [AvaloniaFact]
    public void TestGetCaretOffsetsDescending()
    {
        var canvas = new TextEditorCanvas();
        var doc = new Document(1, "Hello World\nLine 2\nLine 3".AsMemory());
        canvas.Document = doc;

        canvas.MoveCaretToOffset(5); // Primary caret
        canvas.AddExtraCaretForTest(20);
        canvas.AddExtraCaretForTest(12);

        var descendingOffsets = canvas.GetCaretOffsetsDescending();
        Assert.Equal(3, descendingOffsets.Count);
        Assert.Equal(20, descendingOffsets[0]);
        Assert.Equal(12, descendingOffsets[1]);
        Assert.Equal(5, descendingOffsets[2]);
    }

    [AvaloniaFact]
    public void TestDeduplicateCarets()
    {
        var canvas = new TextEditorCanvas();
        var doc = new Document(1, "Hello World\nLine 2\nLine 3".AsMemory());
        canvas.Document = doc;

        canvas.MoveCaretToOffset(5);
        canvas.AddExtraCaretForTest(5); // Matches primary
        canvas.AddExtraCaretForTest(12);
        canvas.AddExtraCaretForTest(12); // Duplicate extra

        // AdjustCaret triggers DeduplicateCarets
        canvas.AdjustCaret(0, 0, 0);

        Assert.Single(canvas.ExtraCarets);
        Assert.Equal(12, canvas.ExtraCarets[0].AbsoluteOffset);
        Assert.Equal(5, canvas.GetCaretAbsoluteOffset());
    }

    [AvaloniaFact]
    public void TestAdjustCaretOnEdit()
    {
        var canvas = new TextEditorCanvas();
        var doc = new Document(1, "Hello World\nLine 2\nLine 3".AsMemory());
        canvas.Document = doc;

        // Caret positions:
        // Primary at 2 ("He|llo")
        // Extra 1 at 8 ("Hello Wo|rld")
        // Extra 2 at 15 ("Line |2")
        canvas.MoveCaretToOffset(2);
        canvas.AddExtraCaretForTest(8);
        canvas.AddExtraCaretForTest(15);

        // Edit: Insert 3 characters at offset 5 (after primary, before extra 1 and 2)
        doc.Insert(5, "ABC");
        canvas.AdjustCaret(5, 3, 0);

        // Assert primary did not shift (2 < 5)
        Assert.Equal(2, canvas.GetCaretAbsoluteOffset());

        // Assert extra 1 shifted (8 + 3 = 11)
        Assert.Equal(11, canvas.ExtraCarets[0].AbsoluteOffset);

        // Assert extra 2 shifted (15 + 3 = 18)
        Assert.Equal(18, canvas.ExtraCarets[1].AbsoluteOffset);

        // Edit: Delete 4 characters at offset 12 (which is before extra 2, but after primary and extra 1)
        doc.Delete(12, 4);
        canvas.AdjustCaret(12, 0, 4);

        // Primary: 2 (no change)
        Assert.Equal(2, canvas.GetCaretAbsoluteOffset());

        // Extra 1: 11 (no change)
        Assert.Equal(11, canvas.ExtraCarets[0].AbsoluteOffset);

        // Extra 2: 18 -> shifted by 4 since delete was at 12 (18 - 4 = 14)
        Assert.Equal(14, canvas.ExtraCarets[1].AbsoluteOffset);
    }
}
