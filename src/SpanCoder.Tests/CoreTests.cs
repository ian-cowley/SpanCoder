using System;
using System.IO;
using Xunit;
using SpanCoder.Contracts;
using SpanCoder.Engine;
using SpanCoder.Shell;
using Avalonia.Headless.XUnit;

namespace SpanCoder.Tests
{
    public class CoreTests
    {
        [Fact]
        public void TestPieceTableBasic()
        {
            var initial = "Hello World".AsMemory();
            using var pt = new PieceTable(initial);

            Assert.Equal(11, pt.Length);

            // Get initial text
            char[] buffer = new char[11];
            pt.GetText(0, 11, buffer);
            Assert.Equal("Hello World", new string(buffer));

            // Insert " Awesome" at 5
            pt.Insert(5, " Awesome");
            Assert.Equal(19, pt.Length);

            char[] buffer2 = new char[19];
            pt.GetText(0, 19, buffer2);
            Assert.Equal("Hello Awesome World", new string(buffer2));

            // Delete " Awesome"
            pt.Delete(5, 8);
            Assert.Equal(11, pt.Length);

            char[] buffer3 = new char[11];
            pt.GetText(0, 11, buffer3);
            Assert.Equal("Hello World", new string(buffer3));
        }

        [Fact]
        public void TestPieceTableSlicing()
        {
            var initial = "Hello World".AsMemory();
            using var pt = new PieceTable(initial);

            // Contiguous query
            var span = pt.GetContiguousSpan(0, 5, out bool isContiguous);
            Assert.True(isContiguous);
            Assert.Equal("Hello", span.ToString());

            // Insert to split
            pt.Insert(5, "!");
            
            // Query crossing split (should not be contiguous)
            var span2 = pt.GetContiguousSpan(4, 3, out bool isContiguous2);
            Assert.False(isContiguous2);

            // Query fully inside the split piece
            var span3 = pt.GetContiguousSpan(5, 1, out bool isContiguous3);
            Assert.True(isContiguous3);
            Assert.Equal("!", span3.ToString());
        }

        [Fact]
        public void TestLineIndexBasic()
        {
            var text = "Line1\nLine2\nLine3";
            var lineIndex = new LineIndex();
            lineIndex.Initialize(text.AsSpan());

            Assert.Equal(3, lineIndex.Count);
            Assert.Equal(0, lineIndex.GetLineStart(0));
            Assert.Equal(6, lineIndex.GetLineStart(1));
            Assert.Equal(12, lineIndex.GetLineStart(2));

            // Test GetLineIndexFromOffset
            Assert.Equal(0, lineIndex.GetLineIndexFromOffset(3));
            Assert.Equal(1, lineIndex.GetLineIndexFromOffset(6));
            Assert.Equal(1, lineIndex.GetLineIndexFromOffset(8));
            Assert.Equal(2, lineIndex.GetLineIndexFromOffset(15));
        }

        [Fact]
        public void TestLineIndexInsertNoNewlines()
        {
            var text = "Line1\nLine2\nLine3";
            var lineIndex = new LineIndex();
            lineIndex.Initialize(text.AsSpan());

            // Insert "abc" (length 3) at offset 2 (inside line 0)
            lineIndex.Insert(2, 3, "abc");

            Assert.Equal(3, lineIndex.Count);
            Assert.Equal(0, lineIndex.GetLineStart(0));
            Assert.Equal(9, lineIndex.GetLineStart(1)); // shifted by 3
            Assert.Equal(15, lineIndex.GetLineStart(2)); // shifted by 3
        }

        [Fact]
        public void TestLineIndexInsertWithNewlines()
        {
            var text = "Line1\nLine2\nLine3";
            var lineIndex = new LineIndex();
            lineIndex.Initialize(text.AsSpan());

            // Insert "\nNewLine\n" (length 9) at offset 2
            lineIndex.Insert(2, 9, "\nNewLine\n");

            // Original: 0, 6, 12
            // Insert at 2. Anything > 2 is shifted by 9.
            // Shifts: 6 -> 15, 12 -> 21
            // New newlines in "\nNewLine\n":
            // Newline 1 at rel index 0 -> abs offset = 2 + 0 + 1 = 3
            // Newline 2 at rel index 8 -> abs offset = 2 + 8 + 1 = 11
            // Final offsets should be: 0, 3, 11, 15, 21

            Assert.Equal(5, lineIndex.Count);
            Assert.Equal(0, lineIndex.GetLineStart(0));
            Assert.Equal(3, lineIndex.GetLineStart(1));
            Assert.Equal(11, lineIndex.GetLineStart(2));
            Assert.Equal(15, lineIndex.GetLineStart(3));
            Assert.Equal(21, lineIndex.GetLineStart(4));
        }

        [Fact]
        public void TestLineIndexDelete()
        {
            var text = "Line1\nLine2\nLine3";
            var lineIndex = new LineIndex();
            lineIndex.Initialize(text.AsSpan());

            // Delete 7 characters starting at offset 4 (deletes "\nLine2")
            // Range (4, 11]
            // Line starts inside: 6 is deleted.
            // Shifting offset 12 by -7 -> 5.
            // Result offsets should be: 0, 5.
            lineIndex.Delete(4, 7);

            Assert.Equal(2, lineIndex.Count);
            Assert.Equal(0, lineIndex.GetLineStart(0));
            Assert.Equal(5, lineIndex.GetLineStart(1));
        }

        [Fact]
        public void TestBinaryMessageSerializer()
        {
            byte[] buffer = new byte[1024];

            // 1. Insert Text
            int len = BinaryMessageSerializer.WriteInsertText(buffer, 42, 10, "Hello");
            Assert.True(BinaryMessageSerializer.TryParseHeader(buffer.AsSpan(0, len), out var header));
            Assert.Equal(MessageTypes.InsertText, header.Type);
            Assert.Equal(42, header.DocumentId);
            Assert.Equal(10, header.Offset);
            Assert.Equal(len, header.Length);

            var text = BinaryMessageSerializer.ParseInsertText(buffer.AsSpan(0, len), out int docId, out int offset);
            Assert.Equal(42, docId);
            Assert.Equal(10, offset);
            Assert.Equal("Hello", text.ToString());

            // 2. Delete Text
            len = BinaryMessageSerializer.WriteDeleteText(buffer, 24, 15, 5);
            Assert.True(BinaryMessageSerializer.TryParseHeader(buffer.AsSpan(0, len), out header));
            Assert.Equal(MessageTypes.DeleteText, header.Type);
            Assert.Equal(24, header.DocumentId);
            Assert.Equal(15, header.Offset);

            int delLen = BinaryMessageSerializer.ParseDeleteText(buffer.AsSpan(0, len), out docId, out offset);
            Assert.Equal(24, docId);
            Assert.Equal(15, offset);
            Assert.Equal(5, delLen);

            // 3. Load File
            len = BinaryMessageSerializer.WriteLoadFile(buffer, "test_file.txt");
            Assert.True(BinaryMessageSerializer.TryParseHeader(buffer.AsSpan(0, len), out header));
            Assert.Equal(MessageTypes.LoadFile, header.Type);

            var filePath = BinaryMessageSerializer.ParseLoadFile(buffer.AsSpan(0, len));
            Assert.Equal("test_file.txt", filePath.ToString());

            // 4. Document Changed
            len = BinaryMessageSerializer.WriteDocumentChanged(buffer, 99, 100, 20, 10, "12345678901234567890");
            Assert.True(BinaryMessageSerializer.TryParseHeader(buffer.AsSpan(0, len), out header));
            Assert.Equal(MessageTypes.DocumentChanged, header.Type);

            var insertedText = BinaryMessageSerializer.ParseDocumentChanged(buffer.AsSpan(0, len), out docId, out offset, out int added, out int deleted);
            Assert.Equal(99, docId);
            Assert.Equal(100, offset);
            Assert.Equal(20, added);
            Assert.Equal(10, deleted);
            Assert.Equal("12345678901234567890", insertedText.ToString());
        }

        [Fact]
        public void TestLargeFilePerformance()
        {
            string tempFile = Path.Combine(Path.GetTempPath(), "large_test_file.log");
            try
            {
                using (var sw = new StreamWriter(tempFile, false, System.Text.Encoding.UTF8))
                {
                    for (int i = 0; i < 1_000_000; i++)
                    {
                        sw.WriteLine("2026-06-16 21:31:42 [INFO] This is a dummy log line repeating to test editor performance and memory footprint.");
                    }
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();
                long memBefore = GC.GetTotalMemory(true);

                var swWatch = System.Diagnostics.Stopwatch.StartNew();

                using var fs = new FileStream(tempFile, FileMode.Open, FileAccess.Read);
                long fileLength = fs.Length;
                using var sr = new StreamReader(fs, System.Text.Encoding.UTF8);
                char[] buffer = new char[fileLength];
                int read = sr.ReadBlock(buffer, 0, (int)fileLength);
                var originalText = new ReadOnlyMemory<char>(buffer, 0, read);

                using var doc = new Document(1, originalText);

                swWatch.Stop();
                long memAfter = GC.GetTotalMemory(true);
                long allocated = memAfter - memBefore;

                Console.WriteLine($"[Benchmark] Loaded and indexed {fileLength / (1024.0 * 1024.0):F2}MB file.");
                Console.WriteLine($"[Benchmark] Line count: {doc.GetLineCount()}");
                Console.WriteLine($"[Benchmark] Time taken: {swWatch.ElapsedMilliseconds}ms");
                Console.WriteLine($"[Benchmark] GC memory difference: {allocated / (1024.0 * 1024.0):F2}MB");

                Assert.True(swWatch.ElapsedMilliseconds < 1500, $"Loading took too long: {swWatch.ElapsedMilliseconds}ms");
            }
            finally
            {
                if (File.Exists(tempFile))
                    File.Delete(tempFile);
            }
        }

        [AvaloniaFact]
        public async Task TestIpcEngineConnectionRecovery()
        {
            using var connection = new SpanCoder.App.IpcEngineConnection();
            connection.Start();

            bool receivedLoadResponse = false;
            int finalDocId = -1;

            connection.MessageReceived += (msg) =>
            {
                if (BinaryMessageSerializer.TryParseHeader(msg, out var header))
                {
                    if (header.Type == MessageTypes.DocumentChanged)
                    {
                        var text = BinaryMessageSerializer.ParseDocumentChanged(msg, out int docId, out int offset, out int addedLength, out int deletedLength);
                        finalDocId = docId;
                        receivedLoadResponse = true;
                    }
                }
            };

            string tempFile = Path.Combine(Path.GetTempPath(), "ipc_recovery_test.txt");
            File.WriteAllText(tempFile, "Hello World\nLine 2");

            try
            {
                byte[] loadMsg = new byte[BinaryMessageSerializer.HeaderSize + 4 + tempFile.Length * 2];
                BinaryMessageSerializer.WriteLoadFile(loadMsg, tempFile);
                connection.Send(loadMsg);

                int retries = 0;
                while (!receivedLoadResponse && retries++ < 50)
                {
                    await Task.Delay(100);
                }
                Assert.True(receivedLoadResponse, "Load file response not received");
                Assert.True(finalDocId > 0);

                receivedLoadResponse = false;
                string insertText = " Beautiful";
                byte[] insertMsg = new byte[BinaryMessageSerializer.HeaderSize + 4 + insertText.Length * 2];
                BinaryMessageSerializer.WriteInsertText(insertMsg, finalDocId, 5, insertText);
                connection.Send(insertMsg);

                retries = 0;
                while (!receivedLoadResponse && retries++ < 50)
                {
                    await Task.Delay(100);
                }
                Assert.True(receivedLoadResponse, "Insert response not received");

                Document? doc = null;
                retries = 0;
                while (doc == null && retries++ < 50)
                {
                    doc = connection.GetDocument(finalDocId) as Document;
                    if (doc == null) await Task.Delay(50);
                }
                Assert.NotNull(doc);
                char[] textBuf = new char[doc.Length];
                doc.PieceTable.GetText(0, doc.Length, textBuf);
                Assert.Equal("Hello Beautiful World\nLine 2", new string(textBuf));

                // Simulate engine crash
                receivedLoadResponse = false;
                connection.SimulateEngineCrash();

                // Wait for automatic recovery and replay
                retries = 0;
                while (!receivedLoadResponse && retries++ < 80)
                {
                    await Task.Delay(100);
                }
                Assert.True(receivedLoadResponse, "Reconnection load response not received after crash");

                Document? docAfter = null;
                retries = 0;
                while (docAfter == null && retries++ < 50)
                {
                    docAfter = connection.GetDocument(finalDocId) as Document;
                    if (docAfter == null) await Task.Delay(50);
                }
                Assert.NotNull(docAfter);

                char[] textBufAfter = new char[docAfter.Length];
                docAfter.PieceTable.GetText(0, docAfter.Length, textBufAfter);
                Assert.Equal("Hello Beautiful World\nLine 2", new string(textBufAfter));
            }
            finally
            {
                if (File.Exists(tempFile))
                    File.Delete(tempFile);
            }
        }

        [Fact]
        public void TestDocumentFilePath()
        {
            var text = "Hello World".AsMemory();
            using var doc = new Document(42, text, "my_custom_path.txt");
            Assert.Equal(42, doc.Id);
            Assert.Equal("my_custom_path.txt", doc.FilePath);
        }

        [Fact]
        public void TestLspBinaryMessagesSerialization()
        {
            byte[] buffer = new byte[1024];

            // 1. Diagnostics Report
            var items = new[]
            {
                new DiagnosticItem { StartOffset = 5, EndOffset = 10, Severity = 1, Message = "Test Error" }
            };
            int len = BinaryMessageSerializer.WriteDiagnosticsReport(buffer, 12, items);
            Assert.True(BinaryMessageSerializer.TryParseHeader(buffer.AsSpan(0, len), out var header));
            Assert.Equal(MessageTypes.DiagnosticsReport, header.Type);
            Assert.Equal(12, header.DocumentId);

            var parsedItems = BinaryMessageSerializer.ParseDiagnosticsReport(buffer.AsSpan(0, len), out int docId);
            Assert.Equal(12, docId);
            Assert.Single(parsedItems);
            Assert.Equal(5, parsedItems[0].StartOffset);
            Assert.Equal(10, parsedItems[0].EndOffset);
            Assert.Equal(1, parsedItems[0].Severity);
            Assert.Equal("Test Error", parsedItems[0].Message);

            // 2. Autocomplete Request
            len = BinaryMessageSerializer.WriteAutocompleteRequest(buffer, 13, 25);
            Assert.True(BinaryMessageSerializer.TryParseHeader(buffer.AsSpan(0, len), out header));
            Assert.Equal(MessageTypes.AutocompleteRequest, header.Type);
            Assert.Equal(13, header.DocumentId);
            Assert.Equal(25, header.Offset);

            // 3. Autocomplete Response
            var completions = new[]
            {
                new AutocompleteItem { Label = "Console", Detail = "class System.Console" }
            };
            len = BinaryMessageSerializer.WriteAutocompleteResponse(buffer, 14, 30, completions);
            Assert.True(BinaryMessageSerializer.TryParseHeader(buffer.AsSpan(0, len), out header));
            Assert.Equal(MessageTypes.AutocompleteResponse, header.Type);
            Assert.Equal(14, header.DocumentId);
            Assert.Equal(30, header.Offset);

            var parsedCompletions = BinaryMessageSerializer.ParseAutocompleteResponse(buffer.AsSpan(0, len), out docId, out int offset);
            Assert.Equal(14, docId);
            Assert.Equal(30, offset);
            Assert.Single(parsedCompletions);
            Assert.Equal("Console", parsedCompletions[0].Label);
            Assert.Equal("class System.Console", parsedCompletions[0].Detail);

            // 4. Hover Request
            len = BinaryMessageSerializer.WriteHoverRequest(buffer, 15, 35);
            Assert.True(BinaryMessageSerializer.TryParseHeader(buffer.AsSpan(0, len), out header));
            Assert.Equal(MessageTypes.HoverRequest, header.Type);
            Assert.Equal(15, header.DocumentId);
            Assert.Equal(35, header.Offset);

            // 5. Hover Response
            len = BinaryMessageSerializer.WriteHoverResponse(buffer, 16, 40, 38, 42, "Hover Text Content");
            Assert.True(BinaryMessageSerializer.TryParseHeader(buffer.AsSpan(0, len), out header));
            Assert.Equal(MessageTypes.HoverResponse, header.Type);
            Assert.Equal(16, header.DocumentId);
            Assert.Equal(40, header.Offset);

            string parsedHover = BinaryMessageSerializer.ParseHoverResponse(buffer.AsSpan(0, len), out docId, out offset, out int startOffset, out int endOffset);
            Assert.Equal(16, docId);
            Assert.Equal(40, offset);
            Assert.Equal(38, startOffset);
            Assert.Equal(42, endOffset);
            Assert.Equal("Hover Text Content", parsedHover);
        }

        [Fact]
        public void TestLspIntegrationDiagnostics()
        {
            using var connection = new SpanCoder.App.IpcEngineConnection();
            connection.Start();

            bool receivedDiagnostics = false;
            System.Collections.Generic.List<DiagnosticItem>? reportedDiags = null;

            connection.MessageReceived += (msg) =>
            {
                if (BinaryMessageSerializer.TryParseHeader(msg, out var header))
                {
                    if (header.Type == MessageTypes.DiagnosticsReport)
                    {
                        var diags = BinaryMessageSerializer.ParseDiagnosticsReport(msg, out _);
                        reportedDiags = diags;
                        receivedDiagnostics = true;
                    }
                }
            };

            string tempFile = Path.Combine(Path.GetTempPath(), "lsp_integration_test.cs");
            File.WriteAllText(tempFile, "using System;\n// todo: fix this later\nvar x = 123");

            try
            {
                byte[] loadMsg = new byte[BinaryMessageSerializer.HeaderSize + 4 + tempFile.Length * 2];
                BinaryMessageSerializer.WriteLoadFile(loadMsg, tempFile);
                connection.Send(loadMsg);

                int retries = 0;
                while (!receivedDiagnostics && retries++ < 60)
                {
                    System.Threading.Thread.Sleep(100);
                }

                Assert.True(receivedDiagnostics, "Diagnostics report not received");
                Assert.NotNull(reportedDiags);
                Assert.True(reportedDiags.Count >= 2, $"Expected at least 2 diagnostics, but got {reportedDiags.Count}");

                var todoDiag = System.Linq.Enumerable.FirstOrDefault(reportedDiags, d => d.Message.Contains("TODO"));
                Assert.NotNull(todoDiag.Message);
                Assert.Equal(2, todoDiag.Severity);

                var semicolonDiag = System.Linq.Enumerable.FirstOrDefault(reportedDiags, d => d.Message.Contains("; expected"));
                Assert.NotNull(semicolonDiag.Message);
                Assert.Equal(1, semicolonDiag.Severity);
            }
            finally
            {
                if (File.Exists(tempFile))
                    File.Delete(tempFile);
            }
        }

        [Fact]
        public void TestLspMockSemicolonDiagnostics()
        {
            using var connection = new SpanCoder.App.IpcEngineConnection();
            connection.Start();

            bool receivedDiagnostics = false;
            System.Collections.Generic.List<DiagnosticItem>? reportedDiags = null;

            connection.MessageReceived += (msg) =>
            {
                if (BinaryMessageSerializer.TryParseHeader(msg, out var header))
                {
                    if (header.Type == MessageTypes.DiagnosticsReport)
                    {
                        var diags = BinaryMessageSerializer.ParseDiagnosticsReport(msg, out _);
                        reportedDiags = diags;
                        receivedDiagnostics = true;
                    }
                }
            };

            string tempFile = Path.Combine(Path.GetTempPath(), "lsp_integration_test_semicolon.cs");
            string fileContent = 
                "using System;\n" +
                "var methodProvider = context.SyntaxProvider.CreateSyntaxProvider(\n" +
                "    (node, _) => node is MethodDeclarationSyntax && ((MethodDeclarationSyntax)node).AttributeLists.Count > 0,\n" +
                "    (ctx, _) => GetCommandMethodInfo(ctx)\n" +
                ").Where(m => m != null).Select((m, _) => m!);\n" +
                "\n" +
                "var code = @\"// <auto-generated />\n" +
                "using System;\n" +
                "using SpanCoder.Contracts;\n" +
                "\n" +
                "namespace SpanCoder.Contracts\n" +
                "{\n" +
                "    public static class GeneratedCommandRegistry\n" +
                "    {\n" +
                "    }\n" +
                "}\n" +
                "\";\n" +
                "\n" +
                "// Log some error here\n" +
                "Console.WriteLine(\"Accept error: message\");\n" +
                "error\n" +
                "var invalidVar = 456\n";

            File.WriteAllText(tempFile, fileContent);

            try
            {
                byte[] loadMsg = new byte[BinaryMessageSerializer.HeaderSize + 4 + tempFile.Length * 2];
                BinaryMessageSerializer.WriteLoadFile(loadMsg, tempFile);
                connection.Send(loadMsg);

                int retries = 0;
                while (!receivedDiagnostics && retries++ < 60)
                {
                    System.Threading.Thread.Sleep(100);
                }

                Assert.True(receivedDiagnostics, "Diagnostics report not received");
                Assert.NotNull(reportedDiags);

                var semicolonDiags = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Where(reportedDiags, d => d.Message.Contains("; expected")));
                Assert.Single(semicolonDiags);

                var mockErrorDiags = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Where(reportedDiags, d => d.Message.Contains("Mock error description")));
                Assert.Single(mockErrorDiags);
            }
            finally
            {
                if (File.Exists(tempFile))
                    File.Delete(tempFile);
            }
        }

        [Fact]
        public void TestIpcEngineConnectionBatchEdit()
        {
            using var connection = new SpanCoder.App.IpcEngineConnection();
            connection.Start();

            bool receivedLoadResponse = false;
            int finalDocId = -1;

            connection.MessageReceived += (msg) =>
            {
                if (BinaryMessageSerializer.TryParseHeader(msg, out var header))
                {
                    if (header.Type == MessageTypes.DocumentChanged)
                    {
                        var text = BinaryMessageSerializer.ParseDocumentChanged(msg, out int docId, out int offset, out int addedLength, out int deletedLength);
                        finalDocId = docId;
                        receivedLoadResponse = true;
                    }
                }
            };

            string tempFile = Path.Combine(Path.GetTempPath(), "ipc_batch_test.txt");
            File.WriteAllText(tempFile, "Line 1\nLine 2\nLine 3");

            try
            {
                byte[] loadMsg = new byte[BinaryMessageSerializer.HeaderSize + 4 + tempFile.Length * 2];
                BinaryMessageSerializer.WriteLoadFile(loadMsg, tempFile);
                connection.Send(loadMsg);

                int retries = 0;
                while (!receivedLoadResponse && retries++ < 50)
                {
                    System.Threading.Thread.Sleep(100);
                }
                Assert.True(receivedLoadResponse, "Load file response not received");
                Assert.True(finalDocId > 0);

                bool receivedBatchResponse = false;
                connection.MessageReceived += (msg) =>
                {
                    if (BinaryMessageSerializer.TryParseHeader(msg, out var header))
                    {
                        if (header.Type == MessageTypes.BatchEditResponse)
                        {
                            receivedBatchResponse = true;
                        }
                    }
                };

                // Send BatchEditRequest inserting 'x' at offset 2, 9, 16
                var edits = new[]
                {
                    new TextEdit { Offset = 2, DeleteLength = 0, Text = "x" },
                    new TextEdit { Offset = 9, DeleteLength = 0, Text = "x" },
                    new TextEdit { Offset = 16, DeleteLength = 0, Text = "x" }
                };

                int editsBytes = 0;
                foreach (var edit in edits)
                {
                    editsBytes += sizeof(int) * 3 + edit.Text.Length * sizeof(char);
                }
                byte[] batchMsg = new byte[BinaryMessageSerializer.HeaderSize + sizeof(int) + editsBytes];
                BinaryMessageSerializer.WriteBatchEditRequest(batchMsg, finalDocId, edits);
                connection.Send(batchMsg);

                retries = 0;
                while (!receivedBatchResponse && retries++ < 50)
                {
                    System.Threading.Thread.Sleep(100);
                }
                Assert.True(receivedBatchResponse, "Batch edit response not received");

                Document? doc = null;
                retries = 0;
                while (doc == null && retries++ < 50)
                {
                    doc = connection.GetDocument(finalDocId) as Document;
                    if (doc == null) System.Threading.Thread.Sleep(50);
                }
                Assert.NotNull(doc);
                char[] textBuf = new char[doc.Length];
                doc.PieceTable.GetText(0, doc.Length, textBuf);
                Assert.Equal("Lixne 1\nLixne 2\nLixne 3", new string(textBuf));
            }
            finally
            {
                if (File.Exists(tempFile))
                    File.Delete(tempFile);
            }
        }

        [Fact]
        public void TestOpenDocumentDirtyState()
        {
            var docView = new Document(1, "initial".AsMemory());
            var openDoc = new ShellWindow.OpenDocument(1, "test.txt", docView);

            // Initially clean
            Assert.False(openDoc.IsDirty);

            // Set dirty
            openDoc.IsDirty = true;
            Assert.True(openDoc.IsDirty);

            // Clean it
            openDoc.IsDirty = false;
            Assert.False(openDoc.IsDirty);
        }
    }
}

