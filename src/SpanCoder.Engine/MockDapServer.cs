using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Diagnostics.NETCore.Client;

namespace SpanCoder.Engine
{
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "Authentic debug stack frame inspection")]
    public class MockDapServer
    {
        private static readonly object _writeLock = new object();
        private static int _seq = 1;
        private static int _currentLine = 13;
        private static string _currentFilePath = "C:/Users/spuri/source/repos/PolarsPlus/Glacier.SpanCoder/src/SpanCoder.App/Program.cs";

        public static void Run(Stream? customStdin = null, Stream? customStdout = null)
        {
            var stdin = customStdin ?? Console.OpenStandardInput();
            var stdout = customStdout ?? Console.OpenStandardOutput();
            byte[] headerLineBuffer = new byte[1024];

            try
            {
                bool keepRunning = true;
                while (keepRunning)
                {
                    int lineLen = ReadLineBytes(stdin, headerLineBuffer);
                    if (lineLen <= 0) break;

                    string header = Encoding.ASCII.GetString(headerLineBuffer, 0, lineLen);
                    if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    {
                        int contentLength = int.Parse(header.Substring("Content-Length:".Length).Trim());

                        while (true)
                        {
                            int emptyLen = ReadLineBytes(stdin, headerLineBuffer);
                            if (emptyLen <= 2) break;
                        }

                        byte[] body = new byte[contentLength];
                        int totalRead = 0;
                        while (totalRead < contentLength)
                        {
                            int read = stdin.Read(body, totalRead, contentLength - totalRead);
                            if (read <= 0) break;
                            totalRead += read;
                        }

                        if (totalRead == contentLength)
                        {
                            if (!ProcessMessage(body, stdout))
                            {
                                keepRunning = false;
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private static int ReadLineBytes(Stream stream, byte[] buffer)
        {
            int index = 0;
            while (index < buffer.Length)
            {
                int b = stream.ReadByte();
                if (b == -1) return index;
                buffer[index++] = (byte)b;
                if (b == '\n') break;
            }
            return index;
        }

        private static bool ProcessMessage(byte[] body, Stream stdout)
        {
            using var jsonDoc = JsonDocument.Parse(body);
            var root = jsonDoc.RootElement;

            if (root.TryGetProperty("type", out var typeEl) && typeEl.GetString() == "request")
            {
                string command = root.GetProperty("command").GetString() ?? "";
                int requestSeq = root.GetProperty("seq").GetInt32();

                if (command == "initialize")
                {
                    SendResponse(stdout, requestSeq, command, "{\"supportsConfigurationDoneRequest\":true,\"supportsStepBack\":false}");
                }
                else if (command == "launch")
                {
                    // Parse program path if available
                    if (root.TryGetProperty("arguments", out var argsEl) && argsEl.TryGetProperty("program", out var progEl))
                    {
                        _currentFilePath = progEl.GetString() ?? _currentFilePath;
                    }

                    SendResponse(stdout, requestSeq, command, null);
                    SendEvent(stdout, "initialized", null);
                }
                else if (command == "setBreakpoints")
                {
                    if (root.TryGetProperty("arguments", out var argsEl) && argsEl.TryGetProperty("source", out var srcEl) && srcEl.TryGetProperty("path", out var pathEl))
                    {
                        _currentFilePath = pathEl.GetString() ?? _currentFilePath;
                    }

                    // Just verify all sent breakpoints
                    var bpsList = new StringBuilder();
                    bpsList.Append("[");
                    if (root.TryGetProperty("arguments", out var argumentsEl) && argumentsEl.TryGetProperty("breakpoints", out var bpsEl))
                    {
                        bool first = true;
                        foreach (var bp in bpsEl.EnumerateArray())
                        {
                            if (!first) bpsList.Append(",");
                            first = false;
                            int line = bp.GetProperty("line").GetInt32();
                            bpsList.Append($"{{\"verified\":true,\"line\":{line}}}");
                        }
                    }
                    else
                    {
                        bpsList.Append("{\"verified\":true,\"line\":13}");
                    }
                    bpsList.Append("]");

                    SendResponse(stdout, requestSeq, command, $"{{\"breakpoints\":{bpsList.ToString()}}}");
                }
                else if (command == "configurationDone")
                {
                    SendResponse(stdout, requestSeq, command, null);
                    
                    // Simulate running and stopping at breakpoint
                    Task.Run(async () =>
                    {
                        await Task.Delay(500);
                        SendEvent(stdout, "stopped", "{\"reason\":\"breakpoint\",\"threadId\":1,\"allThreadsStopped\":true}");
                    });
                }
                else if (command == "threads")
                {
                    var threadList = new System.Text.StringBuilder("[");
                    bool firstT = true;
                    try
                    {
                        var proc = Process.GetCurrentProcess();
                        foreach (ProcessThread t in proc.Threads)
                        {
                            if (!firstT) threadList.Append(",");
                            firstT = false;
                            threadList.Append($"{{\"id\":{t.Id},\"name\":\"Thread {t.Id}\"}}");
                        }
                    }
                    catch { }
                    if (firstT)
                    {
                        threadList.Append("{\"id\":1,\"name\":\"Main Thread\"}");
                    }
                    threadList.Append("]");
                    SendResponse(stdout, requestSeq, command, $"{{\"threads\":{threadList.ToString()}}}");
                }
                else if (command == "stackTrace")
                {
                    var st = new StackTrace(true);
                    var framesList = new System.Text.StringBuilder("[");
                    bool firstF = true;
                    var frames = st.GetFrames();
                    if (frames != null && frames.Length > 0)
                    {
                        for (int fi = 0; fi < Math.Min(frames.Length, 8); fi++)
                        {
                            var f = frames[fi];
                            var m = f.GetMethod();
                            string mName = m != null ? $"{m.DeclaringType?.Name}.{m.Name}()" : "NativeMethod()";
                            string fPath = f.GetFileName() ?? _currentFilePath;
                            string escaped = fPath.Replace("\\", "/");
                            int fLine = f.GetFileLineNumber();
                            if (fLine == 0) fLine = _currentLine;
                            int fCol = f.GetFileColumnNumber();
                            if (fCol == 0) fCol = 1;

                            if (!firstF) framesList.Append(",");
                            firstF = false;
                            framesList.Append($"{{\"id\":{1001 + fi},\"name\":\"{mName}\",\"source\":{{\"name\":\"{Path.GetFileName(fPath)}\",\"path\":\"{escaped}\"}},\"line\":{fLine},\"column\":{fCol}}}");
                        }
                    }
                    if (firstF)
                    {
                        string escapedPath = _currentFilePath.Replace("\\", "/");
                        framesList.Append($"{{\"id\":1001,\"name\":\"Program.Main()\",\"source\":{{\"name\":\"Program.cs\",\"path\":\"{escapedPath}\"}},\"line\":{_currentLine},\"column\":1}}");
                    }
                    framesList.Append("]");
                    SendResponse(stdout, requestSeq, command, $"{{\"stackFrames\":{framesList.ToString()}}}");
                }
                else if (command == "scopes")
                {
                    SendResponse(stdout, requestSeq, command, "{\"scopes\":[{\"name\":\"Locals\",\"variablesReference\":2001,\"expensive\":false}]}");
                }
                else if (command == "variables")
                {
                    var vars = new System.Text.StringBuilder("[");
                    vars.Append($"{{\"name\":\"ProcessId\",\"value\":\"{Environment.ProcessId}\",\"type\":\"int\",\"variablesReference\":0}},");
                    vars.Append($"{{\"name\":\"DotNetVersion\",\"value\":\"\\\"{Environment.Version}\\\"\",\"type\":\"string\",\"variablesReference\":0}},");
                    vars.Append($"{{\"name\":\"WorkingSetBytes\",\"value\":\"{Environment.WorkingSet}\",\"type\":\"long\",\"variablesReference\":0}},");
                    vars.Append($"{{\"name\":\"CurrentDirectory\",\"value\":\"\\\"{JsonEncodedText.Encode(Environment.CurrentDirectory)}\\\"\",\"type\":\"string\",\"variablesReference\":0}},");
                    vars.Append($"{{\"name\":\"ManagedThreadId\",\"value\":\"{Environment.CurrentManagedThreadId}\",\"type\":\"int\",\"variablesReference\":0}}");
                    vars.Append("]");
                    SendResponse(stdout, requestSeq, command, $"{{\"variables\":{vars.ToString()}}}");
                }
                else if (command == "continue")
                {
                    SendResponse(stdout, requestSeq, command, null);
                    Task.Run(async () =>
                    {
                        await Task.Delay(500);
                        _currentLine = 13; // loop back to 13
                        SendEvent(stdout, "stopped", "{\"reason\":\"breakpoint\",\"threadId\":1,\"allThreadsStopped\":true}");
                    });
                }
                else if (command == "next") // Step Over
                {
                    SendResponse(stdout, requestSeq, command, null);
                    Task.Run(async () =>
                    {
                        await Task.Delay(300);
                        _currentLine++; // step to next line
                        SendEvent(stdout, "stopped", "{\"reason\":\"step\",\"threadId\":1,\"allThreadsStopped\":true}");
                    });
                }
                else if (command == "stepIn")
                {
                    SendResponse(stdout, requestSeq, command, null);
                    Task.Run(async () =>
                    {
                        await Task.Delay(300);
                        _currentLine += 2;
                        SendEvent(stdout, "stopped", "{\"reason\":\"step\",\"threadId\":1,\"allThreadsStopped\":true}");
                    });
                }
                else if (command == "stepOut")
                {
                    SendResponse(stdout, requestSeq, command, null);
                    Task.Run(async () =>
                    {
                        await Task.Delay(300);
                        _currentLine = 13;
                        SendEvent(stdout, "stopped", "{\"reason\":\"step\",\"threadId\":1,\"allThreadsStopped\":true}");
                    });
                }
                else if (command == "disconnect")
                {
                    SendResponse(stdout, requestSeq, command, null);
                    return false;
                }
            }
            return true;
        }

        private static void SendResponse(Stream stdout, int requestSeq, string command, string? bodyJson)
        {
            string body = bodyJson != null ? $",\"body\":{bodyJson}" : "";
            string json = $"{{\"command\":\"{command}\",\"request_seq\":{requestSeq},\"seq\":{Interlocked.Increment(ref _seq)},\"success\":true,\"type\":\"response\"{body}}}";
            SendRaw(stdout, json);
        }

        private static void SendEvent(Stream stdout, string eventName, string? bodyJson)
        {
            string body = bodyJson != null ? $",\"body\":{bodyJson}" : "";
            string json = $"{{\"event\":\"{eventName}\",\"seq\":{Interlocked.Increment(ref _seq)},\"type\":\"event\"{body}}}";
            SendRaw(stdout, json);
        }

        private static void SendRaw(Stream stdout, string json)
        {
            byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
            string headers = $"Content-Length: {jsonBytes.Length}\r\n\r\n";
            byte[] headerBytes = Encoding.ASCII.GetBytes(headers);

            lock (_writeLock)
            {
                try
                {
                    stdout.Write(headerBytes, 0, headerBytes.Length);
                    stdout.Write(jsonBytes, 0, jsonBytes.Length);
                    stdout.Flush();
                }
                catch { }
            }
        }
    }
}
