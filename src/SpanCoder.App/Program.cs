using System;
using System.Diagnostics;
using Avalonia;

namespace SpanCoder.App
{
    internal class Program
    {
        private static Stopwatch? _startupStopwatch;

        // Initialization code. Don't use any Avalonia, third-party APIs or any
        // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
        // yet and stuff might break.
        [STAThread]
        public static void Main(string[] args)
        {
            _startupStopwatch = Stopwatch.StartNew();
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        // AppBuilder configure to boot App.cs
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure(() => new App(_startupStopwatch))
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();
    }
}
