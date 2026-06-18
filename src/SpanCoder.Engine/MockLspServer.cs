using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace SpanCoder.Engine
{
    public class MockLspServer
    {
        private static readonly object _writeLock = new object();

        private static void Log(string message)
        {
        }

        public static void Run()
        {
            Log("Mock LSP Server starting up...");
            var stdin = Console.OpenStandardInput();
            var stdout = Console.OpenStandardOutput();

            byte[] headerLineBuffer = new byte[1024];
            var documents = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();

            try
            {
                while (true)
                {
                    int lineLen = ReadLineBytes(stdin, headerLineBuffer);
                    if (lineLen <= 0) break;

                    string header = Encoding.ASCII.GetString(headerLineBuffer, 0, lineLen);
                    if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    {
                        int contentLength = int.Parse(header.Substring("Content-Length:".Length).Trim());
                        Log($"Content-Length parsed: {contentLength}");

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
                            if (read <= 0)
                            {
                                Log($"Read returned <= 0, totalRead={totalRead}");
                                break;
                            }
                            totalRead += read;
                        }

                        if (totalRead == contentLength)
                        {
                            Log($"Successfully read body of {contentLength} bytes");
                            ProcessMessage(body, documents, stdout);
                        }
                        else
                        {
                            Log($"Failed to read full body: read {totalRead} of {contentLength}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MockLspServer] Error: {ex}");
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

        private static void ProcessMessage(byte[] body, System.Collections.Concurrent.ConcurrentDictionary<string, string> documents, Stream stdout)
        {
            using var jsonDoc = JsonDocument.Parse(body);
            var root = jsonDoc.RootElement;

            Log($"ProcessMessage: body = {Encoding.UTF8.GetString(body)}");
            if (root.TryGetProperty("method", out var methodProp))
            {
                string method = methodProp.GetString() ?? "";
                Log($"ProcessMessage method: {method}");
                if (method == "initialize")
                {
                    int id = root.GetProperty("id").GetInt32();
                    Log($"initialize request ID: {id}");
                    SendResponse(stdout, id, "{\"capabilities\":{\"completionProvider\":{},\"hoverProvider\":true}}");
                }
                else if (method == "textDocument/didOpen")
                {
                    var paramsEl = root.GetProperty("params");
                    var textDocument = paramsEl.GetProperty("textDocument");
                    string uri = textDocument.GetProperty("uri").GetString() ?? "";
                    string text = textDocument.GetProperty("text").GetString() ?? "";
                    documents[uri] = text;

                    Log($"didOpen URI: {uri}");
                    PublishDiagnostics(stdout, uri, text);
                }
                else if (method == "textDocument/didChange")
                {
                    var paramsEl = root.GetProperty("params");
                    var textDocument = paramsEl.GetProperty("textDocument");
                    string uri = textDocument.GetProperty("uri").GetString() ?? "";
                    var contentChanges = paramsEl.GetProperty("contentChanges");
                    if (contentChanges.GetArrayLength() > 0)
                    {
                        string text = contentChanges[0].GetProperty("text").GetString() ?? "";
                        documents[uri] = text;
                        Log($"didChange URI: {uri}");
                        PublishDiagnostics(stdout, uri, text);
                    }
                }
            }
            else if (root.TryGetProperty("id", out var idProp))
            {
                if (root.TryGetProperty("method", out var requestMethodProp))
                {
                    int id = idProp.GetInt32();
                    string method = requestMethodProp.GetString() ?? "";
                    var paramsEl = root.GetProperty("params");
                    var textDocument = paramsEl.GetProperty("textDocument");
                    string uri = textDocument.GetProperty("uri").GetString() ?? "";
                    Log($"ProcessMessage request method: {method}, id: {id}");

                    if (method == "textDocument/completion")
                    {
                        var position = paramsEl.GetProperty("position");
                        int line = position.GetProperty("line").GetInt32();
                        int character = position.GetProperty("character").GetInt32();

                        documents.TryGetValue(uri, out string? text);
                        var completionsJson = GetMockCompletions(text, line, character);
                        SendResponse(stdout, id, completionsJson);
                    }
                    else if (method == "textDocument/hover")
                    {
                        var position = paramsEl.GetProperty("position");
                        int line = position.GetProperty("line").GetInt32();
                        int character = position.GetProperty("character").GetInt32();

                        documents.TryGetValue(uri, out string? text);
                        var hoverJson = GetMockHover(text, line, character);
                        SendResponse(stdout, id, hoverJson);
                    }
                    else if (method == "textDocument/definition")
                    {
                        var position = paramsEl.GetProperty("position");
                        int line = position.GetProperty("line").GetInt32();
                        int character = position.GetProperty("character").GetInt32();

                        documents.TryGetValue(uri, out string? text);
                        var definitionJson = GetMockDefinition(documents, uri, text, line, character);
                        SendResponse(stdout, id, definitionJson);
                    }
                    else if (method == "textDocument/references")
                    {
                        var position = paramsEl.GetProperty("position");
                        int line = position.GetProperty("line").GetInt32();
                        int character = position.GetProperty("character").GetInt32();

                        documents.TryGetValue(uri, out string? text);
                        var referencesJson = GetMockReferences(documents, uri, text, line, character);
                        SendResponse(stdout, id, referencesJson);
                    }
                    else if (method == "textDocument/rename")
                    {
                        var position = paramsEl.GetProperty("position");
                        int line = position.GetProperty("line").GetInt32();
                        int character = position.GetProperty("character").GetInt32();
                        string newName = paramsEl.GetProperty("newName").GetString() ?? "";

                        documents.TryGetValue(uri, out string? text);
                        var renameJson = GetMockRename(documents, uri, text, line, character, newName);
                        SendResponse(stdout, id, renameJson);
                    }
                    else if (method == "textDocument/documentSymbol")
                    {
                        documents.TryGetValue(uri, out string? text);
                        var symbolsJson = GetMockDocumentSymbols(uri, text);
                        SendResponse(stdout, id, symbolsJson);
                    }
                    else if (method == "textDocument/foldingRange")
                    {
                        documents.TryGetValue(uri, out string? text);
                        var foldingJson = GetMockFoldingRanges(uri, text);
                        SendResponse(stdout, id, foldingJson);
                    }
                }
            }
        }

        private static void PublishDiagnostics(Stream stdout, string uri, string text)
        {
            var diagnosticsList = new System.Text.StringBuilder();
            diagnosticsList.Append("[");

            string[] lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            bool first = true;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];

                int todoIdx = line.IndexOf("todo", StringComparison.OrdinalIgnoreCase);
                if (todoIdx >= 0)
                {
                    if (!first) diagnosticsList.Append(",");
                    first = false;
                    diagnosticsList.Append($"{{\"range\":{{\"start\":{{\"line\":{i},\"character\":{todoIdx}}},\"end\":{{\"line\":{i},\"character\":{todoIdx + 4}}}}},\"severity\":2,\"message\":\"TODO comment found\"}}");
                }

                if (line.Trim().StartsWith("var ", StringComparison.Ordinal) && !line.Trim().EndsWith(";", StringComparison.Ordinal))
                {
                    if (!first) diagnosticsList.Append(",");
                    first = false;
                    int lineLen = line.Length;
                    diagnosticsList.Append($"{{\"range\":{{\"start\":{{\"line\":{i},\"character\":0}},\"end\":{{\"line\":{i},\"character\":{lineLen}}}}},\"severity\":1,\"message\":\"; expected\"}}");
                }

                int errIdx = line.IndexOf("error", StringComparison.OrdinalIgnoreCase);
                if (errIdx >= 0 && !line.Contains("errorDescription", StringComparison.OrdinalIgnoreCase) && !line.Contains("ErrorMock", StringComparison.OrdinalIgnoreCase))
                {
                    if (!first) diagnosticsList.Append(",");
                    first = false;
                    diagnosticsList.Append($"{{\"range\":{{\"start\":{{\"line\":{i},\"character\":{errIdx}}},\"end\":{{\"line\":{i},\"character\":{errIdx + 5}}}}},\"severity\":1,\"message\":\"Mock error description\"}}");
                }
            }

            diagnosticsList.Append("]");

            string notificationJson = $"{{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/publishDiagnostics\",\"params\":{{\"uri\":\"{uri}\",\"diagnostics\":{diagnosticsList.ToString()}}}}}";
            SendNotification(stdout, notificationJson);
        }

        private static string GetMockCompletions(string? text, int line, int character)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "{\"items\":[]}";
            }

            string[] lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            if (line >= lines.Length) return "{\"items\":[]}";
            string currentLine = lines[line];
            string prefix = currentLine.Substring(0, Math.Min(character, currentLine.Length));

            var items = new System.Text.StringBuilder();
            items.Append("[");

            if (prefix.EndsWith("System.", StringComparison.Ordinal))
            {
                items.Append("{\"label\":\"Console\",\"detail\":\"class System.Console\"},");
                items.Append("{\"label\":\"Diagnostics\",\"detail\":\"namespace System.Diagnostics\"},");
                items.Append("{\"label\":\"IO\",\"detail\":\"namespace System.IO\"},");
                items.Append("{\"label\":\"Text\",\"detail\":\"namespace System.Text\"}");
            }
            else if (prefix.EndsWith("Console.", StringComparison.Ordinal))
            {
                items.Append("{\"label\":\"WriteLine\",\"detail\":\"void Console.WriteLine(string value)\"},");
                items.Append("{\"label\":\"Write\",\"detail\":\"void Console.Write(string value)\"},");
                items.Append("{\"label\":\"ReadLine\",\"detail\":\"string Console.ReadLine()\"},");
                items.Append("{\"label\":\"Clear\",\"detail\":\"void Console.Clear()\"}");
            }
            else
            {
                items.Append("{\"label\":\"using\",\"detail\":\"keyword using\"},");
                items.Append("{\"label\":\"namespace\",\"detail\":\"keyword namespace\"},");
                items.Append("{\"label\":\"class\",\"detail\":\"keyword class\"},");
                items.Append("{\"label\":\"public\",\"detail\":\"keyword public\"},");
                items.Append("{\"label\":\"void\",\"detail\":\"keyword void\"},");
                items.Append("{\"label\":\"string\",\"detail\":\"keyword string\"},");
                items.Append("{\"label\":\"int\",\"detail\":\"keyword int\"}");
            }

            items.Append("]");
            return $"{{\"items\":{items.ToString()}}}";
        }

        private static string GetMockHover(string? text, int line, int character)
        {
            if (string.IsNullOrEmpty(text)) return "{\"contents\":\"\"}";

            string[] lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            if (line >= lines.Length) return "{\"contents\":\"\"}";
            string currentLine = lines[line];

            int start = character;
            while (start > 0 && char.IsLetterOrDigit(currentLine[start - 1])) start--;
            int end = character;
            while (end < currentLine.Length && char.IsLetterOrDigit(currentLine[end])) end++;

            if (start == end) return "{\"contents\":\"\"}";
            string word = currentLine.Substring(start, end - start);

            string contents = "";
            if (word == "Console")
            {
                contents = "**class System.Console**\\n\\nProvides standard input, output, and error streams for console applications.";
            }
            else if (word == "WriteLine")
            {
                contents = "**void Console.WriteLine(string? value)**\\n\\nWrites the specified string value, followed by the current line terminator, to the standard output stream.";
            }
            else if (word == "using")
            {
                contents = "**keyword using**\\n\\nImports namespaces or defines a using statement/expression.";
            }
            else
            {
                contents = $"**{word}**\\n\\nMock documentation for symbol '{word}'.";
            }

            return $"{{\"contents\":\"{contents}\",\"range\":{{\"start\":{{\"line\":{line},\"character\":{start}}},\"end\":{{\"line\":{line},\"character\":{end}}}}}}}";
        }

        private static string GetMockDefinition(System.Collections.Concurrent.ConcurrentDictionary<string, string> documents, string uri, string? text, int line, int character)
        {
            if (string.IsNullOrEmpty(text)) return "null";

            string[] lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            if (line >= lines.Length) return "null";
            string currentLine = lines[line];

            int start = character;
            while (start > 0 && char.IsLetterOrDigit(currentLine[start - 1])) start--;
            int end = character;
            while (end < currentLine.Length && char.IsLetterOrDigit(currentLine[end])) end++;

            if (start == end) return "null";
            string word = currentLine.Substring(start, end - start);

            foreach (var kvp in documents)
            {
                string docUri = kvp.Key;
                string docText = kvp.Value;
                string[] docLines = docText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                for (int i = 0; i < docLines.Length; i++)
                {
                    string l = docLines[i];
                    int idx = l.IndexOf(word);
                    if (idx >= 0)
                    {
                        // Check for type definition: class, struct, interface, enum, record
                        bool isTypeDecl = false;
                        string[] typeKeywords = { "class", "struct", "interface", "enum", "record" };
                        foreach (var kw in typeKeywords)
                        {
                            int kwIdx = l.IndexOf(kw);
                            if (kwIdx >= 0 && kwIdx < idx)
                            {
                                string sub = l.Substring(kwIdx + kw.Length, idx - (kwIdx + kw.Length)).Trim();
                                if (sub.Length == 0)
                                {
                                    isTypeDecl = true;
                                    break;
                                }
                            }
                        }

                        // Check for method declaration: "void Word", "int Word", or generally ending with "("
                        bool isMethodDecl = false;
                        int afterWord = idx + word.Length;
                        while (afterWord < l.Length && char.IsWhiteSpace(l[afterWord])) afterWord++;
                        if (afterWord < l.Length && l[afterWord] == '(')
                        {
                            if (idx == 0 || l[idx - 1] != '.')
                            {
                                isMethodDecl = true;
                            }
                        }

                        if (isTypeDecl || isMethodDecl)
                        {
                            string escapedUri = docUri.Replace("\\", "\\\\").Replace("\"", "\\\"");
                            return $"{{\"uri\":\"{escapedUri}\",\"range\":{{\"start\":{{\"line\":{i},\"character\":{idx}}},\"end\":{{\"line\":{i},\"character\":{idx + word.Length}}}}}}}";
                        }
                    }
                }
            }

            return "null";
        }

        private static string GetMockReferences(System.Collections.Concurrent.ConcurrentDictionary<string, string> documents, string uri, string? text, int line, int character)
        {
            if (string.IsNullOrEmpty(text)) return "[]";

            string[] lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            if (line >= lines.Length) return "[]";
            string currentLine = lines[line];

            int start = character;
            while (start > 0 && char.IsLetterOrDigit(currentLine[start - 1])) start--;
            int end = character;
            while (end < currentLine.Length && char.IsLetterOrDigit(currentLine[end])) end++;

            if (start == end) return "[]";
            string word = currentLine.Substring(start, end - start);

            var sb = new System.Text.StringBuilder();
            sb.Append("[");
            bool first = true;

            foreach (var kvp in documents)
            {
                string docUri = kvp.Key;
                string docText = kvp.Value;
                string[] docLines = docText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                for (int i = 0; i < docLines.Length; i++)
                {
                    string l = docLines[i];
                    int idx = 0;
                    while ((idx = l.IndexOf(word, idx)) >= 0)
                    {
                        bool beforeOk = (idx == 0 || !char.IsLetterOrDigit(l[idx - 1]));
                        bool afterOk = (idx + word.Length == l.Length || !char.IsLetterOrDigit(l[idx + word.Length]));
                        
                        if (beforeOk && afterOk)
                        {
                            if (!first) sb.Append(",");
                            first = false;
                            string escapedUri = docUri.Replace("\\", "\\\\").Replace("\"", "\\\"");
                            sb.Append($"{{\"uri\":\"{escapedUri}\",\"range\":{{\"start\":{{\"line\":{i},\"character\":{idx}}},\"end\":{{\"line\":{i},\"character\":{idx + word.Length}}}}}}}");
                        }
                        idx += word.Length;
                    }
                }
            }

            sb.Append("]");
            return sb.ToString();
        }

        private static string GetMockRename(System.Collections.Concurrent.ConcurrentDictionary<string, string> documents, string uri, string? text, int line, int character, string newName)
        {
            if (string.IsNullOrEmpty(text)) return "{\"changes\":{}}";

            string[] lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            if (line >= lines.Length) return "{\"changes\":{}}";
            string currentLine = lines[line];

            int start = character;
            while (start > 0 && char.IsLetterOrDigit(currentLine[start - 1])) start--;
            int end = character;
            while (end < currentLine.Length && char.IsLetterOrDigit(currentLine[end])) end++;

            if (start == end) return "{\"changes\":{}}";
            string word = currentLine.Substring(start, end - start);

            var sb = new System.Text.StringBuilder();
            sb.Append("{\"changes\":{");
            bool firstDoc = true;

            foreach (var kvp in documents)
            {
                string docUri = kvp.Key;
                string docText = kvp.Value;
                string[] docLines = docText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                
                var docEdits = new System.Collections.Generic.List<string>();
                for (int i = 0; i < docLines.Length; i++)
                {
                    string l = docLines[i];
                    int idx = 0;
                    while ((idx = l.IndexOf(word, idx)) >= 0)
                    {
                        bool beforeOk = (idx == 0 || !char.IsLetterOrDigit(l[idx - 1]));
                        bool afterOk = (idx + word.Length == l.Length || !char.IsLetterOrDigit(l[idx + word.Length]));
                        
                        if (beforeOk && afterOk)
                        {
                            docEdits.Add($"{{\"range\":{{\"start\":{{\"line\":{i},\"character\":{idx}}},\"end\":{{\"line\":{i},\"character\":{idx + word.Length}}}}},\"newText\":\"{newName}\"}}");
                        }
                        idx += word.Length;
                    }
                }

                if (docEdits.Count > 0)
                {
                    if (!firstDoc) sb.Append(",");
                    firstDoc = false;
                    string escapedUri = docUri.Replace("\\", "\\\\").Replace("\"", "\\\"");
                    sb.Append($"\"{escapedUri}\":[{string.Join(",", docEdits)}]");
                }
            }

            sb.Append("}}");
            return sb.ToString();
        }

        private static string GetMockDocumentSymbols(string uri, string? text)
        {
            if (string.IsNullOrEmpty(text)) return "[]";

            string[] lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var sb = new System.Text.StringBuilder();
            sb.Append("[");
            bool first = true;

            for (int i = 0; i < lines.Length; i++)
            {
                string l = lines[i];
                string trimmed = l.Trim();
                
                bool isDecl = false;
                string kind = "";
                string name = "";
                
                string[] keywords = { "class", "struct", "interface", "enum", "void", "string", "int", "Task", "bool" };
                foreach (var kw in keywords)
                {
                    int kwIdx = l.IndexOf(kw + " ");
                    if (kwIdx >= 0)
                    {
                        int startName = kwIdx + kw.Length + 1;
                        while (startName < l.Length && char.IsWhiteSpace(l[startName])) startName++;
                        int endName = startName;
                        while (endName < l.Length && (char.IsLetterOrDigit(l[endName]) || l[endName] == '_')) endName++;
                        
                        if (endName > startName)
                        {
                            name = l.Substring(startName, endName - startName);
                            if (Array.IndexOf(keywords, name) < 0)
                            {
                                isDecl = true;
                                kind = kw;
                                break;
                            }
                        }
                    }
                }

                if (isDecl && !string.IsNullOrEmpty(name))
                {
                    if (!first) sb.Append(",");
                    first = false;
                    
                    int idx = l.IndexOf(name);
                    string detail = kind;
                    sb.Append($"{{\"name\":\"{name}\",\"detail\":\"{detail}\",\"range\":{{\"start\":{{\"line\":{i},\"character\":{idx}}},\"end\":{{\"line\":{i},\"character\":{idx + name.Length}}}}}}}");
                }
            }

            sb.Append("]");
            return sb.ToString();
        }

        private static string GetMockFoldingRanges(string uri, string? text)
        {
            if (string.IsNullOrEmpty(text)) return "[]";

            var list = new System.Collections.Generic.List<string>();
            var stack = new System.Collections.Generic.Stack<int>();
            string[] lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            for (int i = 0; i < lines.Length; i++)
            {
                string lineText = lines[i];
                for (int j = 0; j < lineText.Length; j++)
                {
                    if (lineText[j] == '{')
                    {
                        stack.Push(i);
                    }
                    else if (lineText[j] == '}')
                    {
                        if (stack.Count > 0)
                        {
                            int start = stack.Pop();
                            if (i > start)
                            {
                                list.Add($"{{\"startLine\":{start},\"endLine\":{i}}}");
                            }
                        }
                    }
                }
            }
            return "[" + string.Join(",", list) + "]";
        }

        private static void SendResponse(Stream stdout, int id, string resultJson)
        {
            string json = $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{resultJson}}}";
            WritePayload(stdout, json);
        }

        private static void SendNotification(Stream stdout, string json)
        {
            WritePayload(stdout, json);
        }

        private static void WritePayload(Stream stdout, string json)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            string headers = $"Content-Length: {bytes.Length}\r\n\r\n";
            byte[] headerBytes = Encoding.ASCII.GetBytes(headers);

            lock (_writeLock)
            {
                stdout.Write(headerBytes, 0, headerBytes.Length);
                stdout.Write(bytes, 0, bytes.Length);
                stdout.Flush();
            }
        }
    }
}
