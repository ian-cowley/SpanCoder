using System;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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
            var syntaxTree = CSharpSyntaxTree.ParseText(text);
            var diagnostics = syntaxTree.GetDiagnostics();

            var diagnosticsList = new System.Text.StringBuilder();
            diagnosticsList.Append("[");
            bool first = true;

            foreach (var diag in diagnostics)
            {
                if (!first) diagnosticsList.Append(",");
                first = false;

                var lineSpan = diag.Location.GetLineSpan();
                int startLine = lineSpan.StartLinePosition.Line;
                int startChar = lineSpan.StartLinePosition.Character;
                int endLine = lineSpan.EndLinePosition.Line;
                int endChar = lineSpan.EndLinePosition.Character;
                int severity = diag.Severity switch
                {
                    DiagnosticSeverity.Error => 1,
                    DiagnosticSeverity.Warning => 2,
                    DiagnosticSeverity.Info => 3,
                    _ => 4
                };
                string msg = diag.GetMessage().Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", " ");
                diagnosticsList.Append($"{{\"range\":{{\"start\":{{\"line\":{startLine},\"character\":{startChar}}},\"end\":{{\"line\":{endLine},\"character\":{endChar}}}}},\"severity\":{severity},\"message\":\"{msg}\"}}");
            }

            var root = syntaxTree.GetCompilationUnitRoot();

            foreach (var token in root.DescendantTokens())
            {
                if (token.IsKind(SyntaxKind.IdentifierToken) && token.Text.Equals("error", StringComparison.OrdinalIgnoreCase))
                {
                    var span = token.GetLocation().GetLineSpan();
                    if (!first) diagnosticsList.Append(",");
                    first = false;
                    int startLine = span.StartLinePosition.Line;
                    int startChar = span.StartLinePosition.Character;
                    diagnosticsList.Append($"{{\"range\":{{\"start\":{{\"line\":{startLine},\"character\":{startChar}}},\"end\":{{\"line\":{startLine},\"character\":{startChar + 5}}}}},\"severity\":1,\"message\":\"Mock error description: Identifier 'error' unresolved\"}}");
                }
            }

            foreach (var trivia in root.DescendantTrivia())
            {
                if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia))
                {
                    string comment = trivia.ToString();
                    int todoIdx = comment.IndexOf("todo", StringComparison.OrdinalIgnoreCase);
                    if (todoIdx >= 0)
                    {
                        var lineSpan = trivia.GetLocation().GetLineSpan();
                        int line = lineSpan.StartLinePosition.Line;
                        int charStart = lineSpan.StartLinePosition.Character + todoIdx;
                        if (!first) diagnosticsList.Append(",");
                        first = false;
                        diagnosticsList.Append($"{{\"range\":{{\"start\":{{\"line\":{line},\"character\":{charStart}}},\"end\":{{\"line\":{line},\"character\":{charStart + 4}}}}},\"severity\":2,\"message\":\"TODO comment found\"}}");
                    }
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
                var syntaxTree = CSharpSyntaxTree.ParseText(docText);
                var root = syntaxTree.GetCompilationUnitRoot();

                foreach (var node in root.DescendantNodes())
                {
                    if (node is BaseTypeDeclarationSyntax typeDecl && typeDecl.Identifier.Text == word)
                    {
                        var span = typeDecl.Identifier.GetLocation().GetLineSpan();
                        string escapedUri = docUri.Replace("\\", "\\\\").Replace("\"", "\\\"");
                        int defLine = span.StartLinePosition.Line;
                        int defChar = span.StartLinePosition.Character;
                        return $"{{\"uri\":\"{escapedUri}\",\"range\":{{\"start\":{{\"line\":{defLine},\"character\":{defChar}}},\"end\":{{\"line\":{defLine},\"character\":{defChar + word.Length}}}}}}}";
                    }
                    if (node is MethodDeclarationSyntax methodDecl && methodDecl.Identifier.Text == word)
                    {
                        var span = methodDecl.Identifier.GetLocation().GetLineSpan();
                        string escapedUri = docUri.Replace("\\", "\\\\").Replace("\"", "\\\"");
                        int defLine = span.StartLinePosition.Line;
                        int defChar = span.StartLinePosition.Character;
                        return $"{{\"uri\":\"{escapedUri}\",\"range\":{{\"start\":{{\"line\":{defLine},\"character\":{defChar}}},\"end\":{{\"line\":{defLine},\"character\":{defChar + word.Length}}}}}}}";
                    }
                    if (node is PropertyDeclarationSyntax propDecl && propDecl.Identifier.Text == word)
                    {
                        var span = propDecl.Identifier.GetLocation().GetLineSpan();
                        string escapedUri = docUri.Replace("\\", "\\\\").Replace("\"", "\\\"");
                        int defLine = span.StartLinePosition.Line;
                        int defChar = span.StartLinePosition.Character;
                        return $"{{\"uri\":\"{escapedUri}\",\"range\":{{\"start\":{{\"line\":{defLine},\"character\":{defChar}}},\"end\":{{\"line\":{defLine},\"character\":{defChar + word.Length}}}}}}}";
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
                var syntaxTree = CSharpSyntaxTree.ParseText(docText);
                var root = syntaxTree.GetCompilationUnitRoot();

                foreach (var token in root.DescendantTokens())
                {
                    if (token.IsKind(SyntaxKind.IdentifierToken) && token.Text == word)
                    {
                        var span = token.GetLocation().GetLineSpan();
                        if (!first) sb.Append(",");
                        first = false;
                        string escapedUri = docUri.Replace("\\", "\\\\").Replace("\"", "\\\"");
                        int tokLine = span.StartLinePosition.Line;
                        int tokChar = span.StartLinePosition.Character;
                        sb.Append($"{{\"uri\":\"{escapedUri}\",\"range\":{{\"start\":{{\"line\":{tokLine},\"character\":{tokChar}}},\"end\":{{\"line\":{tokLine},\"character\":{tokChar + word.Length}}}}}}}");
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
                var syntaxTree = CSharpSyntaxTree.ParseText(docText);
                var root = syntaxTree.GetCompilationUnitRoot();

                var docEdits = new System.Collections.Generic.List<string>();
                foreach (var token in root.DescendantTokens())
                {
                    if (token.IsKind(SyntaxKind.IdentifierToken) && token.Text == word)
                    {
                        var span = token.GetLocation().GetLineSpan();
                        int tokLine = span.StartLinePosition.Line;
                        int tokChar = span.StartLinePosition.Character;
                        docEdits.Add($"{{\"range\":{{\"start\":{{\"line\":{tokLine},\"character\":{tokChar}}},\"end\":{{\"line\":{tokLine},\"character\":{tokChar + word.Length}}}}},\"newText\":\"{newName}\"}}");
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

            var syntaxTree = CSharpSyntaxTree.ParseText(text);
            var root = syntaxTree.GetCompilationUnitRoot();
            var sb = new System.Text.StringBuilder();
            sb.Append("[");
            bool first = true;

            foreach (var node in root.DescendantNodes())
            {
                if (node is BaseTypeDeclarationSyntax typeDecl)
                {
                    if (!first) sb.Append(",");
                    first = false;
                    var span = typeDecl.Identifier.GetLocation().GetLineSpan();
                    string kind = typeDecl.Kind().ToString().Replace("Declaration", "").ToLowerInvariant();
                    int line = span.StartLinePosition.Line;
                    int charIdx = span.StartLinePosition.Character;
                    int len = typeDecl.Identifier.Text.Length;
                    sb.Append($"{{\"name\":\"{typeDecl.Identifier.Text}\",\"detail\":\"{kind}\",\"range\":{{\"start\":{{\"line\":{line},\"character\":{charIdx}}},\"end\":{{\"line\":{line},\"character\":{charIdx + len}}}}}}}");
                }
                else if (node is MethodDeclarationSyntax methodDecl)
                {
                    if (!first) sb.Append(",");
                    first = false;
                    var span = methodDecl.Identifier.GetLocation().GetLineSpan();
                    string detail = methodDecl.ReturnType.ToString();
                    int line = span.StartLinePosition.Line;
                    int charIdx = span.StartLinePosition.Character;
                    int len = methodDecl.Identifier.Text.Length;
                    sb.Append($"{{\"name\":\"{methodDecl.Identifier.Text}\",\"detail\":\"{detail}\",\"range\":{{\"start\":{{\"line\":{line},\"character\":{charIdx}}},\"end\":{{\"line\":{line},\"character\":{charIdx + len}}}}}}}");
                }
                else if (node is PropertyDeclarationSyntax propDecl)
                {
                    if (!first) sb.Append(",");
                    first = false;
                    var span = propDecl.Identifier.GetLocation().GetLineSpan();
                    string detail = propDecl.Type.ToString();
                    int line = span.StartLinePosition.Line;
                    int charIdx = span.StartLinePosition.Character;
                    int len = propDecl.Identifier.Text.Length;
                    sb.Append($"{{\"name\":\"{propDecl.Identifier.Text}\",\"detail\":\"{detail}\",\"range\":{{\"start\":{{\"line\":{line},\"character\":{charIdx}}},\"end\":{{\"line\":{line},\"character\":{charIdx + len}}}}}}}");
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
