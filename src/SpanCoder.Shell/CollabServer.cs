using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SpanCoder.Contracts;

namespace SpanCoder.Shell
{
    public class CollabServer
    {
        private readonly TcpListener _listener;
        private readonly ConcurrentDictionary<string, WebSocket> _clients = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly CrdtDocument _crdtDoc;
        private readonly int _port;
        private bool _isRunning;

        public CrdtDocument Document => _crdtDoc;

        public CollabServer(int port)
        {
            _port = port;
            _listener = new TcpListener(IPAddress.Any, port);
            _crdtDoc = new CrdtDocument("host_" + Guid.NewGuid().ToString("N").Substring(0, 6));
        }

        public void Start()
        {
            _isRunning = true;
            _listener.Start();
            Task.Run(AcceptLoop);
            Console.WriteLine($"[CollabServer] Started listening on port {_port}...");
        }

        public void Stop()
        {
            _isRunning = false;
            _cts.Cancel();
            _listener.Stop();
            foreach (var client in _clients.Values)
            {
                try
                {
                    client.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server stopping", CancellationToken.None).Wait();
                }
                catch { }
            }
            _clients.Clear();
            Console.WriteLine("[CollabServer] Stopped.");
        }

        private async Task AcceptLoop()
        {
            while (_isRunning && !_cts.Token.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    _ = Task.Run(() => HandleTcpClient(client));
                }
                catch
                {
                    break;
                }
            }
        }

        private async Task HandleTcpClient(TcpClient client)
        {
            string clientId = Guid.NewGuid().ToString("N").Substring(0, 8);
            using var stream = client.GetStream();

            try
            {
                // 1. Perform WebSocket Handshake
                byte[] buffer = new byte[8192];
                int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, _cts.Token);
                string headers = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                if (!headers.Contains("Upgrade: websocket"))
                {
                    client.Close();
                    return;
                }

                var match = Regex.Match(headers, "Sec-WebSocket-Key: (.*)");
                if (!match.Success)
                {
                    client.Close();
                    return;
                }

                string key = match.Groups[1].Value.Trim();
                string acceptKey = Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));

                string response = "HTTP/1.1 101 Switching Protocols\r\n" +
                                  "Upgrade: websocket\r\n" +
                                  "Connection: Upgrade\r\n" +
                                  "Sec-WebSocket-Accept: " + acceptKey + "\r\n\r\n";

                byte[] responseBytes = Encoding.UTF8.GetBytes(response);
                await stream.WriteAsync(responseBytes, 0, responseBytes.Length, _cts.Token);

                // 2. Upgrade stream to WebSocket
                using WebSocket webSocket = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(30));
                _clients[clientId] = webSocket;

                Console.WriteLine($"[CollabServer] Client '{clientId}' connected.");

                // 3. Send Sync State to new client
                var syncState = new CollabSyncState
                {
                    Nodes = _crdtDoc.GetState()
                };
                var syncPayload = new CollabPayload
                {
                    Type = CollabMessageTypes.Sync,
                    Data = JsonSerializer.Serialize(syncState, CollabJsonContext.Default.CollabSyncState)
                };
                byte[] syncBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(syncPayload, CollabJsonContext.Default.CollabPayload));
                await webSocket.SendAsync(new ArraySegment<byte>(syncBytes), WebSocketMessageType.Text, true, _cts.Token);

                // 4. Run incoming message loop for this client
                byte[] receiveBuffer = new byte[65536];
                while (webSocket.State == WebSocketState.Open && !_cts.Token.IsCancellationRequested)
                {
                    var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), _cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        string messageStr = Encoding.UTF8.GetString(receiveBuffer, 0, result.Count);
                        var payload = JsonSerializer.Deserialize(messageStr, CollabJsonContext.Default.CollabPayload);
                        if (payload != null)
                        {
                            await ProcessClientMessage(clientId, payload);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CollabServer] Exception handling client '{clientId}': {ex.Message}");
            }
            finally
            {
                _clients.TryRemove(clientId, out _);
                client.Close();
                Console.WriteLine($"[CollabServer] Client '{clientId}' disconnected.");
                
                // Broadcast cursor removal
                var removeCursor = new CollabCursorMessage
                {
                    ClientId = clientId,
                    Username = "",
                    Line = -1,
                    Character = -1
                };
                var payload = new CollabPayload
                {
                    Type = CollabMessageTypes.Cursor,
                    Data = JsonSerializer.Serialize(removeCursor, CollabJsonContext.Default.CollabCursorMessage)
                };
                await BroadcastAsync(clientId, payload);
            }
        }

        private async Task ProcessClientMessage(string senderId, CollabPayload payload)
        {
            switch (payload.Type)
            {
                case CollabMessageTypes.Insert:
                    {
                        var msg = JsonSerializer.Deserialize(payload.Data, CollabJsonContext.Default.CollabInsertMessage);
                        if (msg != null)
                        {
                            _crdtDoc.ApplyRemoteInsert(msg);
                            await BroadcastAsync(senderId, payload);
                        }
                        break;
                    }
                case CollabMessageTypes.Delete:
                    {
                        var msg = JsonSerializer.Deserialize(payload.Data, CollabJsonContext.Default.CollabDeleteMessage);
                        if (msg != null)
                        {
                            _crdtDoc.ApplyRemoteDelete(msg);
                            await BroadcastAsync(senderId, payload);
                        }
                        break;
                    }
                case CollabMessageTypes.Cursor:
                    {
                        var msg = JsonSerializer.Deserialize(payload.Data, CollabJsonContext.Default.CollabCursorMessage);
                        if (msg != null)
                        {
                            // Overwrite client ID to enforce uniqueness
                            msg.ClientId = senderId;
                            payload.Data = JsonSerializer.Serialize(msg, CollabJsonContext.Default.CollabCursorMessage);
                            await BroadcastAsync(senderId, payload);
                        }
                        break;
                    }
            }
        }

        private async Task BroadcastAsync(string senderId, CollabPayload payload)
        {
            byte[] messageBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, CollabJsonContext.Default.CollabPayload));
            var segment = new ArraySegment<byte>(messageBytes);

            foreach (var kvp in _clients)
            {
                if (kvp.Key != senderId && kvp.Value.State == WebSocketState.Open)
                {
                    try
                    {
                        await kvp.Value.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
                    }
                    catch { }
                }
            }
        }
    }
}
