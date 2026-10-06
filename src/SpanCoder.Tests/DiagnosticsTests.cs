using System;
using System.Collections.Generic;
using SpanCoder.Contracts;
using Xunit;

namespace SpanCoder.Tests
{
    [Collection("DiagnosticsTests")]
    public class DiagnosticsTests
    {
        [Fact]
        public void DelegateGlacierLogger_CapturesLogsAtOrAboveMinimumLevel()
        {
            var logged = new List<(LogLevel Level, string Message, Exception? Exception)>();
            var logger = new DelegateGlacierLogger((level, msg, ex) => logged.Add((level, msg, ex)), LogLevel.Warning);

            logger.Log(LogLevel.Debug, "debug message");
            logger.Log(LogLevel.Information, "info message");
            logger.Log(LogLevel.Warning, "warning message");
            logger.Log(LogLevel.Error, "error message", new InvalidOperationException("err"));

            Assert.Equal(2, logged.Count);
            Assert.Equal(LogLevel.Warning, logged[0].Level);
            Assert.Equal("warning message", logged[0].Message);
            Assert.Null(logged[0].Exception);

            Assert.Equal(LogLevel.Error, logged[1].Level);
            Assert.Equal("error message", logged[1].Message);
            Assert.NotNull(logged[1].Exception);
        }

        [Fact]
        public void NullGlacierLogger_DoesNotThrowAndIsNeverEnabled()
        {
            var logger = NullGlacierLogger.Instance;
            Assert.False(logger.IsEnabled(LogLevel.Error));
            Assert.False(logger.IsEnabled(LogLevel.Critical));

            // Should not throw
            logger.Log(LogLevel.Error, "error message");
        }

        [Fact]
        public void ConsoleGlacierLogger_LevelFilteringWorks()
        {
            var logger = new ConsoleGlacierLogger(LogLevel.Information);
            Assert.False(logger.IsEnabled(LogLevel.Trace));
            Assert.False(logger.IsEnabled(LogLevel.Debug));
            Assert.True(logger.IsEnabled(LogLevel.Information));
            Assert.True(logger.IsEnabled(LogLevel.Warning));
            Assert.True(logger.IsEnabled(LogLevel.Error));
            Assert.True(logger.IsEnabled(LogLevel.Critical));
        }

        [Fact]
        public void SpanCoderDiagnostics_AmbientLogger_DispatchesCorrectly()
        {
            var original = SpanCoderDiagnostics.Logger;
            try
            {
                var messages = new List<string>();
                SpanCoderDiagnostics.Logger = new DelegateGlacierLogger((lvl, msg, ex) => messages.Add($"{lvl}: {msg}"), LogLevel.Information);

                SpanCoderDiagnostics.LogDebug("This should be filtered");
                SpanCoderDiagnostics.LogInformation("System initialized");
                SpanCoderDiagnostics.LogWarning("System high memory");
                SpanCoderDiagnostics.LogError("Operation failed");

                Assert.Equal(3, messages.Count);
                Assert.Equal("Information: System initialized", messages[0]);
                Assert.Equal("Warning: System high memory", messages[1]);
                Assert.Equal("Error: Operation failed", messages[2]);
            }
            finally
            {
                SpanCoderDiagnostics.Logger = original;
            }
        }

        [Fact]
        public void SpanCoderDiagnostics_GlacierDiagnosticsAlias_ReflectsSameLogger()
        {
            var original = SpanCoderDiagnostics.Logger;
            try
            {
                var testLogger = NullGlacierLogger.Instance;
                GlacierDiagnostics.Logger = testLogger;
                Assert.Same(testLogger, SpanCoderDiagnostics.Logger);
            }
            finally
            {
                SpanCoderDiagnostics.Logger = original;
            }
        }
    }
}
