using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace SpanCoder.Shell
{
    public class PerformanceGraphsControl : Control
    {
        private readonly Queue<double> _cpuHistory = new();
        private readonly Queue<double> _memHistory = new();
        private readonly DispatcherTimer _timer;
        private TimeSpan _lastCpuTime = TimeSpan.Zero;
        private DateTime _lastTime = DateTime.MinValue;
        private readonly int _maxPoints = 60;

        private double _currentCpu = 0.0;
        private double _currentMem = 0.0;

        private readonly Typeface _typeface = new("Consolas");

        public PerformanceGraphsControl()
        {
            ClipToBounds = true;
            
            // Initialize history with 0s
            for (int i = 0; i < _maxPoints; i++)
            {
                _cpuHistory.Enqueue(0.0);
                _memHistory.Enqueue(0.0);
            }

            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _timer.Tick += Timer_Tick;
            _timer.Start();

            // Set initial CPU times
            try
            {
                var proc = Process.GetCurrentProcess();
                _lastCpuTime = proc.TotalProcessorTime;
                _lastTime = DateTime.UtcNow;
            }
            catch
            {
                _lastCpuTime = TimeSpan.Zero;
                _lastTime = DateTime.UtcNow;
            }
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            try
            {
                var proc = Process.GetCurrentProcess();
                var now = DateTime.UtcNow;
                var cpuTime = proc.TotalProcessorTime;

                var timeDelta = now - _lastTime;
                var cpuDelta = cpuTime - _lastCpuTime;

                _lastTime = now;
                _lastCpuTime = cpuTime;

                if (timeDelta.TotalMilliseconds > 0)
                {
                    double cpuPercent = (cpuDelta.TotalMilliseconds / timeDelta.TotalMilliseconds) / Environment.ProcessorCount * 100.0;
                    _currentCpu = Math.Min(100.0, Math.Max(0.0, cpuPercent));
                }

                // Working set memory in MB
                _currentMem = proc.WorkingSet64 / (1024.0 * 1024.0);

                _cpuHistory.Dequeue();
                _cpuHistory.Enqueue(_currentCpu);

                _memHistory.Dequeue();
                _memHistory.Enqueue(_currentMem);

                InvalidateVisual();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PerformanceGraphs] Error getting diagnostics: {ex.Message}");
            }
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);

            double w = Bounds.Width;
            double h = Bounds.Height;

            if (w < 100 || h < 50) return;

            // Background
            context.FillRectangle(new SolidColorBrush(Color.Parse("#1A1A1A")), Bounds);

            // Split into two halves: Left for CPU, Right for Memory
            double chartW = (w - 48) / 2.0;
            double chartH = h - 40;

            DrawChart(context, _cpuHistory, "CPU Usage", $"{_currentCpu:F1}%", 0, 100.0, new Rect(16, 24, chartW, chartH), Color.Parse("#00B0FF"), Color.Parse("#80E27E"));
            DrawChart(context, _memHistory, "Memory Footprint", $"{_currentMem:F0} MB", 16 + chartW + 16, Math.Max(512.0, _memHistory.Max() * 1.2), new Rect(16 + chartW + 16, 24, chartW, chartH), Color.Parse("#00E676"), Color.Parse("#29B6F6"));
        }

        private void DrawChart(DrawingContext context, Queue<double> history, string title, string currentText, double xOffset, double maxVal, Rect rect, Color primaryColor, Color secondaryColor)
        {
            if (rect.Width <= 0 || rect.Height <= 0) return;

            // Draw Title & Value
            var titleText = new FormattedText(
                $"{title}: {currentText}",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                _typeface,
                12.0,
                Brushes.White
            );
            context.DrawText(titleText, new Point(rect.X, 4));

            // Draw chart border
            context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.Parse("#2D2D2D")), 1.0), rect);

            // Draw gridlines (horizontal)
            var gridPen = new Pen(new SolidColorBrush(Color.Parse("#222222")), 1.0);
            for (int i = 1; i <= 4; i++)
            {
                double y = rect.Y + (rect.Height * i / 5.0);
                context.DrawLine(gridPen, new Point(rect.X, y), new Point(rect.X + rect.Width, y));
            }

            // Draw historical data
            var points = history.ToArray();
            var lineGeometry = new StreamGeometry();
            var areaGeometry = new StreamGeometry();

            using (var lineCtx = lineGeometry.Open())
            using (var areaCtx = areaGeometry.Open())
            {
                double stepX = rect.Width / (_maxPoints - 1);
                
                double getX(int index) => rect.X + (index * stepX);
                double getY(double val) => rect.Y + rect.Height - ((val / maxVal) * rect.Height);

                var startPt = new Point(getX(0), getY(points[0]));
                lineCtx.BeginFigure(startPt, false);
                areaCtx.BeginFigure(new Point(rect.X, rect.Y + rect.Height), true);
                areaCtx.LineTo(startPt);

                for (int i = 1; i < points.Length; i++)
                {
                    var pt = new Point(getX(i), getY(points[i]));
                    lineCtx.LineTo(pt);
                    areaCtx.LineTo(pt);
                }

                areaCtx.LineTo(new Point(getX(points.Length - 1), rect.Y + rect.Height));
            }

            // Fill Area with beautiful soft gradient
            var areaBrush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(80, primaryColor.R, primaryColor.G, primaryColor.B), 0.0),
                    new GradientStop(Color.FromArgb(10, primaryColor.R, primaryColor.G, primaryColor.B), 1.0)
                }
            };

            context.DrawGeometry(areaBrush, null, areaGeometry);

            // Draw Line with glowing neon outline
            var linePen = new Pen(new SolidColorBrush(primaryColor), 1.5);
            context.DrawGeometry(null, linePen, lineGeometry);
        }
    }
}
