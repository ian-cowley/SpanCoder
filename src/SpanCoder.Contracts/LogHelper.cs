using System;
using System.IO;

namespace SpanCoder.Contracts
{
    public static class LogHelper
    {
        private static readonly string LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "spancoder.log");
        private static readonly string CurrentDirLogPath = Path.Combine(Environment.CurrentDirectory, "spancoder.log");
        private static readonly object LockObj = new();

        public static bool Enabled { get; set; } = Environment.GetEnvironmentVariable("SPANCODER_DEBUG") == "1";

        static LogHelper()
        {
            if (!Enabled) return;

            // Clear old logs at startup
            try
            {
                if (File.Exists(LogPath)) File.Delete(LogPath);
                if (File.Exists(CurrentDirLogPath)) File.Delete(CurrentDirLogPath);
            }
            catch { }

            Log($"[LogHelper] Logger initialized.");
            Log($"[LogHelper] Writing base log to: {LogPath}");
            Log($"[LogHelper] Writing working dir log to: {CurrentDirLogPath}");
        }

        public static void Log(string message)
        {
            if (!Enabled) return;

            lock (LockObj)
            {
                try
                {
                    string logLine = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
                    File.AppendAllText(LogPath, logLine);
                    
                    if (CurrentDirLogPath != LogPath)
                    {
                        File.AppendAllText(CurrentDirLogPath, logLine);
                    }
                }
                catch { }
            }
            Console.WriteLine(message);
        }
    }
}
