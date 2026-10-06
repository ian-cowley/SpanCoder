using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SpanCoder.Contracts;
using Xunit;

namespace SpanCoder.Tests
{
    [Collection("DiagnosticsTests")]
    public class ChallengerSpanCoderStressTests : IDisposable
    {
        private readonly IGlacierLogger _originalLogger;

        public ChallengerSpanCoderStressTests()
        {
            _originalLogger = SpanCoderDiagnostics.Logger;
        }

        public void Dispose()
        {
            SpanCoderDiagnostics.Logger = _originalLogger;
        }

        [Fact]
        public async Task StressTest_ConcurrentMultiThreadedWrites_ThreadSafeAndZeroExceptions()
        {
            int totalLogged = 0;
            var testLogger = new DelegateGlacierLogger((lvl, msg, ex) =>
            {
                if (msg.StartsWith("StressThread_"))
                {
                    Interlocked.Increment(ref totalLogged);
                }
            }, LogLevel.Trace);

            SpanCoderDiagnostics.Logger = testLogger;

            const int threadCount = 16;
            const int logsPerThread = 1000;
            int totalExpected = threadCount * logsPerThread;

            var tasks = Enumerable.Range(0, threadCount).Select(threadId => Task.Run(() =>
            {
                for (int i = 0; i < logsPerThread; i++)
                {
                    switch (i % 6)
                    {
                        case 0:
                            SpanCoderDiagnostics.LogTrace($"StressThread_{threadId} trace {i}");
                            break;
                        case 1:
                            SpanCoderDiagnostics.LogDebug($"StressThread_{threadId} debug {i}");
                            break;
                        case 2:
                            SpanCoderDiagnostics.LogInformation($"StressThread_{threadId} info {i}");
                            break;
                        case 3:
                            SpanCoderDiagnostics.LogWarning($"StressThread_{threadId} warn {i}");
                            break;
                        case 4:
                            SpanCoderDiagnostics.LogError($"StressThread_{threadId} error {i}", new InvalidOperationException("test"));
                            break;
                        case 5:
                            SpanCoderDiagnostics.LogCritical($"StressThread_{threadId} critical {i}");
                            break;
                    }
                }
            })).ToArray();

            await Task.WhenAll(tasks);

            Assert.Equal(totalExpected, Volatile.Read(ref totalLogged));
        }

        [Fact]
        public async Task StressTest_ConcurrentLoggerSwapping_NoDeadlocksOrNullRefExceptions()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var token = cts.Token;

            int writeCount = 0;
            int swapCount = 0;
            Exception? caughtException = null;

            var writerLogger1 = new DelegateGlacierLogger((lvl, msg, ex) => Interlocked.Increment(ref writeCount), LogLevel.Trace);
            var writerLogger2 = new DelegateGlacierLogger((lvl, msg, ex) => Interlocked.Increment(ref writeCount), LogLevel.Trace);

            // 8 writer threads logging at maximum speed
            var writerTasks = Enumerable.Range(0, 8).Select(id => Task.Run(() =>
            {
                int counter = 0;
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        SpanCoderDiagnostics.LogInformation($"Concurrent write {id}-{counter++}");
                        SpanCoderDiagnostics.LogWarning($"Warning write {id}-{counter}");
                        SpanCoderDiagnostics.LogError($"Error write {id}-{counter}", new Exception("synthetic"));
                    }
                    catch (Exception ex)
                    {
                        Volatile.Write(ref caughtException, ex);
                        break;
                    }
                }
            })).ToArray();

            // 2 swapper threads constantly swapping the ambient logger
            var swapperTasks = Enumerable.Range(0, 2).Select(id => Task.Run(() =>
            {
                int swapIter = 0;
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        switch (swapIter++ % 5)
                        {
                            case 0:
                                SpanCoderDiagnostics.SetLogger(writerLogger1);
                                break;
                            case 1:
                                SpanCoderDiagnostics.SetLogger(writerLogger2);
                                break;
                            case 2:
                                SpanCoderDiagnostics.SetLogger(null); // Must safely default to NullGlacierLogger
                                break;
                            case 3:
                                SpanCoderDiagnostics.Reset();
                                break;
                            case 4:
                                GlacierDiagnostics.SetLogger(writerLogger1);
                                break;
                        }
                        Interlocked.Increment(ref swapCount);
                        Thread.Sleep(1);
                    }
                    catch (Exception ex)
                    {
                        Volatile.Write(ref caughtException, ex);
                        break;
                    }
                }
            })).ToArray();

            await Task.Delay(1500);
            cts.Cancel();
            await Task.WhenAll(writerTasks.Concat(swapperTasks));

            Assert.Null(caughtException);
            Assert.True(writeCount > 100);
            Assert.True(swapCount > 20);
        }

        [Fact]
        public void StressTest_NullLoggerSafety_NeverThrowsOnNullAssignmentsOrInvocations()
        {
            // SetLogger(null)
            SpanCoderDiagnostics.SetLogger(null);
            Assert.NotNull(SpanCoderDiagnostics.Logger);
            Assert.Same(NullGlacierLogger.Instance, SpanCoderDiagnostics.Logger);

            // Direct property setter with null
            SpanCoderDiagnostics.Logger = null!;
            Assert.NotNull(SpanCoderDiagnostics.Logger);
            Assert.Same(NullGlacierLogger.Instance, SpanCoderDiagnostics.Logger);

            // Alias property setter with null
            GlacierDiagnostics.Logger = null!;
            Assert.NotNull(GlacierDiagnostics.Logger);
            Assert.Same(NullGlacierLogger.Instance, GlacierDiagnostics.Logger);

            // Logging when null logger is active should execute safely with zero exceptions
            SpanCoderDiagnostics.LogTrace("null test trace");
            SpanCoderDiagnostics.LogDebug("null test debug");
            SpanCoderDiagnostics.LogInformation("null test info");
            SpanCoderDiagnostics.LogWarning("null test warn", new Exception());
            SpanCoderDiagnostics.LogError("null test error", new Exception());
            SpanCoderDiagnostics.LogCritical("null test crit", new Exception());

            GlacierDiagnostics.LogTrace("alias null trace");
            GlacierDiagnostics.LogDebug("alias null debug");
            GlacierDiagnostics.LogInformation("alias null info");
            GlacierDiagnostics.LogWarning("alias null warn", new Exception());
            GlacierDiagnostics.LogError("alias null error", new Exception());
            GlacierDiagnostics.LogCritical("alias null crit", new Exception());
        }

        [Fact]
        public async Task StressTest_ConsoleGlacierLogger_ThreadSafetyUnderRedirectedStreams()
        {
            using var swOut = new StringWriter();
            using var swErr = new StringWriter();
            var prevOut = Console.Out;
            var prevErr = Console.Error;

            try
            {
                Console.SetOut(TextWriter.Synchronized(swOut));
                Console.SetError(TextWriter.Synchronized(swErr));

                var loggerWithColors = new ConsoleGlacierLogger(LogLevel.Trace, useColors: true);
                var loggerNoColors = new ConsoleGlacierLogger(LogLevel.Trace, useColors: false);

                // Multi-threaded writes across both color and non-color modes
                const int threadCount = 8;
                const int iterations = 100;

                var tasks = Enumerable.Range(0, threadCount).Select(t => Task.Run(() =>
                {
                    var logger = (t % 2 == 0) ? loggerWithColors : loggerNoColors;
                    for (int i = 0; i < iterations; i++)
                    {
                        logger.Log(LogLevel.Information, $"Thread {t} test message {i}");
                        if (i % 10 == 0)
                        {
                            logger.Log(LogLevel.Error, $"Thread {t} error {i}", new InvalidOperationException("synthetic"));
                        }
                    }
                })).ToArray();

                await Task.WhenAll(tasks);

                string outText = swOut.ToString();
                string errText = swErr.ErrorText();

                Assert.True(outText.Length > 0);
                Assert.Contains("[INFO]", outText);
                Assert.Contains("[ERROR]", errText);
            }
            finally
            {
                Console.SetOut(prevOut);
                Console.SetError(prevErr);
            }
        }
    }

    internal static class StringWriterExtensions
    {
        public static string ErrorText(this StringWriter sw) => sw.ToString();
    }
}
