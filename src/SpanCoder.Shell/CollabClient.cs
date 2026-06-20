using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SpanCoder.Contracts;

namespace SpanCoder.Shell
{
    public class CollabClient : IDisposable
    {
        private WebSocket? _webSocket;
        private CancellationTokenSource _cts = new();
        private CrdtDocument? _crdtDoc;
        private readonly string _username;
        private readonly string _clientId;
        private bool _isConnected;

        public CrdtDocument? CrdtDocument => _crdtDoc;
        public string ClientId => _clientId;
        public string Username => _username;
        public bool IsConnected => _isConnected;

        // Events
        public event Action<string>? SyncReceived;
        public event Action<int, char>? RemoteInsertReceived; // visibleOffset, value
        public event Action<int>? RemoteDeleteReceived; // visibleOffset
        public event Action<CollabCursorMessage>? RemoteCursorMoved;

        public CollabClient(string username)
        {
            _username = username;
            _clientId = "client_" + Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        public async Task ConnectAsync(string ipAddress, int port)
        {
            var ws = new ClientWebSocket();
            _crdtDoc = new CrdtDocument(_clientId);
            _cts = new CancellationTokenSource();

            var uri = new Uri($"ws://{ipAddress}:{port}/collab/");
            Console.WriteLine($"[CollabClient] Connecting to {uri}...");
            await ws.ConnectAsync(uri, _cts.Token);
            _webSocket = ws;
            _isConnected = true;
            Console.WriteLine("[CollabClient] Connected successfully.");

            // Start listening for incoming server messages
            _ = Task.Run(ReadLoop);
        }

        public void Disconnect()
        {
            _isConnected = false;
            _cts.Cancel();
            if (_webSocket != null)
            {
                try
                {
                    _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnecting", CancellationToken.None).Wait();
                }
                catch { }
                _webSocket.Dispose();
                _webSocket = null;
            }
        }

        public async Task SendInsertAsync(int visibleOffset, char val)
        {
            if (!_isConnected || _webSocket == null || _crdtDoc == null) return;

            CrdtNodeState node;
            node = _crdtDoc.LocalInsert(visibleOffset, val);

            var insertMsg = new CollabInsertMessage
            {
                Position = node.Position,
                Value = node.Value,
                ClientId = node.ClientId,
                Clock = node.Clock
            };

            var payload = new CollabPayload
            {
                Type = CollabMessageTypes.Insert,
                Data = JsonSerializer.Serialize(insertMsg, CollabJsonContext.Default.CollabInsertMessage)
            };

            await SendPayloadAsync(payload);
        }

        public async Task SendDeleteAsync(int visibleOffset)
        {
            if (!_isConnected || _webSocket == null || _crdtDoc == null) return;

            CrdtNodeState? node;
            node = _crdtDoc.LocalDelete(visibleOffset);
            if (node == null) return;

            var deleteMsg = new CollabDeleteMessage
            {
                Position = node.Position,
                ClientId = node.ClientId,
                Clock = node.Clock
            };

            var payload = new CollabPayload
            {
                Type = CollabMessageTypes.Delete,
                Data = JsonSerializer.Serialize(deleteMsg, CollabJsonContext.Default.CollabDeleteMessage)
            };

            await SendPayloadAsync(payload);
        }

        public async Task SendCursorAsync(int line, int character, int selStart, int selEnd)
        {
            if (!_isConnected || _webSocket == null) return;

            var cursorMsg = new CollabCursorMessage
            {
                ClientId = _clientId,
                Username = _username,
                Line = line,
                Character = character,
                SelectionStartOffset = selStart,
                SelectionEndOffset = selEnd,
                ColorHex = GetUserColorHex(_clientId)
            };

            var payload = new CollabPayload
            {
                Type = CollabMessageTypes.Cursor,
                Data = JsonSerializer.Serialize(cursorMsg, CollabJsonContext.Default.CollabCursorMessage)
            };

            await SendPayloadAsync(payload);
        }

        private async Task SendPayloadAsync(CollabPayload payload)
        {
            if (_webSocket == null || _webSocket.State != WebSocketState.Open) return;

            try
            {
                string json = JsonSerializer.Serialize(payload, CollabJsonContext.Default.CollabPayload);
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CollabClient] Error sending message: {ex.Message}");
            }
        }

        private async Task ReadLoop()
        {
            byte[] buffer = new byte[65536];
            using var ms = new MemoryStream();

            while (_isConnected && _webSocket != null && _webSocket.State == WebSocketState.Open && !_cts.Token.IsCancellationRequested)
            {
                try
                {
                    var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Text || result.MessageType == WebSocketMessageType.Binary)
                    {
                        ms.Write(buffer, 0, result.Count);

                        if (result.EndOfMessage)
                        {
                            ms.Position = 0;
                            CollabPayload? payload = null;

                            if (ms.TryGetBuffer(out var segment))
                            {
                                payload = JsonSerializer.Deserialize(segment.AsSpan(), CollabJsonContext.Default.CollabPayload);
                            }
                            else
                            {
                                payload = JsonSerializer.Deserialize(ms.ToArray(), CollabJsonContext.Default.CollabPayload);
                            }

                            if (payload != null)
                            {
                                ProcessServerPayload(payload);
                            }

                            ms.SetLength(0); // Clear for next message
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (_isConnected)
                    {
                        Console.WriteLine($"[CollabClient] Read loop exception: {ex.Message}");
                    }
                    break;
                }
            }
            _isConnected = false;
        }

        private void ProcessServerPayload(CollabPayload payload)
        {
            if (_crdtDoc == null) return;

            switch (payload.Type)
            {
                case CollabMessageTypes.Sync:
                    {
                        var syncState = JsonSerializer.Deserialize(payload.Data, CollabJsonContext.Default.CollabSyncState);
                        if (syncState != null)
                        {
                            _crdtDoc.InitializeFromState(syncState.Nodes);
                            string text = _crdtDoc.GetText();
                            SyncReceived?.Invoke(text);
                        }
                        break;
                    }
                case CollabMessageTypes.Insert:
                    {
                        var msg = JsonSerializer.Deserialize(payload.Data, CollabJsonContext.Default.CollabInsertMessage);
                        if (msg != null)
                        {
                            bool inserted = _crdtDoc.ApplyRemoteInsert(msg);
                            if (inserted)
                            {
                                int visibleOffset = _crdtDoc.GetVisibleOffsetOf(msg.Position, msg.ClientId);
                                if (visibleOffset >= 0)
                                {
                                    RemoteInsertReceived?.Invoke(visibleOffset, msg.Value);
                                }
                            }
                        }
                        break;
                    }
                case CollabMessageTypes.Delete:
                    {
                        var msg = JsonSerializer.Deserialize(payload.Data, CollabJsonContext.Default.CollabDeleteMessage);
                        if (msg != null)
                        {
                            int visibleOffset = _crdtDoc.GetVisibleOffsetOf(msg.Position, msg.ClientId);
                            bool deleted = _crdtDoc.ApplyRemoteDelete(msg);
                            if (deleted && visibleOffset >= 0)
                            {
                                RemoteDeleteReceived?.Invoke(visibleOffset);
                            }
                        }
                        break;
                    }
                case CollabMessageTypes.Cursor:
                    {
                        var msg = JsonSerializer.Deserialize(payload.Data, CollabJsonContext.Default.CollabCursorMessage);
                        if (msg != null)
                        {
                            RemoteCursorMoved?.Invoke(msg);
                        }
                        break;
                    }
            }
        }

        public static string GetUserColorHex(string clientId)
        {
            // Compute a stable color based on clientId hash
            int hash = clientId.GetHashCode();
            // Generate beautiful pastel colors
            int r = (hash & 0xFF0000) >> 16;
            int g = (hash & 0x00FF00) >> 8;
            int b = (hash & 0x0000FF);
            // Mix with white to pastel-ize
            r = (r + 255) / 2;
            g = (g + 255) / 2;
            b = (b + 255) / 2;
            return $"#{r:X2}{g:X2}{b:X2}";
        }

        public void Dispose()
        {
            Disconnect();
        }
    }
}
