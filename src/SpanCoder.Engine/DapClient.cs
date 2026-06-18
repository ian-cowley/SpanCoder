using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SpanCoder.Engine
{
    public class DapClient : IDisposable
    {
        private Process? _process;
        private readonly Action<string, int, int, string> _onStopped;
        private readonly Action<System.Collections.Generic.List<string>, System.Collections.Generic.List<string>> _onStateReport;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly object _writeLock = new object();
        private int _nextRequestId = 1;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pendingRequests = new();

        public DapClient(Action<string, int, int, string> onStopped, Action<System.Collections.Generic.List<string>, System.Collections.Generic.List<string>> onStateReport)
        {
            _onStopped = onStopped;
            _onStateReport = onStateReport;
        }

        public void Start(string executable, string arguments, string programPath)
        {
            var psi = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            try
            {
                _process = Process.Start(psi);
                if (_process == null) throw new IOException("Failed to start DAP subprocess.");

                Task.Run(() => ReadLoop(_process.StandardOutput.BaseStream));
                Task.Run(() => ReadErrorLoop(_process.StandardError));

                // 1. Initialize Request
                SendRequest("initialize", "{\"clientID\":\"spancoder\",\"adapterID\":\"coreclr\"}");

                // 2. Launch Request
                string escapedPath = EscapeJsonString(programPath).Replace("\\", "/");
                SendRequest("launch", $"{{\"noDebug\":false,\"program\":\"{escapedPath}\",\"args\":[]}}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DapClient] Failed to start: {ex.Message}");
            }
        }

        private static string EscapeJsonString(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        public void SetBreakpoints(string filePath, System.Collections.Generic.List<int> lines)
        {
            string escapedPath = EscapeJsonString(filePath).Replace("\\", "/");
            var bpsBuilder = new StringBuilder();
            bpsBuilder.Append("[");
            for (int i = 0; i < lines.Count; i++)
            {
                if (i > 0) bpsBuilder.Append(",");
                bpsBuilder.Append($"{{\"line\":{lines[i]}}}");
            }
            bpsBuilder.Append("]");

            SendRequest("setBreakpoints", $"{{\"source\":{{\"path\":\"{escapedPath}\"}},\"breakpoints\":{bpsBuilder.ToString()}}}");
        }

        public void ConfigurationDone()
        {
            SendRequest("configurationDone", null);
        }

        public void Continue()
        {
            SendRequest("continue", "{\"threadId\":1}");
        }

        public void StepOver()
        {
            SendRequest("next", "{\"threadId\":1}");
        }

        public void StepInto()
        {
            SendRequest("stepIn", "{\"threadId\":1}");
        }

        public void StepOut()
        {
            SendRequest("stepOut", "{\"threadId\":1}");
        }

        public void Stop()
        {
            SendRequest("disconnect", "{\"terminateDebuggee\":true}");
        }

        private void SendRequest(string command, string? argumentsJson)
        {
            int id = Interlocked.Increment(ref _nextRequestId);
            string args = argumentsJson != null ? $",\"arguments\":{argumentsJson}" : "";
            string json = $"{{\"command\":\"{command}\",\"seq\":{id},\"type\":\"request\"{args}}}";
            SendRaw(json);
        }

        private void SendRaw(string json)
        {
            if (_process == null || _process.HasExited) return;

            byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
            string headers = $"Content-Length: {jsonBytes.Length}\r\n\r\n";
            byte[] headerBytes = Encoding.ASCII.GetBytes(headers);

            try
            {
                lock (_writeLock)
                {
                    var stdin = _process.StandardInput.BaseStream;
                    stdin.Write(headerBytes, 0, headerBytes.Length);
                    stdin.Write(jsonBytes, 0, jsonBytes.Length);
                    stdin.Flush();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DapClient] Error writing to server: {ex.Message}");
            }
        }

        private void ReadLoop(Stream stream)
        {
            byte[] headerLineBuffer = new byte[1024];
            try
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    int lineLen = ReadLineBytes(stream, headerLineBuffer);
                    if (lineLen <= 0) break;

                    string header = Encoding.ASCII.GetString(headerLineBuffer, 0, lineLen);
                    if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    {
                        int contentLength = int.Parse(header.Substring("Content-Length:".Length).Trim());

                        while (true)
                        {
                            int emptyLen = ReadLineBytes(stream, headerLineBuffer);
                            if (emptyLen <= 2) break;
                        }

                        byte[] body = new byte[contentLength];
                        int totalRead = 0;
                        while (totalRead < contentLength)
                        {
                            int read = stream.Read(body, totalRead, contentLength - totalRead);
                            if (read <= 0) throw new IOException("DAP server stdout closed");
                            totalRead += read;
                        }

                        ProcessMessage(body);
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_cts.Token.IsCancellationRequested)
                {
                    Console.WriteLine($"[DapClient] Read loop error: {ex.Message}");
                }
            }
        }

        private int ReadLineBytes(Stream stream, byte[] buffer)
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

        private void ProcessMessage(byte[] body)
        {
            try
            {
                using var jsonDoc = JsonDocument.Parse(body);
                var root = jsonDoc.RootElement;

                if (root.TryGetProperty("type", out var typeEl))
                {
                    string type = typeEl.GetString() ?? "";
                    if (type == "event")
                    {
                        string eventName = root.GetProperty("event").GetString() ?? "";
                        if (eventName == "initialized")
                        {
                            ConfigurationDone();
                        }
                        else if (eventName == "stopped")
                        {
                            var bodyEl = root.GetProperty("body");
                            string reason = bodyEl.GetProperty("reason").GetString() ?? "breakpoint";
                            
                            Task.Run(async () =>
                            {
                                await QueryStateAndReportAsync(reason);
                            });
                        }
                    }
                    else if (type == "response")
                    {
                        int requestSeq = root.GetProperty("request_seq").GetInt32();
                        if (_pendingRequests.TryRemove(requestSeq, out var tcs))
                        {
                            if (root.TryGetProperty("success", out var successEl) && successEl.GetBoolean())
                            {
                                if (root.TryGetProperty("body", out var bodyEl))
                                {
                                    tcs.SetResult(bodyEl.Clone());
                                }
                                else
                                {
                                    tcs.SetResult(default);
                                }
                            }
                            else
                            {
                                tcs.SetResult(default);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DapClient] Message processing error: {ex.Message}");
            }
        }

        private async Task QueryStateAndReportAsync(string reason)
        {
            try
            {
                var threadsEl = await SendRequestAsync("threads", null);
                var stackEl = await SendRequestAsync("stackTrace", "{\"threadId\":1}");
                var framesList = new System.Collections.Generic.List<string>();
                string activeFilePath = "";
                int activeLine = 1;

                if (stackEl.TryGetProperty("stackFrames", out var framesEl) && framesEl.GetArrayLength() > 0)
                {
                    for (int i = 0; i < framesEl.GetArrayLength(); i++)
                    {
                        var frame = framesEl[i];
                        string name = frame.GetProperty("name").GetString() ?? "";
                        int line = frame.GetProperty("line").GetInt32();
                        
                        string sourceName = "";
                        if (frame.TryGetProperty("source", out var srcEl))
                        {
                            sourceName = srcEl.GetProperty("name").GetString() ?? "";
                            if (i == 0)
                            {
                                activeFilePath = srcEl.TryGetProperty("path", out var pEl) ? (pEl.GetString() ?? "") : "";
                                activeLine = line;
                            }
                        }
                        framesList.Add($"{name} ({activeFilePath}:{line})");
                    }
                }

                var scopesEl = await SendRequestAsync("scopes", "{\"frameId\":1001}");
                var varsList = new System.Collections.Generic.List<string>();

                if (scopesEl.TryGetProperty("scopes", out var scopesArrEl) && scopesArrEl.GetArrayLength() > 0)
                {
                    int varRef = scopesArrEl[0].GetProperty("variablesReference").GetInt32();
                    if (varRef > 0)
                    {
                        var varsEl = await SendRequestAsync("variables", $"{{\"variablesReference\":{varRef}}}");
                        if (varsEl.TryGetProperty("variables", out var varsArrEl))
                        {
                            for (int i = 0; i < varsArrEl.GetArrayLength(); i++)
                            {
                                var v = varsArrEl[i];
                                string name = v.GetProperty("name").GetString() ?? "";
                                string val = v.GetProperty("value").GetString() ?? "";
                                string type = v.TryGetProperty("type", out var tEl) ? (tEl.GetString() ?? "") : "";
                                varsList.Add($"{name}: {val} ({type})");
                            }
                        }
                    }
                }

                _onStateReport(framesList, varsList);
                _onStopped(activeFilePath, activeLine, 1, reason);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DapClient] Error querying state: {ex.Message}");
            }
        }

        private Task<JsonElement> SendRequestAsync(string command, string? argumentsJson)
        {
            int id = Interlocked.Increment(ref _nextRequestId);
            var tcs = new TaskCompletionSource<JsonElement>();
            _pendingRequests[id] = tcs;

            string args = argumentsJson != null ? $",\"arguments\":{argumentsJson}" : "";
            string json = $"{{\"command\":\"{command}\",\"seq\":{id},\"type\":\"request\"{args}}}";
            SendRaw(json);

            return tcs.Task;
        }

        private void ReadErrorLoop(StreamReader reader)
        {
            try
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    string? line = reader.ReadLine();
                    if (line == null) break;
                    Console.WriteLine($"[DapClient-Err] {line}");
                }
            }
            catch { }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try
            {
                if (_process != null && !_process.HasExited)
                {
                    _process.Kill();
                }
            }
            catch { }
            _process?.Dispose();
        }
    }
}
