namespace SpanCoder.Contracts;

using System;
using System.Runtime.CompilerServices;

/// <summary>
/// Ambient static diagnostic logger dispatcher for Glacier.SpanCoder.
/// Dispatches log messages to the configured ambient <see cref="IGlacierLogger"/>.
/// </summary>
public static class SpanCoderDiagnostics
{
    private static volatile IGlacierLogger _logger = NullGlacierLogger.Instance;

    /// <summary>
    /// Gets or sets the ambient logger. Defaults to <see cref="NullGlacierLogger.Instance"/>.
    /// </summary>
    public static IGlacierLogger Logger
    {
        get => _logger;
        set => _logger = value ?? NullGlacierLogger.Instance;
    }

    /// <summary>
    /// Configures the ambient logger.
    /// </summary>
    public static void SetLogger(IGlacierLogger? logger) => _logger = logger ?? NullGlacierLogger.Instance;

    /// <summary>
    /// Resets the ambient logger back to the default <see cref="NullGlacierLogger.Instance"/>.
    /// </summary>
    public static void Reset() => _logger = NullGlacierLogger.Instance;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsEnabled(LogLevel level) => _logger.IsEnabled(level);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogTrace(string message)
    {
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.Log(LogLevel.Trace, message);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogDebug(string message)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.Log(LogLevel.Debug, message);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogInformation(string message)
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.Log(LogLevel.Information, message);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogWarning(string message, Exception? exception = null)
    {
        if (_logger.IsEnabled(LogLevel.Warning))
        {
            _logger.Log(LogLevel.Warning, message, exception);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogError(string message, Exception? exception = null)
    {
        if (_logger.IsEnabled(LogLevel.Error))
        {
            _logger.Log(LogLevel.Error, message, exception);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogCritical(string message, Exception? exception = null)
    {
        if (_logger.IsEnabled(LogLevel.Critical))
        {
            _logger.Log(LogLevel.Critical, message, exception);
        }
    }
}

/// <summary>
/// Ecosystem-standard alias for <see cref="SpanCoderDiagnostics"/>.
/// </summary>
public static class GlacierDiagnostics
{
    public static IGlacierLogger Logger
    {
        get => SpanCoderDiagnostics.Logger;
        set => SpanCoderDiagnostics.Logger = value;
    }

    public static void SetLogger(IGlacierLogger? logger) => SpanCoderDiagnostics.SetLogger(logger);

    public static void Reset() => SpanCoderDiagnostics.Reset();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsEnabled(LogLevel level) => SpanCoderDiagnostics.IsEnabled(level);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogTrace(string message) => SpanCoderDiagnostics.LogTrace(message);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogDebug(string message) => SpanCoderDiagnostics.LogDebug(message);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogInformation(string message) => SpanCoderDiagnostics.LogInformation(message);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogWarning(string message, Exception? exception = null) => SpanCoderDiagnostics.LogWarning(message, exception);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogError(string message, Exception? exception = null) => SpanCoderDiagnostics.LogError(message, exception);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogCritical(string message, Exception? exception = null) => SpanCoderDiagnostics.LogCritical(message, exception);
}
