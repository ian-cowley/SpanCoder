using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SpanCoder.Contracts;

namespace SpanCoder.Engine
{
    public class LspClient : IDisposable
    {
        private Process? _process;
        private readonly string _workspacePath;
        private readonly Action<string, DiagnosticItem[]> _onDiagnostics;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly object _writeLock = new object();
        private int _nextRequestId = 1;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pendingRequests = new();

        private void Log(string message)
        {
        }

        public LspClient(string workspacePath, Action<string, DiagnosticItem[]> onDiagnostics)
        {
            _workspacePath = workspacePath;
            _onDiagnostics = onDiagnostics;
            Log("LspClient initialized.");
        }

        public static bool IsCommandOnPath(string cmd)
        {
            var isWindows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
            var psi = new ProcessStartInfo
            {
                FileName = isWindows ? "where" : "which",
                Arguments = cmd,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            try
            {
                using var p = Process.Start(psi);
                if (p == null) return false;
                p.WaitForExit(1000);
                return p.ExitCode == 0;
            }
            catch { return false; }
        }

        public void Start()
        {
            Log("LspClient.Start called.");
            string executable = "";
            string arguments = "";

            if (IsCommandOnPath("csharp-ls"))
            {
                executable = "csharp-ls";
                arguments = "";
            }
            else
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                executable = Path.Combine(baseDir, "SpanCoder.Engine.exe");
                if (!File.Exists(executable))
                {
                    executable = Path.Combine(baseDir, "SpanCoder.Engine"); // Linux
                }

                arguments = "--mock-lsp";

                if (!File.Exists(executable))
                {
                    executable = "dotnet";
                    string dllPath = Path.Combine(baseDir, "SpanCoder.Engine.dll");
                    arguments = $"\"{dllPath}\" --mock-lsp";
                }
            }

            Start(executable, arguments);
        }

        public void Start(string executable, string arguments)
        {
            Log($"Starting LSP process: {executable} {arguments}");
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
                if (_process == null) throw new IOException("Failed to start LSP subprocess.");

                Log($"LSP process started with PID {_process.Id}");

                Task.Run(() => ReadLoop(_process.StandardOutput.BaseStream));
                Task.Run(() => ReadErrorLoop(_process.StandardError));

                int initId = Interlocked.Increment(ref _nextRequestId);
                string escapedPath = EscapeJsonString(_workspacePath);
                string initJson = $"{{\"jsonrpc\":\"2.0\",\"id\":{initId},\"method\":\"initialize\",\"params\":{{\"processId\":{Process.GetCurrentProcess().Id},\"rootPath\":\"{escapedPath}\",\"capabilities\":{{}}}}}}";
                SendRaw(initJson);
            }
            catch (Exception ex)
            {
                Log($"Failed to start LSP process: {ex.ToString()}");
                Console.WriteLine($"[LspClient] Failed to start LSP client: {ex.Message}");
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
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 32)
                        {
                            sb.AppendFormat("\\u{0:x4}", (int)c);
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            return sb.ToString();
        }

        public void NotifyDidOpen(string filePath, string text)
        {
            string uri = FilePathToUri(filePath);
            string escapedUri = EscapeJsonString(uri);
            string escapedText = EscapeJsonString(text);
            string json = $"{{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{{\"textDocument\":{{\"uri\":\"{escapedUri}\",\"languageId\":\"csharp\",\"version\":1,\"text\":\"{escapedText}\"}}}}}}";
            SendRaw(json);
        }

        public void NotifyDidChange(string filePath, string text)
        {
            string uri = FilePathToUri(filePath);
            string escapedUri = EscapeJsonString(uri);
            string escapedText = EscapeJsonString(text);
            string json = $"{{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didChange\",\"params\":{{\"textDocument\":{{\"uri\":\"{escapedUri}\",\"version\":2}},\"contentChanges\":[{{\"text\":\"{escapedText}\"}}]}}}}";
            SendRaw(json);
        }

        public async Task<AutocompleteItem[]> RequestCompletionAsync(string filePath, int line, int character)
        {
            string uri = FilePathToUri(filePath);
            string escapedUri = EscapeJsonString(uri);
            int id = Interlocked.Increment(ref _nextRequestId);
            var tcs = new TaskCompletionSource<JsonElement>();
            _pendingRequests[id] = tcs;

            string json = $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"textDocument/completion\",\"params\":{{\"textDocument\":{{\"uri\":\"{escapedUri}\"}},\"position\":{{\"line\":{line},\"character\":{character}}}}}}}";
            SendRaw(json);

            try
            {
                var response = await tcs.Task;
                if (response.ValueKind == JsonValueKind.Object && response.TryGetProperty("items", out var itemsEl))
                {
                    int len = itemsEl.GetArrayLength();
                    var list = new AutocompleteItem[len];
                    for (int i = 0; i < len; i++)
                    {
                        var el = itemsEl[i];
                        string label = el.GetProperty("label").GetString() ?? "";
                        string detail = el.TryGetProperty("detail", out var dEl) ? (dEl.GetString() ?? "") : "";
                        list[i] = new AutocompleteItem { Label = label, Detail = detail };
                    }
                    return list;
                }
            }
            catch (Exception ex)
            {
                Log($"Completion request failed: {ex.Message}");
                Console.WriteLine($"[LspClient] Completion request failed: {ex.Message}");
            }
            return Array.Empty<AutocompleteItem>();
        }

        public struct HoverResult
        {
            public string Contents;
            public int StartChar;
            public int EndChar;
            public int Line;
        }

        public async Task<HoverResult?> RequestHoverAsync(string filePath, int line, int character)
        {
            string uri = FilePathToUri(filePath);
            string escapedUri = EscapeJsonString(uri);
            int id = Interlocked.Increment(ref _nextRequestId);
            var tcs = new TaskCompletionSource<JsonElement>();
            _pendingRequests[id] = tcs;

            string json = $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"textDocument/hover\",\"params\":{{\"textDocument\":{{\"uri\":\"{escapedUri}\"}},\"position\":{{\"line\":{line},\"character\":{character}}}}}}}";
            SendRaw(json);

            try
            {
                var response = await tcs.Task;
                if (response.ValueKind == JsonValueKind.Object)
                {
                    string contents = "";
                    if (response.TryGetProperty("contents", out var contentsEl))
                    {
                        contents = contentsEl.GetString() ?? "";
                    }

                    int startChar = character;
                    int endChar = character;
                    if (response.TryGetProperty("range", out var rangeEl))
                    {
                        var startEl = rangeEl.GetProperty("start");
                        startChar = startEl.GetProperty("character").GetInt32();
                        var endEl = rangeEl.GetProperty("end");
                        endChar = endEl.GetProperty("character").GetInt32();
                    }

                    return new HoverResult
                    {
                        Contents = contents,
                        StartChar = startChar,
                        EndChar = endChar,
                        Line = line
                    };
                }
            }
            catch (Exception ex)
            {
                Log($"Hover request failed: {ex.Message}");
                Console.WriteLine($"[LspClient] Hover request failed: {ex.Message}");
            }
            return null;
        }

        public async Task<(string FilePath, int Line, int Character)?> RequestDefinitionAsync(string filePath, int line, int character)
        {
            string uri = FilePathToUri(filePath);
            string escapedUri = EscapeJsonString(uri);
            int id = Interlocked.Increment(ref _nextRequestId);
            var tcs = new TaskCompletionSource<JsonElement>();
            _pendingRequests[id] = tcs;

            string json = $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"textDocument/definition\",\"params\":{{\"textDocument\":{{\"uri\":\"{escapedUri}\"}},\"position\":{{\"line\":{line},\"character\":{character}}}}}}}";
            SendRaw(json);

            try
            {
                var response = await tcs.Task;
                if (response.ValueKind == JsonValueKind.Object)
                {
                    if (response.TryGetProperty("uri", out var uriEl) && response.TryGetProperty("range", out var rangeEl))
                    {
                        string targetUri = uriEl.GetString() ?? "";
                        var startEl = rangeEl.GetProperty("start");
                        int targetLine = startEl.GetProperty("line").GetInt32();
                        int targetChar = startEl.GetProperty("character").GetInt32();
                        return (UriToFilePath(targetUri), targetLine, targetChar);
                    }
                }
                else if (response.ValueKind == JsonValueKind.Array && response.GetArrayLength() > 0)
                {
                    var first = response[0];
                    if (first.TryGetProperty("uri", out var uriEl) && first.TryGetProperty("range", out var rangeEl))
                    {
                        string targetUri = uriEl.GetString() ?? "";
                        var startEl = rangeEl.GetProperty("start");
                        int targetLine = startEl.GetProperty("line").GetInt32();
                        int targetChar = startEl.GetProperty("character").GetInt32();
                        return (UriToFilePath(targetUri), targetLine, targetChar);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Definition request failed: {ex.Message}");
                Console.WriteLine($"[LspClient] Definition request failed: {ex.Message}");
            }
            return null;
        }

        public async Task<ReferenceItem[]> RequestReferencesAsync(string filePath, int line, int character)
        {
            string uri = FilePathToUri(filePath);
            string escapedUri = EscapeJsonString(uri);
            int id = Interlocked.Increment(ref _nextRequestId);
            var tcs = new TaskCompletionSource<JsonElement>();
            _pendingRequests[id] = tcs;

            string json = $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"textDocument/references\",\"params\":{{\"textDocument\":{{\"uri\":\"{escapedUri}\"}},\"position\":{{\"line\":{line},\"character\":{character}}},\"context\":{{\"includeDeclaration\":true}}}}}}";
            SendRaw(json);

            try
            {
                var response = await tcs.Task;
                if (response.ValueKind == JsonValueKind.Array)
                {
                    int len = response.GetArrayLength();
                    var list = new ReferenceItem[len];
                    for (int i = 0; i < len; i++)
                    {
                        var el = response[i];
                        string targetUri = el.GetProperty("uri").GetString() ?? "";
                        var rangeEl = el.GetProperty("range");
                        var startEl = rangeEl.GetProperty("start");
                        int targetLine = startEl.GetProperty("line").GetInt32();
                        int targetChar = startEl.GetProperty("character").GetInt32();
                        list[i] = new ReferenceItem { FilePath = UriToFilePath(targetUri), Line = targetLine, Character = targetChar };
                    }
                    return list;
                }
            }
            catch (Exception ex)
            {
                Log($"References request failed: {ex.Message}");
                Console.WriteLine($"[LspClient] References request failed: {ex.Message}");
            }
            return Array.Empty<ReferenceItem>();
        }

        public async Task<LspTextEdit[]> RequestRenameAsync(string filePath, int line, int character, string newName)
        {
            string uri = FilePathToUri(filePath);
            string escapedUri = EscapeJsonString(uri);
            string escapedNewName = EscapeJsonString(newName);
            int id = Interlocked.Increment(ref _nextRequestId);
            var tcs = new TaskCompletionSource<JsonElement>();
            _pendingRequests[id] = tcs;

            string json = $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"textDocument/rename\",\"params\":{{\"textDocument\":{{\"uri\":\"{escapedUri}\"}},\"position\":{{\"line\":{line},\"character\":{character}}},\"newName\":\"{escapedNewName}\"}}}}";
            SendRaw(json);

            try
            {
                var response = await tcs.Task;
                if (response.ValueKind == JsonValueKind.Object && response.TryGetProperty("changes", out var changesEl))
                {
                    var edits = new System.Collections.Generic.List<LspTextEdit>();
                    foreach (var prop in changesEl.EnumerateObject())
                    {
                        string targetUri = prop.Name;
                        string targetPath = UriToFilePath(targetUri);
                        var editsArr = prop.Value;
                        int editsLen = editsArr.GetArrayLength();
                        for (int i = 0; i < editsLen; i++)
                        {
                            var editEl = editsArr[i];
                            var rangeEl = editEl.GetProperty("range");
                            var startEl = rangeEl.GetProperty("start");
                            var endEl = rangeEl.GetProperty("end");
                            string text = editEl.GetProperty("newText").GetString() ?? "";

                            edits.Add(new LspTextEdit
                            {
                                FilePath = targetPath,
                                StartLine = startEl.GetProperty("line").GetInt32(),
                                StartCharacter = startEl.GetProperty("character").GetInt32(),
                                EndLine = endEl.GetProperty("line").GetInt32(),
                                EndCharacter = endEl.GetProperty("character").GetInt32(),
                                NewText = text
                            });
                        }
                    }
                    return edits.ToArray();
                }
            }
            catch (Exception ex)
            {
                Log($"Rename request failed: {ex.Message}");
                Console.WriteLine($"[LspClient] Rename request failed: {ex.Message}");
            }
            return Array.Empty<LspTextEdit>();
        }

        public async Task<DocumentSymbolItem[]> RequestDocumentSymbolsAsync(string filePath)
        {
            string uri = FilePathToUri(filePath);
            string escapedUri = EscapeJsonString(uri);
            int id = Interlocked.Increment(ref _nextRequestId);
            var tcs = new TaskCompletionSource<JsonElement>();
            _pendingRequests[id] = tcs;

            string json = $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"textDocument/documentSymbol\",\"params\":{{\"textDocument\":{{\"uri\":\"{escapedUri}\"}}}}}}";
            SendRaw(json);

            try
            {
                var response = await tcs.Task;
                if (response.ValueKind == JsonValueKind.Array)
                {
                    int len = response.GetArrayLength();
                    var list = new System.Collections.Generic.List<DocumentSymbolItem>();
                    for (int i = 0; i < len; i++)
                    {
                        var el = response[i];
                        string name = el.GetProperty("name").GetString() ?? "";
                        string detail = el.TryGetProperty("detail", out var dEl) ? (dEl.GetString() ?? "") : "";
                        
                        if (el.TryGetProperty("range", out var rangeEl))
                        {
                            var startEl = rangeEl.GetProperty("start");
                            int line = startEl.GetProperty("line").GetInt32();
                            int character = startEl.GetProperty("character").GetInt32();
                            list.Add(new DocumentSymbolItem { Name = name, Detail = detail, Line = line, Character = character });
                        }
                        else if (el.TryGetProperty("location", out var locEl))
                        {
                            var rangeEl2 = locEl.GetProperty("range");
                            var startEl2 = rangeEl2.GetProperty("start");
                            int line = startEl2.GetProperty("line").GetInt32();
                            int character = startEl2.GetProperty("character").GetInt32();
                            list.Add(new DocumentSymbolItem { Name = name, Detail = detail, Line = line, Character = character });
                        }
                    }
                    return list.ToArray();
                }
            }
            catch (Exception ex)
            {
                Log($"Document symbols request failed: {ex.Message}");
                Console.WriteLine($"[LspClient] Document symbols request failed: {ex.Message}");
            }
            return Array.Empty<DocumentSymbolItem>();
        }

        public struct LspTextEdit
        {
            public string FilePath;
            public int StartLine;
            public int StartCharacter;
            public int EndLine;
            public int EndCharacter;
            public string NewText;
        }

        private string FilePathToUri(string filePath)
        {
            return new Uri(Path.GetFullPath(filePath)).AbsoluteUri;
        }

        private string UriToFilePath(string uriString)
        {
            try
            {
                var uri = new Uri(uriString);
                return uri.LocalPath;
            }
            catch
            {
                return uriString;
            }
        }

        private void SendRaw(string json)
        {
            Log($"SendRaw: {json}");
            if (_process == null)
            {
                Log("SendRaw: _process is null!");
                return;
            }
            if (_process.HasExited)
            {
                Log("SendRaw: _process has exited!");
                return;
            }

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
                Log("SendRaw successfully flushed.");
            }
            catch (Exception ex)
            {
                Log($"SendRaw error: {ex.ToString()}");
                Console.WriteLine($"[LspClient] Error writing to LSP server: {ex.Message}");
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
                    if (lineLen <= 0)
                    {
                        Log("ReadLoop: ReadLineBytes returned <= 0, exiting loop.");
                        break;
                    }

                    string header = Encoding.ASCII.GetString(headerLineBuffer, 0, lineLen);
                    Log($"ReadLoop: header line: {header.Trim()}");
                    if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    {
                        int contentLength = int.Parse(header.Substring("Content-Length:".Length).Trim());
                        Log($"ReadLoop: Content-Length parsed: {contentLength}");

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
                            if (read <= 0)
                            {
                                Log("ReadLoop: Read returned <= 0, LSP stdout closed.");
                                throw new IOException("LSP server stdout closed");
                            }
                            totalRead += read;
                        }

                        Log($"ReadLoop: Successfully read body of {contentLength} bytes");
                        ProcessMessage(body);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"ReadLoop error: {ex.ToString()}");
                if (!_cts.Token.IsCancellationRequested)
                {
                    Console.WriteLine($"[LspClient] Error reading from LSP server: {ex.Message}");
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
            string bodyStr = Encoding.UTF8.GetString(body);
            Log($"ProcessMessage: {bodyStr}");
            try
            {
                using var jsonDoc = JsonDocument.Parse(body);
                var root = jsonDoc.RootElement;

                if (root.TryGetProperty("method", out var methodProp) && methodProp.GetString() == "textDocument/publishDiagnostics")
                {
                    var paramsEl = root.GetProperty("params");
                    string uriString = paramsEl.GetProperty("uri").GetString() ?? "";
                    string filePath = UriToFilePath(uriString);

                    var diagnosticsEl = paramsEl.GetProperty("diagnostics");
                    int len = diagnosticsEl.GetArrayLength();
                    var list = new DiagnosticItem[len];

                    Log($"ProcessMessage: published {len} diagnostics for {filePath}");
                    for (int i = 0; i < len; i++)
                    {
                        var el = diagnosticsEl[i];
                        var rangeEl = el.GetProperty("range");
                        var startEl = rangeEl.GetProperty("start");
                        var endEl = rangeEl.GetProperty("end");

                        int startLine = startEl.GetProperty("line").GetInt32();
                        int startChar = startEl.GetProperty("character").GetInt32();
                        int endLine = endEl.GetProperty("line").GetInt32();
                        int endChar = endEl.GetProperty("character").GetInt32();

                        byte severity = el.TryGetProperty("severity", out var sevEl) ? (byte)sevEl.GetInt32() : (byte)1;
                        string message = el.GetProperty("message").GetString() ?? "";

                        list[i] = new DiagnosticItem
                        {
                            StartOffset = startLine,     // temp: start line
                            EndOffset = startChar,       // temp: start char
                            Severity = severity,
                            Message = $"{endLine}:{endChar}:{message}" // encode end pos and message
                        };
                    }

                    _onDiagnostics(filePath, list);
                }
                else if (root.TryGetProperty("id", out var idProp))
                {
                    int id = idProp.GetInt32();
                    Log($"ProcessMessage: processing response for request ID: {id}");
                    if (_pendingRequests.TryRemove(id, out var tcs))
                    {
                        if (root.TryGetProperty("result", out var resultProp))
                        {
                            tcs.SetResult(resultProp.Clone());
                        }
                        else
                        {
                            tcs.SetException(new Exception("LSP request failed."));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"ProcessMessage parsing error: {ex.ToString()}");
                Console.WriteLine($"[LspClient] Error processing LSP message: {ex.Message}");
            }
        }

        private void ReadErrorLoop(StreamReader reader)
        {
            try
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    string? line = reader.ReadLine();
                    if (line == null) break;
                    Console.WriteLine($"[LspClient-Err] {line}");
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
