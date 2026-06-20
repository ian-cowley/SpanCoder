using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SpanCoder.Contracts;
using SpanCoder.Engine;
using SpanCoder.Shell;
using Xunit;

namespace SpanCoder.Tests
{
    public class HardeningAndOptimizationsTests
    {
        [Fact]
        public void TestDpapiHelperFallbackRandomizedAndLegacy()
        {
            // 1. Test fallback encrypt / decrypt via reflection
            var encryptMethod = typeof(DpapiHelper).GetMethod("EncryptFallback", BindingFlags.NonPublic | BindingFlags.Static);
            var decryptMethod = typeof(DpapiHelper).GetMethod("DecryptFallback", BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(encryptMethod);
            Assert.NotNull(decryptMethod);

            string plaintext = "SuperSecretApiKey123!";

            // Encrypting twice should yield different ciphertexts due to randomized IVs
            string cipher1 = (string)encryptMethod.Invoke(null, new object[] { plaintext })!;
            string cipher2 = (string)encryptMethod.Invoke(null, new object[] { plaintext })!;

            Assert.NotEmpty(cipher1);
            Assert.NotEmpty(cipher2);
            Assert.NotEqual(cipher1, cipher2);

            // Both should decrypt back to plaintext
            string decrypted1 = (string)decryptMethod.Invoke(null, new object[] { cipher1 })!;
            string decrypted2 = (string)decryptMethod.Invoke(null, new object[] { cipher2 })!;

            Assert.Equal(plaintext, decrypted1);
            Assert.Equal(plaintext, decrypted2);

            // 2. Test legacy IV compatibility
            // We manually construct legacy encryption (with static IV) to see if DecryptFallback can decrypt it.
            var getKeyMethod = typeof(DpapiHelper).GetMethod("GetFallbackKey", BindingFlags.NonPublic | BindingFlags.Static);
            var getIvMethod = typeof(DpapiHelper).GetMethod("GetFallbackIv", BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(getKeyMethod);
            Assert.NotNull(getIvMethod);

            byte[] key = (byte[])getKeyMethod.Invoke(null, null)!;
            byte[] legacyIv = (byte[])getIvMethod.Invoke(null, null)!;

            using var aes = System.Security.Cryptography.Aes.Create();
            aes.Key = key;
            aes.IV = legacyIv;

            using var ms = new MemoryStream();
            using (var cs = new System.Security.Cryptography.CryptoStream(ms, aes.CreateEncryptor(), System.Security.Cryptography.CryptoStreamMode.Write))
            {
                byte[] plainBytes = Encoding.UTF8.GetBytes(plaintext);
                cs.Write(plainBytes, 0, plainBytes.Length);
            }
            string legacyCipher = Convert.ToBase64String(ms.ToArray());

            // Attempt to decrypt legacy ciphertext using DecryptFallback
            string legacyDecrypted = (string)decryptMethod.Invoke(null, new object[] { legacyCipher })!;
            Assert.Equal(plaintext, legacyDecrypted);
        }

        [Fact]
        public async Task TestAiToolsPathTraversalGuard()
        {
            // Outside directory should fail path traversal guard
            string outsidePath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "OutsideFile.txt"));

            // 1. read_file should fail
            string readResult = await AiToolsRegistry.ExecuteToolAsync("read_file", JsonSerializer.Serialize(new { path = outsidePath }));
            Assert.Contains("outside the workspace root", readResult);
            Assert.Contains("Access denied", readResult);

            // 2. write_file should fail
            string writeResult = await AiToolsRegistry.ExecuteToolAsync("write_file", JsonSerializer.Serialize(new { path = outsidePath, content = "evil" }));
            Assert.Contains("outside the workspace root", writeResult);
            Assert.Contains("Access denied", writeResult);

            // 3. edit_file_replace should fail
            string editResult = await AiToolsRegistry.ExecuteToolAsync("edit_file_replace", JsonSerializer.Serialize(new { path = outsidePath, target = "foo", replacement = "bar" }));
            Assert.Contains("outside the workspace root", editResult);
            Assert.Contains("Access denied", editResult);
        }

        [Fact]
        public async Task TestCollabClientWebSocketReassembly()
        {
            var client = new CollabClient("TestUser");

            var mockSocket = new MockWebSocket();

            var webSocketField = typeof(CollabClient).GetField("_webSocket", BindingFlags.NonPublic | BindingFlags.Instance);
            var isConnectedField = typeof(CollabClient).GetField("_isConnected", BindingFlags.NonPublic | BindingFlags.Instance);
            var crdtDocField = typeof(CollabClient).GetField("_crdtDoc", BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.NotNull(webSocketField);
            Assert.NotNull(isConnectedField);
            Assert.NotNull(crdtDocField);

            webSocketField.SetValue(client, mockSocket);
            isConnectedField.SetValue(client, true);
            crdtDocField.SetValue(client, new CrdtDocument(client.ClientId));

            // Construct a Sync payload
            var syncState = new CollabSyncState
            {
                Nodes = new List<CrdtNodeState>()
            };
            var syncPayload = new CollabPayload
            {
                Type = CollabMessageTypes.Sync,
                Data = JsonSerializer.Serialize(syncState, CollabJsonContext.Default.CollabSyncState)
            };
            string jsonPayload = JsonSerializer.Serialize(syncPayload, CollabJsonContext.Default.CollabPayload);

            // Split the jsonPayload into two halves
            int mid = jsonPayload.Length / 2;
            string part1 = jsonPayload.Substring(0, mid);
            string part2 = jsonPayload.Substring(mid);

            // Enqueue the two fragments
            mockSocket.EnqueueFrame(part1, false);
            mockSocket.EnqueueFrame(part2, true);
            mockSocket.EnqueueClose(); // To terminate the loop

            // Set up a TaskCompletionSource to await the SyncReceived event
            var tcs = new TaskCompletionSource<string>();
            client.SyncReceived += (text) =>
            {
                tcs.TrySetResult(text);
            };

            // Get ReadLoop method and run it
            var readLoopMethod = typeof(CollabClient).GetMethod("ReadLoop", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(readLoopMethod);

            var readLoopTask = (Task)readLoopMethod.Invoke(client, null)!;

            // Wait for the read loop to complete or time out
            var timeoutTask = Task.Delay(5000);
            var completedTask = await Task.WhenAny(readLoopTask, timeoutTask);

            Assert.Same(readLoopTask, completedTask);

            // Verify that the sync received event was fired and we successfully deserialized/processed the reassembled message
            Assert.True(tcs.Task.IsCompleted);
            string resultText = await tcs.Task;
            Assert.Equal("", resultText); // Since nodes was empty
        }
    }

    public class MockWebSocket : WebSocket
    {
        private readonly Queue<WebSocketFrame> _frames = new();
        private WebSocketState _state = WebSocketState.Open;

        public class WebSocketFrame
        {
            public byte[] Data { get; set; } = Array.Empty<byte>();
            public bool EndOfMessage { get; set; }
            public WebSocketMessageType MessageType { get; set; } = WebSocketMessageType.Text;
        }

        public void EnqueueFrame(string text, bool endOfMessage)
        {
            _frames.Enqueue(new WebSocketFrame
            {
                Data = Encoding.UTF8.GetBytes(text),
                EndOfMessage = endOfMessage,
                MessageType = WebSocketMessageType.Text
            });
        }

        public void EnqueueClose()
        {
            _frames.Enqueue(new WebSocketFrame
            {
                MessageType = WebSocketMessageType.Close
            });
        }

        public override WebSocketState State => _state;
        public override string? SubProtocol => null;
        public override WebSocketCloseStatus? CloseStatus => WebSocketCloseStatus.NormalClosure;
        public override string? CloseStatusDescription => "Done";

        public override void Abort() => _state = WebSocketState.Aborted;

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override void Dispose() { }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (_state != WebSocketState.Open || _frames.Count == 0)
            {
                return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            }

            var frame = _frames.Dequeue();
            if (frame.MessageType == WebSocketMessageType.Close)
            {
                _state = WebSocketState.Closed;
                return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            }

            int count = Math.Min(buffer.Count, frame.Data.Length);
            Array.Copy(frame.Data, 0, buffer.Array!, buffer.Offset, count);
            return Task.FromResult(new WebSocketReceiveResult(count, frame.MessageType, frame.EndOfMessage));
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
