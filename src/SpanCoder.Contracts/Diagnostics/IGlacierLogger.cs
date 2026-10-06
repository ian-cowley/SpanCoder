namespace SpanCoder.Contracts;

using System;

/// <summary>
/// Pluggable logging abstraction for Glacier.SpanCoder diagnostics.
/// Hosts (CLI, tests, servers) implement this to control or redirect diagnostic output.
/// </summary>
public interface IGlacierLogger
{
    /// <summary>
    /// Checks if the given log level is enabled.
    /// </summary>
    bool IsEnabled(LogLevel level);

    /// <summary>
    /// Writes a diagnostic log entry.
    /// </summary>
    void Log(LogLevel level, string message, Exception? exception = null);
}
