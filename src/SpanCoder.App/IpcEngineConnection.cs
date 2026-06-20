using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SpanCoder.Contracts;
using SpanCoder.Engine; // App references Engine, so we can use Document for local mirroring

namespace SpanCoder.App
{
    public class IpcEngineConnection : IEngineConnection, IDisposable
    {
        private TcpListener? _listener;
        private TcpClient? _client;
        private NetworkStream? _stream;
        private Process? _engineProcess;
        private readonly object _writeLock = new();
        private readonly CancellationTokenSource _cts = new();

        private readonly string? _remoteHost;
        private readonly int _remotePort;
        private readonly RemotePathMapper _pathMapper = new(null);
        
        // Local Document Mirrors
        private readonly ConcurrentDictionary<int, Document> _documentMirrors = new();
        
        // History and Crash Recovery State
        private class DocumentState
        {
            public int OldId { get; set; }
            public string FilePath { get; set; } = "";
            public List<byte[]> EditHistory { get; } = new();
        }
        
        private readonly ConcurrentDictionary<int, DocumentState> _trackedStates = new();
        private readonly ConcurrentDictionary<string, int> _pathToOldIdMap = new();
        private readonly ConcurrentDictionary<int, int> _oldToNewIdMap = new();
        private readonly ConcurrentQueue<string> _pendingLoadFiles = new();
        private volatile bool _isRecovering = false;

        public event Action<byte[]>? MessageReceived;

        public IpcEngineConnection()
        {
        }

        public IpcEngineConnection(string? remoteHost, int remotePort, string? pathMapping)
        {
            _remoteHost = remoteHost;
            _remotePort = remotePort;
            _pathMapper = new RemotePathMapper(pathMapping);
        }

        public void Start()
        {
            StartListenerAndSpawn();
            Task.Run(ReadLoop);
        }

        private void StartListenerAndSpawn()
        {
            if (!string.IsNullOrEmpty(_remoteHost))
            {
                LogHelper.Log($"[IpcConnection] Connecting to remote engine at {_remoteHost}:{_remotePort}...");
                _client = new TcpClient();
                var connectTask = _client.ConnectAsync(_remoteHost, _remotePort);
                if (connectTask.Wait(5000))
                {
                    _stream = _client.GetStream();
                    LogHelper.Log("[IpcConnection] Connected to remote engine.");
                }
                else
                {
                    throw new TimeoutException($"Timeout connecting to remote engine at {_remoteHost}:{_remotePort}");
                }
                return;
            }

            // 1. Bind listener to a dynamic free port
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            LogHelper.Log($"[IpcConnection] Bound TcpListener to port {port}");

            // 2. Spawn Engine Process
            string arguments;
            string executable = GetEngineExecutable(out arguments, port);
            LogHelper.Log($"[IpcConnection] Spawning engine: {executable} {arguments}");

            var psi = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            _engineProcess = Process.Start(psi);
            if (_engineProcess == null)
            {
                throw new IOException("Failed to start engine process.");
            }

            // Spawn background tasks to read standard output and error from the engine process
            Task.Run(async () =>
            {
                try
                {
                    while (!_cts.Token.IsCancellationRequested)
                    {
                        string? line = await _engineProcess.StandardOutput.ReadLineAsync();
                        if (line == null) break;
                        LogHelper.Log($"[Engine-Out] {line}");
                    }
                }
                catch { }
            });
            Task.Run(async () =>
            {
                try
                {
                    while (!_cts.Token.IsCancellationRequested)
                    {
                        string? line = await _engineProcess.StandardError.ReadLineAsync();
                        if (line == null) break;
                        LogHelper.Log($"[Engine-Err] {line}");
                    }
                }
                catch { }
            });

            // 3. Accept incoming TCP connection from the Engine
            // Set a timeout of 5 seconds for safety
            var acceptTask = _listener.AcceptTcpClientAsync();
            if (acceptTask.Wait(5000))
            {
                _client = acceptTask.Result;
                _stream = _client.GetStream();
                LogHelper.Log("[IpcConnection] Connected to engine process.");
            }
            else
            {
                _listener.Stop();
                _engineProcess.Kill();
                throw new TimeoutException("Timeout waiting for engine process connection.");
            }
        }

        public void Send(byte[] message)
        {
            if (!BinaryMessageSerializer.TryParseHeader(message, out var header)) return;

            LogHelper.Log($"[IpcConnection] Send: Type={header.Type}, DocumentId={header.DocumentId}, Length={header.Length}");

            // Map paths for outgoing messages
            if (header.Type == MessageTypes.LoadFile)
            {
                var localPath = BinaryMessageSerializer.ParseLoadFile(message).ToString();
                var remotePath = _pathMapper.ToRemote(localPath);
                
                byte[] newBuffer = new byte[BinaryMessageSerializer.HeaderSize + 4 + remotePath.Length * 2];
                BinaryMessageSerializer.WriteLoadFile(newBuffer, remotePath);
                message = newBuffer;

                _pendingLoadFiles.Enqueue(localPath);
                _pathToOldIdMap[localPath] = -1;
            }
            else if (header.Type == MessageTypes.DebugStartRequest)
            {
                var localPath = BinaryMessageSerializer.ParseDebugStartRequest(message, out int docId);
                var remotePath = _pathMapper.ToRemote(localPath);

                byte[] newBuffer = new byte[BinaryMessageSerializer.HeaderSize + 4 + remotePath.Length * 2];
                BinaryMessageSerializer.WriteDebugStartRequest(newBuffer, docId, remotePath);
                message = newBuffer;
            }
            else if (header.Type == MessageTypes.InsertText || header.Type == MessageTypes.DeleteText || header.Type == MessageTypes.BatchEditRequest)
            {
                TrackOutgoingMessage(message);
            }

            if (_stream == null) return;

            try
            {
                lock (_writeLock)
                {
                    _stream.Write(message, 0, message.Length);
                    _stream.Flush();
                }
            }
            catch (Exception ex)
            {
                LogHelper.Log($"[IpcConnection] Socket write failed: {ex.Message}");
                HandleCrash();
            }
        }

        public IDocumentView? GetDocument(int documentId)
        {
            int lookupId = documentId;
            if (_oldToNewIdMap.TryGetValue(documentId, out int newId))
            {
                lookupId = newId;
            }
            bool exists = _documentMirrors.TryGetValue(lookupId, out var doc);
            LogHelper.Log($"[IpcConnection] GetDocument: originalId={documentId}, lookupId={lookupId}, exists={exists}, docLength={doc?.Length}");
            return doc;
        }

        private void TrackOutgoingMessage(ReadOnlySpan<byte> message)
        {
            if (_isRecovering) return; // Don't record history during recovery replays

            if (!BinaryMessageSerializer.TryParseHeader(message, out var header)) return;

            if (header.Type == MessageTypes.InsertText || header.Type == MessageTypes.DeleteText || header.Type == MessageTypes.BatchEditRequest)
            {
                int docId = header.DocumentId;
                if (_trackedStates.TryGetValue(docId, out var state))
                {
                    byte[] copy = message.ToArray();
                    lock (state.EditHistory)
                    {
                        state.EditHistory.Add(copy);
                    }
                }
            }
        }

        private void ReadLoop()
        {
            byte[] headerBuffer = new byte[BinaryMessageSerializer.HeaderSize];

            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    if (_stream == null || _client == null || !_client.Connected)
                    {
                        Thread.Sleep(100);
                        continue;
                    }

                    // Read Header
                    ReadExactly(_stream, headerBuffer, 0, headerBuffer.Length);
                    
                    if (!BinaryMessageSerializer.TryParseHeader(headerBuffer, out var header))
                    {
                        throw new InvalidDataException("IPC header parse error.");
                    }

                    int messageLength = header.Length;
                    byte[] rented = System.Buffers.ArrayPool<byte>.Shared.Rent(messageLength);
                    try
                    {
                        Array.Copy(headerBuffer, 0, rented, 0, headerBuffer.Length);

                        if (messageLength > headerBuffer.Length)
                        {
                            ReadExactly(_stream, rented, headerBuffer.Length, messageLength - headerBuffer.Length);
                        }

                        // Translate incoming message paths
                        byte[] translated = TranslateIncomingMessage(rented);

                        // Process incoming message locally (mirroring)
                        ProcessIncomingMessage(translated);

                        // Dispatch to UI listeners
                        MessageReceived?.Invoke(translated);
                    }
                    finally
                    {
                        System.Buffers.ArrayPool<byte>.Shared.Return(rented);
                    }
                }
                catch (Exception ex)
                {
                    if (!_cts.Token.IsCancellationRequested)
                    {
                        LogHelper.Log($"[IpcConnection] Socket read error: {ex.Message}");
                        HandleCrash();
                    }
                }
            }
        }

        private void ProcessIncomingMessage(ReadOnlySpan<byte> message)
        {
            if (!BinaryMessageSerializer.TryParseHeader(message, out var header)) return;
            LogHelper.Log($"[IpcConnection] ProcessIncomingMessage: Type={header.Type}, DocumentId={header.DocumentId}, Length={header.Length}");

            if (header.Type == MessageTypes.DocumentChanged)
            {
                var text = BinaryMessageSerializer.ParseDocumentChanged(message, out int docId, out int offset, out int addedLength, out int deletedLength);

                if (_isRecovering)
                {
                    // During recovery, update local mirrors
                    UpdateLocalMirror(docId, offset, addedLength, deletedLength, text);
                    return;
                }

                // Normal execution
                // 1. Map pending LoadFile paths to the document ID
                if (offset == 0 && deletedLength == 0 && addedLength == text.Length)
                {
                    if (_pendingLoadFiles.TryDequeue(out string? filePath))
                    {
                        _pathToOldIdMap[filePath] = docId;
                        var state = new DocumentState
                        {
                            OldId = docId,
                            FilePath = filePath
                        };
                        _trackedStates[docId] = state;
                    }
                }

                // 2. Synchronize local document mirror
                UpdateLocalMirror(docId, offset, addedLength, deletedLength, text);
            }
            else if (header.Type == MessageTypes.BatchEditResponse)
            {
                var edits = BinaryMessageSerializer.ParseBatchEditResponse(message, out int docId);
                UpdateLocalMirrorForBatch(docId, edits);
            }
        }

        private void UpdateLocalMirror(int docId, int offset, int addedLength, int deletedLength, ReadOnlySpan<char> insertedText)
        {
            if (Avalonia.Application.Current != null && !IsRunningInUnitTest() && !Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                string textStr = insertedText.ToString();
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    UpdateLocalMirror(docId, offset, addedLength, deletedLength, textStr.AsSpan());
                });
                return;
            }

            if (offset == 0 && deletedLength == 0 && addedLength == insertedText.Length)
            {
                // File load or reload
                if (_documentMirrors.TryGetValue(docId, out var existing))
                {
                    existing.Dispose();
                }
                // Allocate a contiguous buffer copy for the mirror
                char[] mirrorBuffer = new char[insertedText.Length];
                insertedText.CopyTo(mirrorBuffer);

                string filePath = "";
                if (_trackedStates.TryGetValue(docId, out var state))
                {
                    filePath = state.FilePath;
                }
                else
                {
                    foreach (var kvp in _pathToOldIdMap)
                    {
                        if (kvp.Value == docId)
                        {
                            filePath = kvp.Key;
                            break;
                        }
                    }
                }

                _documentMirrors[docId] = new Document(docId, new ReadOnlyMemory<char>(mirrorBuffer), filePath);
            }
            else
            {
                // Incremental edit
                if (_documentMirrors.TryGetValue(docId, out var doc))
                {
                    if (deletedLength > 0)
                    {
                        doc.Delete(offset, deletedLength);
                    }
                    if (addedLength > 0)
                    {
                        doc.Insert(offset, insertedText);
                    }
                }
            }
        }

        private void UpdateLocalMirrorForBatch(int docId, TextEdit[] edits)
        {
            if (Avalonia.Application.Current != null && !IsRunningInUnitTest() && !Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    UpdateLocalMirrorForBatch(docId, edits);
                });
                return;
            }

            LogHelper.Log($"[IpcConnection] UpdateLocalMirrorForBatch: docId={docId}, editsCount={edits.Length}, documentExists={_documentMirrors.ContainsKey(docId)}");
            if (_documentMirrors.TryGetValue(docId, out var doc))
            {
                LogHelper.Log($"[IpcConnection] UpdateLocalMirrorForBatch: doc beforeLength={doc.Length}");
                var sortedEdits = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.OrderByDescending(edits, e => e.Offset));
                foreach (var edit in sortedEdits)
                {
                    LogHelper.Log($"[IpcConnection] UpdateLocalMirrorForBatch applying edit: offset={edit.Offset}, deleteLen={edit.DeleteLength}, text='{edit.Text}'");
                    if (edit.DeleteLength > 0)
                    {
                        doc.Delete(edit.Offset, edit.DeleteLength);
                    }
                    if (!string.IsNullOrEmpty(edit.Text))
                    {
                        doc.Insert(edit.Offset, edit.Text);
                    }
                }
                LogHelper.Log($"[IpcConnection] UpdateLocalMirrorForBatch: doc afterLength={doc.Length}");
            }
        }

        private void HandleCrash()
        {
            lock (_writeLock)
            {
                if (_isRecovering) return;
                _isRecovering = true;
            }

            Console.WriteLine("[IpcConnection] Engine process crash detected! Starting automatic recovery...");

            try
            {
                // 1. Clean up socket streams
                _stream?.Close();
                _client?.Close();
                _listener?.Stop();

                // 2. Kill crash process if lingering
                try
                {
                    if (_engineProcess != null && !_engineProcess.HasExited)
                    {
                        _engineProcess.Kill();
                    }
                }
                catch { }

                // 3. Clear current document mirrors
                _documentMirrors.Clear();
                _oldToNewIdMap.Clear();

                // 4. Re-spawn engine and listener
                StartListenerAndSpawn();

                // 5. Replay history
                ReplayHistory();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[IpcConnection] Crash recovery failed: {ex.Message}");
            }
            finally
            {
                lock (_writeLock)
                {
                    _isRecovering = false;
                }
                Console.WriteLine("[IpcConnection] Recovery completed.");
            }
        }

        private void ReplayHistory()
        {
            // Replay the file load and all edits in order
            foreach (var state in _trackedStates.Values)
            {
                Console.WriteLine($"[IpcConnection] Replaying load for {state.FilePath}...");
                
                // Construct and send LoadFile message
                string remotePath = _pathMapper.ToRemote(state.FilePath);
                byte[] loadBuffer = new byte[BinaryMessageSerializer.HeaderSize + 4 + remotePath.Length * 2];
                BinaryMessageSerializer.WriteLoadFile(loadBuffer, remotePath);
                
                // Send synchronously to let the engine respond and allocate the document ID
                lock (_writeLock)
                {
                    _stream!.Write(loadBuffer, 0, loadBuffer.Length);
                    _stream.Flush();
                }

                // Read the DocumentChanged confirmation packet synchronously
                byte[] responseHeader = new byte[BinaryMessageSerializer.HeaderSize];
                ReadExactly(_stream!, responseHeader, 0, responseHeader.Length);
                BinaryMessageSerializer.TryParseHeader(responseHeader, out var header);

                byte[] responsePayload = new byte[header.Length];
                Array.Copy(responseHeader, 0, responsePayload, 0, responseHeader.Length);
                if (header.Length > responseHeader.Length)
                {
                    ReadExactly(_stream!, responsePayload, responseHeader.Length, header.Length - responseHeader.Length);
                }

                // Parse the loaded DocumentChanged packet
                var text = BinaryMessageSerializer.ParseDocumentChanged(responsePayload, out int newDocId, out int offset, out int addedLength, out int deletedLength);
                
                // Map the old document ID to the new document ID
                _oldToNewIdMap[state.OldId] = newDocId;
                
                // Update the local mirror
                UpdateLocalMirror(newDocId, offset, addedLength, deletedLength, text);

                // Replay the edits
                Console.WriteLine($"[IpcConnection] Replaying {state.EditHistory.Count} edits for {state.FilePath}...");
                lock (state.EditHistory)
                {
                    foreach (var editPacket in state.EditHistory)
                    {
                        // Overwrite document ID in header with newDocId (DocumentId is at offset 5 in MessageHeader layout)
                        // Wait, MessageHeader: Type (offset 0), Length (offset 1, 4 bytes), DocumentId (offset 5, 4 bytes)
                        byte[] newDocIdBytes = BitConverter.GetBytes(newDocId);
                        Array.Copy(newDocIdBytes, 0, editPacket, 5, 4);

                        // Send to socket
                        lock (_writeLock)
                        {
                            _stream!.Write(editPacket, 0, editPacket.Length);
                            _stream.Flush();
                        }
                    }
                }
            }
        }

        public void SimulateEngineCrash()
        {
            try
            {
                _engineProcess?.Kill();
            }
            catch { }
        }

        private string GetEngineExecutable(out string arguments, int port)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            
            // 1. Check native binary in current directory
            string nativeExe = Path.Combine(baseDir, "SpanCoder.Engine.exe");
            if (!File.Exists(nativeExe))
            {
                nativeExe = Path.Combine(baseDir, "SpanCoder.Engine"); // Linux
            }
            
            if (File.Exists(nativeExe))
            {
                arguments = $"--port {port}";
                return nativeExe;
            }

            // 2. Check DLL in current directory
            string dllPath = Path.Combine(baseDir, "SpanCoder.Engine.dll");
            if (File.Exists(dllPath))
            {
                arguments = $"\"{dllPath}\" --port {port}";
                return "dotnet";
            }

            // 3. Fallback: navigate up to find project path for dev "dotnet run"
            string? dir = baseDir;
            for (int i = 0; i < 5 && dir != null; i++)
            {
                string projectDir = Path.Combine(dir, "src", "SpanCoder.Engine");
                if (Directory.Exists(projectDir))
                {
                    arguments = $"run --project \"{Path.Combine(projectDir, "SpanCoder.Engine.csproj")}\" -- --port {port}";
                    return "dotnet";
                }
                dir = Path.GetDirectoryName(dir);
            }

            arguments = $"run --project src/SpanCoder.Engine/SpanCoder.Engine.csproj -- --port {port}";
            return "dotnet";
        }

        private static void ReadExactly(NetworkStream stream, byte[] buffer, int offset, int count)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = stream.Read(buffer, offset + totalRead, count - totalRead);
                if (read <= 0)
                    throw new IOException("Socket closed prematurely");
                totalRead += read;
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _stream?.Close();
            _client?.Close();
            _listener?.Stop();

            try
            {
                if (_engineProcess != null && !_engineProcess.HasExited)
                {
                    _engineProcess.Kill();
                }
            }
            catch { }
            
            foreach (var doc in _documentMirrors.Values)
            {
                doc.Dispose();
            }
            _documentMirrors.Clear();
        }

        private byte[] TranslateIncomingMessage(byte[] message)
        {
            if (!BinaryMessageSerializer.TryParseHeader(message, out var header)) return message;

            if (header.Type == MessageTypes.GotoDefinitionResponse)
            {
                var remotePath = BinaryMessageSerializer.ParseGotoDefinitionResponse(message, out int docId, out int offset, out int line, out int character);
                var localPath = _pathMapper.ToLocal(remotePath);
                
                byte[] newBuffer = new byte[BinaryMessageSerializer.HeaderSize + 12 + localPath.Length * 2];
                BinaryMessageSerializer.WriteGotoDefinitionResponse(newBuffer, docId, offset, localPath, line, character);
                return newBuffer;
            }
            else if (header.Type == MessageTypes.FindReferencesResponse)
            {
                var items = BinaryMessageSerializer.ParseFindReferencesResponse(message, out int docId, out int offset);
                var mappedItems = new List<ReferenceItem>();
                foreach (var item in items)
                {
                    mappedItems.Add(new ReferenceItem
                    {
                        FilePath = _pathMapper.ToLocal(item.FilePath),
                        Line = item.Line,
                        Character = item.Character
                    });
                }
                
                int bodyLen = sizeof(int);
                for (int i = 0; i < mappedItems.Count; i++)
                {
                    bodyLen += sizeof(int) + mappedItems[i].FilePath.Length * sizeof(char) + sizeof(int) + sizeof(int);
                }
                
                byte[] newBuffer = new byte[BinaryMessageSerializer.HeaderSize + bodyLen];
                BinaryMessageSerializer.WriteFindReferencesResponse(newBuffer, docId, offset, mappedItems.ToArray());
                return newBuffer;
            }
            else if (header.Type == MessageTypes.DebugStateReport)
            {
                BinaryMessageSerializer.ParseDebugStateReport(message, out int docId, out var stackFrames, out var variables);
                var mappedFrames = new List<string>();
                foreach (var frame in stackFrames)
                {
                    mappedFrames.Add(_pathMapper.TranslateFrame(frame));
                }
                
                int bodyLen = sizeof(int);
                for (int i = 0; i < mappedFrames.Count; i++) bodyLen += sizeof(int) + mappedFrames[i].Length * sizeof(char);
                bodyLen += sizeof(int);
                for (int i = 0; i < variables.Count; i++) bodyLen += sizeof(int) + variables[i].Length * sizeof(char);
                
                byte[] newBuffer = new byte[BinaryMessageSerializer.HeaderSize + bodyLen];
                BinaryMessageSerializer.WriteDebugStateReport(newBuffer, docId, mappedFrames, variables);
                return newBuffer;
            }

            return message;
        }

        private static readonly bool IsRunningInUnitTestCached = DetermineIfRunningInUnitTest();

        private static bool DetermineIfRunningInUnitTest()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = assembly.FullName ?? "";
                if (name.Contains("test", StringComparison.OrdinalIgnoreCase) || 
                    name.Contains("xunit", StringComparison.OrdinalIgnoreCase) || 
                    name.Contains("nunit", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsRunningInUnitTest() => IsRunningInUnitTestCached;
    }
}
