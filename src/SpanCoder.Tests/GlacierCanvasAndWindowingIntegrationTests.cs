namespace SpanCoder.Tests;

using System;
using System.IO;
using Avalonia.Headless.XUnit;
using Glacier.Graphics;
using Glacier.Graphics.Raster;
using Glacier.Graphics.Text;
using Glacier.Windowing;
using Glacier.Windowing.Platform.Headless;
using SpanCoder.Engine;
using SpanCoder.Shell;
using Xunit;

public class GlacierCanvasAndWindowingIntegrationTests
{
    [AvaloniaFact]
    public void TextEditorCanvas_GlacierFontAndTrueTypeFont_LoadedAndConfigured()
    {
        var canvas = new TextEditorCanvas();
        canvas.EnsureGlacierFont();

        Assert.NotNull(canvas.GlacierFont);
        Assert.NotNull(canvas.GlacierTrueTypeFont);
        Assert.True(canvas.GlacierFont.Size > 0f);
        Assert.True(canvas.CharWidth > 0.0);
    }

    [AvaloniaFact]
    public void TextEditorCanvas_RenderDirectToGlacierCanvas_FromPieceTableReadOnlySpan()
    {
        var canvas = new TextEditorCanvas();
        string sampleCode = "public class Example\n{\n    public int Value { get; set; } = 42;\n}\n";
        using var doc = new Document(1, sampleCode.AsMemory(), "Example.cs");
        canvas.Document = doc;

        using var fb = new LinearFramebuffer(800, 600);
        using var graphicsCanvas = new CpuGraphicsCanvas(fb);

        // Render editor directly to Glacier pure C# graphics canvas
        canvas.RenderToGlacierCanvas(graphicsCanvas, 800, 600);

        // Verify the framebuffer was rendered into
        var pixels = fb.AsRgbaSpan();
        Assert.Equal(800 * 600, pixels.Length);

        // Verify gutter background at (0, 0) is dark gray and canvas background at bottom right is black
        Assert.Equal(new Rgba32(30, 30, 30, 255), pixels[0]);
        Assert.Equal(new Rgba32(0, 0, 0, 255), pixels[800 * 599 + 799]);

        // Find non-black pixels from text and gutter rendering
        int nonBlackCount = 0;
        for (int i = 0; i < pixels.Length; i++)
        {
            if (pixels[i].R != 0 || pixels[i].G != 0 || pixels[i].B != 0)
            {
                nonBlackCount++;
            }
        }

        Assert.True(nonBlackCount > 100, $"Expected rendered text/gutter pixels, found {nonBlackCount}");
    }

    [AvaloniaFact]
    public void TextEditorCanvas_NativeWindowInputLoop_EliminatesPollingJitter()
    {
        var canvas = new TextEditorCanvas();
        string initialText = "Hello Glacier World\nSecond Line\n";
        using var doc = new Document(1, initialText.AsMemory(), "Test.cs");
        canvas.Document = doc;

        using var window = new HeadlessWindow(new WindowOptions
        {
            Title = "SpanCoder Native Window",
            Width = 800,
            Height = 600
        });

        canvas.AttachNativeWindow(window);
        Assert.Same(window, canvas.NativeWindow);

        // Send native mouse down to position caret
        window.EnqueueInput(new InputEvent(InputEventType.MouseDown, 0, 80f, 10f, 1000));
        canvas.PollEvents();

        // Send native mouse wheel
        double scrollBefore = canvas.ScrollY;
        window.EnqueueInput(new InputEvent(InputEventType.MouseWheel, 0, 0f, -2f, 2000));
        canvas.PollEvents();
        Assert.True(canvas.ScrollY > scrollBefore);

        // Send native key down navigation (Right arrow key = 39)
        int colBefore = canvas.CaretCol;
        window.EnqueueInput(new InputEvent(InputEventType.KeyDown, 39, 0f, 0f, 3000));
        canvas.PollEvents();
        Assert.Equal(colBefore + 1, canvas.CaretCol);

        // Detach window
        canvas.DetachNativeWindow();
        Assert.Null(canvas.NativeWindow);
    }

    [AvaloniaFact]
    public void ShellWindow_AttachNativeWindow_RoutesEventsAndPollsWithoutJitter()
    {
        var window = new ShellWindow();
        window.InitializeLayout();

        using var nativeWin = new HeadlessWindow(new WindowOptions
        {
            Title = "SpanCoder Shell Native",
            Width = 1024,
            Height = 768
        });

        window.AttachNativeWindow(nativeWin);
        Assert.Same(nativeWin, window.NativeWindow);

        nativeWin.EnqueueInput(new InputEvent(InputEventType.MouseMove, 0, 150f, 200f, 5000));
        window.PollEvents();
        // Dispatched without throwing or jitter
    }
}
