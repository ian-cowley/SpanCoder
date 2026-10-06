using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using SpanCoder.Contracts;

namespace SpanCoder.Engine
{
    public partial class EngineHost : IEngineConnection
    {
        private readonly Channel<byte[]> _inputChannel;
        private readonly ConcurrentDictionary<int, Document> _documents;
        private readonly Thread _workerThread;
        private readonly CancellationTokenSource _cts;
        private int _nextDocumentId = 1;
        private readonly ConcurrentDictionary<string, LspClient> _lspClients = new();
        private DapClient? _dapClient;
        private readonly AiAgentCoordinator _aiCoordinator;

        public ChannelWriter<byte[]> Input => _inputChannel.Writer;

        public event Action<byte[]>? MessageReceived;

        public void Send(byte[] message)
        {
            _inputChannel.Writer.TryWrite(message);
        }

        IDocumentView? IEngineConnection.GetDocument(int documentId)
        {
            _documents.TryGetValue(documentId, out var doc);
            return doc;
        }

        private void Log(string message)
        {
            SpanCoderDiagnostics.LogInformation($"[EngineHost] {message}");
        }

        public EngineHost()
        {
            Log("EngineHost initializing...");
            _inputChannel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
            _documents = new ConcurrentDictionary<int, Document>();
            _cts = new CancellationTokenSource();
            _aiCoordinator = new AiAgentCoordinator(responsePayload => MessageReceived?.Invoke(responsePayload));
            _workerThread = new Thread(ProcessLoop)
            {
                IsBackground = true,
                Name = "SpanCoder.Engine.Worker"
            };
        }

        public void Start()
        {
            _workerThread.Start();
        }

        public void Stop()
        {
            _cts.Cancel();
            _inputChannel.Writer.Complete();
            if ((_workerThread.ThreadState & System.Threading.ThreadState.Unstarted) == 0)
            {
                _workerThread.Join(2000);
            }
            _dapClient?.Dispose();
            _dapClient = null;
            foreach (var client in _lspClients.Values)
            {
                client.Dispose();
            }
            _lspClients.Clear();
            foreach (var doc in _documents.Values)
            {
                doc.Dispose();
            }
            _documents.Clear();
        }

        public Document? GetDocument(int documentId)
        {
            _documents.TryGetValue(documentId, out var doc);
            return doc;
        }

        private void ProcessLoop()
        {
            var reader = _inputChannel.Reader;
            var token = _cts.Token;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    // Synchronously read or wait for a message
                    if (!reader.WaitToReadAsync(token).AsTask().GetAwaiter().GetResult())
                        break;

                    while (reader.TryRead(out var message))
                    {
                        ProcessMessage(message);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    SpanCoderDiagnostics.LogError($"[EngineHost] Error in process loop: {ex}");
                }
            }
        }

        private void ProcessMessage(ReadOnlySpan<byte> message)
        {
            if (!BinaryMessageSerializer.TryParseHeader(message, out var header))
            {
                Log("ProcessMessage: Failed to parse message header.");
                return;
            }

            Log($"ProcessMessage: type={header.Type}, docId={header.DocumentId}, offset={header.Offset}");
            switch (header.Type)
            {
                case MessageTypes.LoadFile:
                    HandleLoadFile(message);
                    break;

                case MessageTypes.SaveFile:
                    HandleSaveFile(header);
                    break;

                case MessageTypes.InsertText:
                    HandleInsertText(message);
                    break;

                case MessageTypes.DeleteText:
                    HandleDeleteText(message);
                    break;

                case MessageTypes.BatchEditRequest:
                    HandleBatchEditRequest(message);
                    break;

                case MessageTypes.AutocompleteRequest:
                case MessageTypes.HoverRequest:
                case MessageTypes.GotoDefinitionRequest:
                case MessageTypes.FindReferencesRequest:
                case MessageTypes.RenameRequest:
                case MessageTypes.DocumentSymbolsRequest:
                case MessageTypes.FoldingRangeRequest:
                case MessageTypes.CommandRequest:
                case MessageTypes.DebugStartRequest:
                case MessageTypes.DebugStopRequest:
                case MessageTypes.DebugStepOverRequest:
                case MessageTypes.DebugStepIntoRequest:
                case MessageTypes.DebugStepOutRequest:
                case MessageTypes.DebugContinueRequest:
                case MessageTypes.DebugSetBreakpointsRequest:
                    HandleLspDapMessage(header, message);
                    break;

                case MessageTypes.AiChatRequest:
                    {
                        string requestJson = BinaryMessageSerializer.ParseStringPayload(message);
                        _aiCoordinator.StartRequest(requestJson);
                        break;
                    }

                case MessageTypes.AiStopCommand:
                    {
                        _aiCoordinator.StopActiveRequest();
                        break;
                    }

                case MessageTypes.AiToolApproval:
                    {
                        string approvalJson = BinaryMessageSerializer.ParseStringPayload(message);
                        _aiCoordinator.HandleToolApproval(approvalJson);
                        break;
                    }
            }
        }

        private void HandleLoadFile(ReadOnlySpan<byte> message)
        {
            var filePathSpan = BinaryMessageSerializer.ParseLoadFile(message);
            string filePath = filePathSpan.ToString();
            Log($"ProcessMessage LoadFile: filePath={filePath}");

            int docId = _nextDocumentId++;
            ReadOnlyMemory<char> initialText = ReadFileContent(filePath);

            var doc = new Document(docId, initialText, filePath);
            _documents[docId] = doc;
            Log($"ProcessMessage LoadFile: Created Document docId={docId}, filePath={filePath}, length={initialText.Length}");

            // Emit DocumentChanged response (added docId, offset=0, addedLength=initialText.Length, deletedLength=0)
            byte[] responseBuffer = new byte[BinaryMessageSerializer.HeaderSize + 8 + initialText.Length * 2];
            BinaryMessageSerializer.WriteDocumentChanged(responseBuffer, docId, 0, initialText.Length, 0, initialText.Span);
            MessageReceived?.Invoke(responseBuffer);

            // Notify LSP Client
            Log($"ProcessMessage LoadFile: Notifying LSP client (NotifyDidOpen)...");
            GetOrCreateLspClient(filePath).NotifyDidOpen(filePath, initialText.ToString());
        }

        private void HandleSaveFile(MessageHeader header)
        {
            int docId = header.DocumentId;
            Log($"ProcessMessage SaveFile: docId={docId}");
            if (_documents.TryGetValue(docId, out var doc))
            {
                try
                {
                    char[] buffer = new char[doc.Length];
                    doc.PieceTable.GetText(0, doc.Length, buffer);
                    string text = new string(buffer);
                    File.WriteAllText(doc.FilePath, text);
                    Log($"ProcessMessage SaveFile: Saved {doc.FilePath}");

                    // Send response back
                    byte[] responseBuffer = new byte[BinaryMessageSerializer.HeaderSize];
                    BinaryMessageSerializer.WriteSaveFileResponse(responseBuffer, docId);
                    MessageReceived?.Invoke(responseBuffer);
                }
                catch (Exception ex)
                {
                    Log($"ProcessMessage SaveFile failed: {ex.Message}");
                }
            }
        }

        private void HandleInsertText(ReadOnlySpan<byte> message)
        {
            var text = BinaryMessageSerializer.ParseInsertText(message, out int docId, out int offset);
            if (_documents.TryGetValue(docId, out var doc))
            {
                doc.Insert(offset, text);

                // Echo back change
                byte[] responseBuffer = new byte[BinaryMessageSerializer.HeaderSize + 8 + text.Length * 2];
                BinaryMessageSerializer.WriteDocumentChanged(responseBuffer, docId, offset, text.Length, 0, text);
                MessageReceived?.Invoke(responseBuffer);

                // Notify LSP Client
                char[] fullBuf = new char[doc.Length];
                doc.PieceTable.GetText(0, doc.Length, fullBuf);
                GetOrCreateLspClient(doc.FilePath).NotifyDidChange(doc.FilePath, new string(fullBuf));
            }
        }

        private void HandleDeleteText(ReadOnlySpan<byte> message)
        {
            int length = BinaryMessageSerializer.ParseDeleteText(message, out int docId, out int offset);
            if (_documents.TryGetValue(docId, out var doc))
            {
                doc.Delete(offset, length);

                // Echo back change
                byte[] responseBuffer = new byte[BinaryMessageSerializer.HeaderSize + 8];
                BinaryMessageSerializer.WriteDocumentChanged(responseBuffer, docId, offset, 0, length, ReadOnlySpan<char>.Empty);
                MessageReceived?.Invoke(responseBuffer);

                // Notify LSP Client
                char[] fullBuf = new char[doc.Length];
                doc.PieceTable.GetText(0, doc.Length, fullBuf);
                GetOrCreateLspClient(doc.FilePath).NotifyDidChange(doc.FilePath, new string(fullBuf));
            }
        }

        private void HandleBatchEditRequest(ReadOnlySpan<byte> message)
        {
            var edits = BinaryMessageSerializer.ParseBatchEditRequest(message, out int docId);
            LogHelper.Log($"[EngineHost] ProcessMessage BatchEditRequest: docId={docId}, editsCount={edits.Length}");
            if (_documents.TryGetValue(docId, out var doc))
            {
                LogHelper.Log($"[EngineHost] BatchEditRequest: doc beforeLength={doc.Length}");
                // Sort edits in descending order of offset so they do not shift each other's offsets
                var sortedEdits = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.OrderByDescending(edits, e => e.Offset));
                foreach (var edit in sortedEdits)
                {
                    LogHelper.Log($"[EngineHost] BatchEditRequest applying edit: offset={edit.Offset}, deleteLen={edit.DeleteLength}, text='{edit.Text}'");
                    if (edit.DeleteLength > 0)
                    {
                        doc.Delete(edit.Offset, edit.DeleteLength);
                    }
                    if (!string.IsNullOrEmpty(edit.Text))
                    {
                        doc.Insert(edit.Offset, edit.Text);
                    }
                }

                // Echo back change
                int textEditsBytes = 0;
                foreach (var edit in edits)
                {
                    textEditsBytes += sizeof(int) * 3;
                    if (edit.Text != null)
                    {
                        textEditsBytes += edit.Text.Length * sizeof(char);
                    }
                }
                int responseLen = BinaryMessageSerializer.HeaderSize + sizeof(int) + textEditsBytes;
                byte[] responseBuffer = new byte[responseLen];
                BinaryMessageSerializer.WriteBatchEditResponse(responseBuffer, docId, edits);
                LogHelper.Log($"[EngineHost] BatchEditRequest: sending BatchEditResponse ({responseBuffer.Length} bytes), doc afterLength={doc.Length}");
                MessageReceived?.Invoke(responseBuffer);

                // Notify LSP Client
                char[] fullBuf = new char[doc.Length];
                doc.PieceTable.GetText(0, doc.Length, fullBuf);
                GetOrCreateLspClient(doc.FilePath).NotifyDidChange(doc.FilePath, new string(fullBuf));
            }
        }

        private ReadOnlyMemory<char> ReadFileContent(string filePath)
        {
            if (!File.Exists(filePath))
                return ReadOnlyMemory<char>.Empty;

            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                long fileLength = fs.Length;
                if (fileLength > int.MaxValue)
                    throw new IOException("File too large");

                using var sr = new StreamReader(fs, System.Text.Encoding.UTF8);
                char[] buffer = new char[fileLength];
                int read = sr.ReadBlock(buffer, 0, (int)fileLength);
                return new ReadOnlyMemory<char>(buffer, 0, read);
            }
            catch (Exception ex)
            {
                SpanCoderDiagnostics.LogError($"[EngineHost] Error reading file {filePath}: {ex.Message}");
                return ReadOnlyMemory<char>.Empty;
            }
        }
    }
}
