using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using SpanCoder.Contracts;

namespace SpanCoder.Engine
{
    public partial class EngineHost
    {
        private static string FindWorkspacePath(string filePath)
        {
            try
            {
                string? dir = Path.GetDirectoryName(filePath);
                while (dir != null)
                {
                    if (Directory.GetFiles(dir, "*.sln").Length > 0 ||
                        Directory.GetFiles(dir, "*.slnx").Length > 0 ||
                        Directory.GetFiles(dir, "*.csproj").Length > 0 ||
                        Directory.GetDirectories(dir, ".git").Length > 0)
                    {
                        return dir;
                    }
                    dir = Path.GetDirectoryName(dir);
                }
            }
            catch { }
            return Path.GetDirectoryName(filePath) ?? @"c:\Users\spuri\source\repos\PolarsPlus";
        }

        private LspClient GetOrCreateLspClient(string filePath)
        {
            string ext = Path.GetExtension(filePath).ToLower();
            if (string.IsNullOrEmpty(ext)) ext = ".cs";

            return _lspClients.GetOrAdd(ext, e =>
            {
                string workspacePath = FindWorkspacePath(filePath);
                var client = new LspClient(workspacePath, HandleLspDiagnostics);

                string executable = "";
                string arguments = "";

                if (e == ".cs" && LspClient.IsCommandOnPath("csharp-ls"))
                {
                    executable = "csharp-ls";
                    arguments = "";
                }
                else if (e == ".py" && LspClient.IsCommandOnPath("pyright-langserver"))
                {
                    executable = "pyright-langserver";
                    arguments = "--stdio";
                }
                else if (e == ".py" && LspClient.IsCommandOnPath("pylsp"))
                {
                    executable = "pylsp";
                    arguments = "";
                }
                else if (e == ".rs" && LspClient.IsCommandOnPath("rust-analyzer"))
                {
                    executable = "rust-analyzer";
                    arguments = "";
                }
                else if (e == ".go" && LspClient.IsCommandOnPath("gopls"))
                {
                    executable = "gopls";
                    arguments = "";
                }
                else if ((e == ".c" || e == ".cpp" || e == ".cc" || e == ".h" || e == ".hpp") && LspClient.IsCommandOnPath("clangd"))
                {
                    executable = "clangd";
                    arguments = "";
                }
                else if (e == ".java" && LspClient.IsCommandOnPath("jdtls"))
                {
                    executable = "jdtls";
                    arguments = "";
                }
                else
                {
                    // Fallback to mock LSP
                    string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    executable = Path.Combine(baseDir, "SpanCoder.Engine.exe");
                    if (!File.Exists(executable))
                    {
                        executable = Path.Combine(baseDir, "SpanCoder.Engine");
                    }
                    arguments = "--mock-lsp";
                    if (!File.Exists(executable))
                    {
                        executable = "dotnet";
                        string dllPath = Path.Combine(baseDir, "SpanCoder.Engine.dll");
                        arguments = $"\"{dllPath}\" --mock-lsp";
                    }
                }

                client.Start(executable, arguments);
                return client;
            });
        }

        private void HandleLspDiagnostics(string filePath, DiagnosticItem[] rawDiagnostics)
        {
            Log($"HandleLspDiagnostics: filePath={filePath}, count={rawDiagnostics.Length}");
            Document? doc = null;
            foreach (var d in _documents.Values)
            {
                if (d.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase))
                {
                    doc = d;
                    break;
                }
            }

            if (doc == null)
            {
                Log($"HandleLspDiagnostics: Document NOT found for filePath={filePath}! Active document paths: {string.Join(", ", System.Linq.Enumerable.Select(_documents.Values, d => d.FilePath))}");
                return;
            }

            Log($"HandleLspDiagnostics: Document found, docId={doc.Id}. Translating coordinates...");
            var translated = new DiagnosticItem[rawDiagnostics.Length];
            for (int i = 0; i < rawDiagnostics.Length; i++)
            {
                var raw = rawDiagnostics[i];
                int startLine = raw.StartOffset; // temp: line
                int startChar = raw.EndOffset; // temp: char

                int colon1 = raw.Message.IndexOf(':');
                int colon2 = raw.Message.IndexOf(':', colon1 + 1);
                int endLine = int.Parse(raw.Message.Substring(0, colon1));
                int endChar = int.Parse(raw.Message.Substring(colon1 + 1, colon2 - colon1 - 1));
                string msg = raw.Message.Substring(colon2 + 1);

                long lineStart = doc.GetLineStart(startLine);
                int startAbs = (int)lineStart + startChar;

                long endLineStart = doc.GetLineStart(endLine);
                int endAbs = (int)endLineStart + endChar;

                translated[i] = new DiagnosticItem
                {
                    StartOffset = startAbs,
                    EndOffset = endAbs,
                    Severity = raw.Severity,
                    Message = msg
                };
            }

            int sizeNeeded = BinaryMessageSerializer.HeaderSize + sizeof(int);
            for (int i = 0; i < translated.Length; i++)
            {
                sizeNeeded += sizeof(int) * 3 + translated[i].Message.Length * sizeof(char);
            }

            byte[] sendBuffer = new byte[sizeNeeded + 64];
            int len = BinaryMessageSerializer.WriteDiagnosticsReport(sendBuffer, doc.Id, translated);

            byte[] finalBuffer = new byte[len];
            Array.Copy(sendBuffer, 0, finalBuffer, 0, len);
            MessageReceived?.Invoke(finalBuffer);
        }

        private void HandleLspDapMessage(MessageHeader header, ReadOnlySpan<byte> message)
        {
            switch (header.Type)
            {
                case MessageTypes.AutocompleteRequest:
                    {
                        int docId = header.DocumentId;
                        int offset = header.Offset;
                        if (_documents.TryGetValue(docId, out var doc))
                        {
                            int line = doc.LineIndex.GetLineIndexFromOffset(offset);
                            long lineStart = doc.GetLineStart(line);
                            int character = offset - (int)lineStart;

                            Task.Run(async () =>
                            {
                                var items = await GetOrCreateLspClient(doc.FilePath).RequestCompletionAsync(doc.FilePath, line, character);
                                int respLen = BinaryMessageSerializer.HeaderSize + sizeof(int);
                                for (int i = 0; i < items.Length; i++)
                                    respLen += sizeof(int) * 2 + (items[i].Label.Length + items[i].Detail.Length) * sizeof(char);

                                byte[] buffer = new byte[respLen + 64];
                                int len = BinaryMessageSerializer.WriteAutocompleteResponse(buffer, docId, offset, items);
                                byte[] finalBuffer = new byte[len];
                                Array.Copy(buffer, 0, finalBuffer, 0, len);
                                MessageReceived?.Invoke(finalBuffer);
                            });
                        }
                        break;
                    }

                case MessageTypes.HoverRequest:
                    {
                        int docId = header.DocumentId;
                        int offset = header.Offset;
                        if (_documents.TryGetValue(docId, out var doc))
                        {
                            int line = doc.LineIndex.GetLineIndexFromOffset(offset);
                            long lineStart = doc.GetLineStart(line);
                            int character = offset - (int)lineStart;

                            Task.Run(async () =>
                            {
                                var hover = await GetOrCreateLspClient(doc.FilePath).RequestHoverAsync(doc.FilePath, line, character);
                                if (hover != null)
                                {
                                    long startLineStart = doc.GetLineStart(hover.Value.Line);
                                    int startAbs = (int)startLineStart + hover.Value.StartChar;
                                    int endAbs = (int)startLineStart + hover.Value.EndChar;

                                    string contents = hover.Value.Contents;
                                    int respLen = BinaryMessageSerializer.HeaderSize + sizeof(int) * 3 + contents.Length * sizeof(char);
                                    byte[] buffer = new byte[respLen + 64];
                                    int len = BinaryMessageSerializer.WriteHoverResponse(buffer, docId, offset, startAbs, endAbs, contents);
                                    byte[] finalBuffer = new byte[len];
                                    Array.Copy(buffer, 0, finalBuffer, 0, len);
                                    MessageReceived?.Invoke(finalBuffer);
                                }
                                else
                                {
                                    int respLen = BinaryMessageSerializer.HeaderSize + sizeof(int) * 3;
                                    byte[] buffer = new byte[respLen + 64];
                                    int len = BinaryMessageSerializer.WriteHoverResponse(buffer, docId, offset, offset, offset, "");
                                    byte[] finalBuffer = new byte[len];
                                    Array.Copy(buffer, 0, finalBuffer, 0, len);
                                    MessageReceived?.Invoke(finalBuffer);
                                }
                            });
                        }
                        break;
                    }

                case MessageTypes.GotoDefinitionRequest:
                    {
                        int docId = header.DocumentId;
                        int offset = header.Offset;
                        if (_documents.TryGetValue(docId, out var doc))
                        {
                            int line = doc.LineIndex.GetLineIndexFromOffset(offset);
                            long lineStart = doc.GetLineStart(line);
                            int character = offset - (int)lineStart;

                            Task.Run(async () =>
                            {
                                var definition = await GetOrCreateLspClient(doc.FilePath).RequestDefinitionAsync(doc.FilePath, line, character);
                                if (definition != null)
                                {
                                    string targetPath = definition.Value.FilePath;
                                    int targetLine = definition.Value.Line;
                                    int targetChar = definition.Value.Character;

                                    int respLen = BinaryMessageSerializer.HeaderSize + sizeof(int) * 3 + targetPath.Length * sizeof(char);
                                    byte[] buffer = new byte[respLen + 64];
                                    int len = BinaryMessageSerializer.WriteGotoDefinitionResponse(buffer, docId, offset, targetPath, targetLine, targetChar);
                                    byte[] finalBuffer = new byte[len];
                                    Array.Copy(buffer, 0, finalBuffer, 0, len);
                                    MessageReceived?.Invoke(finalBuffer);
                                }
                                else
                                {
                                    int respLen = BinaryMessageSerializer.HeaderSize + sizeof(int) * 3;
                                    byte[] buffer = new byte[respLen + 64];
                                    int len = BinaryMessageSerializer.WriteGotoDefinitionResponse(buffer, docId, offset, "", 0, 0);
                                    byte[] finalBuffer = new byte[len];
                                    Array.Copy(buffer, 0, finalBuffer, 0, len);
                                    MessageReceived?.Invoke(finalBuffer);
                                }
                            });
                        }
                        break;
                    }

                case MessageTypes.FindReferencesRequest:
                    {
                        int docId = header.DocumentId;
                        int offset = header.Offset;
                        if (_documents.TryGetValue(docId, out var doc))
                        {
                            int line = doc.LineIndex.GetLineIndexFromOffset(offset);
                            long lineStart = doc.GetLineStart(line);
                            int character = offset - (int)lineStart;

                            Task.Run(async () =>
                            {
                                var items = await GetOrCreateLspClient(doc.FilePath).RequestReferencesAsync(doc.FilePath, line, character);
                                int respLen = BinaryMessageSerializer.HeaderSize + sizeof(int);
                                for (int i = 0; i < items.Length; i++)
                                    respLen += sizeof(int) + items[i].FilePath.Length * sizeof(char) + sizeof(int) + sizeof(int);

                                byte[] buffer = new byte[respLen + 64];
                                int len = BinaryMessageSerializer.WriteFindReferencesResponse(buffer, docId, offset, items);
                                byte[] finalBuffer = new byte[len];
                                Array.Copy(buffer, 0, finalBuffer, 0, len);
                                MessageReceived?.Invoke(finalBuffer);
                            });
                        }
                        break;
                    }

                case MessageTypes.RenameRequest:
                    {
                        string newName = BinaryMessageSerializer.ParseRenameRequest(message, out int docId, out int offset);
                        if (_documents.TryGetValue(docId, out var doc))
                        {
                            int line = doc.LineIndex.GetLineIndexFromOffset(offset);
                            long lineStart = doc.GetLineStart(line);
                            int character = offset - (int)lineStart;

                            Task.Run(async () =>
                            {
                                var edits = await GetOrCreateLspClient(doc.FilePath).RequestRenameAsync(doc.FilePath, line, character, newName);

                                // Group edits by document
                                var groupEdits = new Dictionary<Document, List<LspClient.LspTextEdit>>();
                                foreach (var edit in edits)
                                {
                                    Document? targetDoc = null;
                                    foreach (var d in _documents.Values)
                                    {
                                        if (d.FilePath.Equals(edit.FilePath, StringComparison.OrdinalIgnoreCase))
                                        {
                                            targetDoc = d;
                                            break;
                                        }
                                    }
                                    if (targetDoc != null)
                                    {
                                        if (!groupEdits.TryGetValue(targetDoc, out var list))
                                        {
                                            list = new List<LspClient.LspTextEdit>();
                                            groupEdits[targetDoc] = list;
                                        }
                                        list.Add(edit);
                                    }
                                }

                                bool success = true;
                                try
                                {
                                    foreach (var kvp in groupEdits)
                                    {
                                        var targetDoc = kvp.Key;
                                        var docEdits = kvp.Value;

                                        // Sort descending by StartLine, then StartCharacter to safely apply edits
                                        docEdits.Sort((a, b) =>
                                        {
                                            if (a.StartLine != b.StartLine)
                                                return b.StartLine.CompareTo(a.StartLine);
                                            return b.StartCharacter.CompareTo(a.StartCharacter);
                                        });

                                        foreach (var edit in docEdits)
                                        {
                                            long startLineStart = targetDoc.GetLineStart(edit.StartLine);
                                            int startAbs = (int)startLineStart + edit.StartCharacter;
                                            long endLineStart = targetDoc.GetLineStart(edit.EndLine);
                                            int endAbs = (int)endLineStart + edit.EndCharacter;

                                            int delLen = endAbs - startAbs;
                                            if (delLen > 0)
                                            {
                                                targetDoc.Delete(startAbs, delLen);
                                            }
                                            if (edit.NewText.Length > 0)
                                            {
                                                targetDoc.Insert(startAbs, edit.NewText);
                                            }

                                            // Broadcast DocumentChanged message to update the shell UI
                                            byte[] docChangedBuffer = new byte[BinaryMessageSerializer.HeaderSize + 8 + edit.NewText.Length * 2];
                                            BinaryMessageSerializer.WriteDocumentChanged(docChangedBuffer, targetDoc.Id, startAbs, edit.NewText.Length, delLen, edit.NewText);
                                            MessageReceived?.Invoke(docChangedBuffer);
                                        }

                                        // Notify LSP client of the updated document text
                                        char[] fullBuf = new char[targetDoc.Length];
                                        targetDoc.PieceTable.GetText(0, targetDoc.Length, fullBuf);
                                        GetOrCreateLspClient(targetDoc.FilePath).NotifyDidChange(targetDoc.FilePath, new string(fullBuf));
                                    }
                                }
                                catch (Exception ex)
                                {
                                    SpanCoderDiagnostics.LogError($"[EngineHost] Error applying rename edits: {ex.Message}");
                                    success = false;
                                }

                                byte[] respBuffer = new byte[BinaryMessageSerializer.HeaderSize + 1];
                                int respLen = BinaryMessageSerializer.WriteRenameResponse(respBuffer, docId, offset, success);
                                byte[] finalResp = new byte[respLen];
                                Array.Copy(respBuffer, 0, finalResp, 0, respLen);
                                MessageReceived?.Invoke(finalResp);
                            });
                        }
                        break;
                    }

                case MessageTypes.DocumentSymbolsRequest:
                    {
                        int docId = header.DocumentId;
                        if (_documents.TryGetValue(docId, out var doc))
                        {
                            Task.Run(async () =>
                            {
                                var items = await GetOrCreateLspClient(doc.FilePath).RequestDocumentSymbolsAsync(doc.FilePath);
                                int respLen = BinaryMessageSerializer.HeaderSize + sizeof(int);
                                for (int i = 0; i < items.Length; i++)
                                {
                                    respLen += sizeof(int) + items[i].Name.Length * sizeof(char) +
                                                 sizeof(int) + items[i].Detail.Length * sizeof(char) +
                                                 sizeof(int) + sizeof(int);
                                }

                                byte[] buffer = new byte[respLen + 64];
                                int len = BinaryMessageSerializer.WriteDocumentSymbolsResponse(buffer, docId, items);
                                byte[] finalBuffer = new byte[len];
                                Array.Copy(buffer, 0, finalBuffer, 0, len);
                                MessageReceived?.Invoke(finalBuffer);
                            });
                        }
                        break;
                    }

                case MessageTypes.FoldingRangeRequest:
                    {
                        int docId = header.DocumentId;
                        if (_documents.TryGetValue(docId, out var doc))
                        {
                            Task.Run(async () =>
                            {
                                var items = await GetOrCreateLspClient(doc.FilePath).RequestFoldingRangesAsync(doc.FilePath);
                                int respLen = BinaryMessageSerializer.HeaderSize + sizeof(int) + items.Length * sizeof(int) * 2;
                                byte[] buffer = new byte[respLen + 64];
                                int len = BinaryMessageSerializer.WriteFoldingRangeResponse(buffer, docId, items);
                                byte[] finalBuffer = new byte[len];
                                Array.Copy(buffer, 0, finalBuffer, 0, len);
                                MessageReceived?.Invoke(finalBuffer);
                            });
                        }
                        break;
                    }

                case MessageTypes.CommandRequest:
                    {
                        // Command routing is executed on the Shell, but we could handle engine-specific commands here.
                        break;
                    }

                case MessageTypes.DebugStartRequest:
                    {
                        string programPath = BinaryMessageSerializer.ParseDebugStartRequest(message, out int docId);
                        _dapClient?.Dispose();

                        _dapClient = new DapClient(
                            (filePath, line, character, reason) =>
                            {
                                Document? doc = null;
                                foreach (var d in _documents.Values)
                                {
                                    if (d.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase))
                                    {
                                        doc = d;
                                        break;
                                    }
                                }
                                int matchedDocId = doc?.Id ?? docId; // Fallback to docId if not matched
                                byte[] sendBuffer = new byte[BinaryMessageSerializer.HeaderSize + 12 + reason.Length * 2];
                                int len = BinaryMessageSerializer.WriteDebugStoppedEvent(sendBuffer, matchedDocId, line, character, reason);
                                byte[] finalBuffer = new byte[len];
                                Array.Copy(sendBuffer, 0, finalBuffer, 0, len);
                                MessageReceived?.Invoke(finalBuffer);
                            },
                            (stackFrames, variables) =>
                            {
                                int bodyLen = sizeof(int);
                                for (int i = 0; i < stackFrames.Count; i++) bodyLen += sizeof(int) + stackFrames[i].Length * sizeof(char);
                                bodyLen += sizeof(int);
                                for (int i = 0; i < variables.Count; i++) bodyLen += sizeof(int) + variables[i].Length * sizeof(char);

                                byte[] sendBuffer = new byte[BinaryMessageSerializer.HeaderSize + bodyLen];
                                int len = BinaryMessageSerializer.WriteDebugStateReport(sendBuffer, docId, stackFrames, variables);
                                byte[] finalBuffer = new byte[len];
                                Array.Copy(sendBuffer, 0, finalBuffer, 0, len);
                                MessageReceived?.Invoke(finalBuffer);
                            }
                        );

                        string debugType = "coreclr";
                        string gdbPath = "gdb-multiarch";
                        string customProgram = "";

                        string? debugConfigPath = null;
                        string? currentDir = Path.GetDirectoryName(programPath);
                        while (!string.IsNullOrEmpty(currentDir))
                        {
                            string checkPath = Path.Combine(currentDir, "spancoder_debug.json");
                            if (File.Exists(checkPath))
                            {
                                debugConfigPath = checkPath;
                                break;
                            }
                            currentDir = Path.GetDirectoryName(currentDir);
                        }

                        if (debugConfigPath != null)
                        {
                            try
                            {
                                string json = File.ReadAllText(debugConfigPath);
                                using var doc = System.Text.Json.JsonDocument.Parse(json);
                                if (doc.RootElement.TryGetProperty("type", out var typeProp))
                                {
                                    debugType = typeProp.GetString() ?? "coreclr";
                                }
                                if (doc.RootElement.TryGetProperty("gdbPath", out var gdbProp))
                                {
                                    gdbPath = gdbProp.GetString() ?? "gdb-multiarch";
                                }
                                if (doc.RootElement.TryGetProperty("program", out var progProp))
                                {
                                    customProgram = progProp.GetString() ?? "";
                                    if (!string.IsNullOrEmpty(customProgram) && !Path.IsPathRooted(customProgram) && debugConfigPath != null)
                                    {
                                        customProgram = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(debugConfigPath)!, customProgram));
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                SpanCoderDiagnostics.LogError($"[EngineHost] Error parsing spancoder_debug.json: {ex.Message}");
                            }
                        }

                        string executable = "";
                        string arguments = "";

                        if (debugType.Equals("silicon", StringComparison.OrdinalIgnoreCase))
                        {
                            string targetProgram = string.IsNullOrEmpty(customProgram) ? programPath : customProgram;
                            if (LspClient.IsCommandOnPath(gdbPath))
                            {
                                executable = gdbPath;
                                arguments = "--interpreter=dap";
                            }
                            else
                            {
                                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                                executable = Path.Combine(baseDir, "SpanCoder.Engine.exe");
                                if (!File.Exists(executable))
                                {
                                    executable = Path.Combine(baseDir, "SpanCoder.Engine");
                                }
                                arguments = "--mock-silicon-dap";
                                if (!File.Exists(executable))
                                {
                                    executable = "dotnet";
                                    string dllPath = Path.Combine(baseDir, "SpanCoder.Engine.dll");
                                    arguments = $"\"{dllPath}\" --mock-silicon-dap";
                                }
                            }
                            _dapClient.Start(executable, arguments, targetProgram);
                        }
                        else
                        {
                            if (LspClient.IsCommandOnPath("netcoredbg"))
                            {
                                executable = "netcoredbg";
                                arguments = "--interpreter=dap";
                            }
                            else
                            {
                                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                                executable = Path.Combine(baseDir, "SpanCoder.Engine.exe");
                                if (!File.Exists(executable))
                                {
                                    executable = Path.Combine(baseDir, "SpanCoder.Engine");
                                }
                                arguments = "--mock-dap";
                                if (!File.Exists(executable))
                                {
                                    executable = "dotnet";
                                    string dllPath = Path.Combine(baseDir, "SpanCoder.Engine.dll");
                                    arguments = $"\"{dllPath}\" --mock-dap";
                                }
                            }
                            _dapClient.Start(executable, arguments, programPath);
                        }
                        break;
                    }

                case MessageTypes.DebugStopRequest:
                    {
                        _dapClient?.Stop();
                        _dapClient?.Dispose();
                        _dapClient = null;
                        break;
                    }

                case MessageTypes.DebugStepOverRequest:
                    {
                        _dapClient?.StepOver();
                        break;
                    }

                case MessageTypes.DebugStepIntoRequest:
                    {
                        _dapClient?.StepInto();
                        break;
                    }

                case MessageTypes.DebugStepOutRequest:
                    {
                        _dapClient?.StepOut();
                        break;
                    }

                case MessageTypes.DebugContinueRequest:
                    {
                        _dapClient?.Continue();
                        break;
                    }

                case MessageTypes.DebugSetBreakpointsRequest:
                    {
                        var bps = BinaryMessageSerializer.ParseDebugSetBreakpointsRequest(message, out int docId);
                        if (_documents.TryGetValue(docId, out var doc))
                        {
                            _dapClient?.SetBreakpoints(doc.FilePath, bps);
                        }
                        break;
                    }
            }
        }
    }
}
