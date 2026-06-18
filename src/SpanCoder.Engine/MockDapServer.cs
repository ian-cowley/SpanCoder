using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SpanCoder.Engine
{
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
                    SendResponse(stdout, requestSeq, command, "{\"threads\":[{\"id\":1,\"name\":\"Main Thread\"}]}");
                }
                else if (command == "stackTrace")
                {
                    string escapedPath = _currentFilePath.Replace("\\", "/");
                    SendResponse(stdout, requestSeq, command, $"{{\"stackFrames\":[{{\"id\":1001,\"name\":\"Program.Main()\",\"source\":{{\"name\":\"Program.cs\",\"path\":\"{escapedPath}\"}},\"line\":{_currentLine},\"column\":1}}]}}");
                }
                else if (command == "scopes")
                {
                    SendResponse(stdout, requestSeq, command, "{\"scopes\":[{\"name\":\"Locals\",\"variablesReference\":2001,\"expensive\":false}]}");
                }
                else if (command == "variables")
                {
                    SendResponse(stdout, requestSeq, command, "{\"variables\":[{\"name\":\"args\",\"value\":\"string[0]\",\"type\":\"string[]\",\"variablesReference\":0},{\"name\":\"x\",\"value\":\"123\",\"type\":\"int\",\"variablesReference\":0},{\"name\":\"status\",\"value\":\"\\\"Running\\\"\",\"type\":\"string\",\"variablesReference\":0}]}");
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
