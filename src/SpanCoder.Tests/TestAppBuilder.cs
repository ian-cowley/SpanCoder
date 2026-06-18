using Avalonia;
using Avalonia.Headless;
using SpanCoder.App;

[assembly: AvaloniaTestApplication(typeof(SpanCoder.Tests.TestAppBuilder))]

namespace SpanCoder.Tests
{
    public static class TestAppBuilder
    {
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<SpanCoder.App.App>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions
                {
                    UseHeadlessDrawing = true
                });
    }
}
