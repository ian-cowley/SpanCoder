using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SpanCoder.Contracts;

namespace SpanCoder.Extensions.HtmlPreview
{
    class Program
    {
        private static readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);

        static async Task Main(string[] args)
        {
            int port = 0;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--port" && i + 1 < args.Length)
                {
                    int.TryParse(args[i + 1], out port);
                }
            }

            if (port == 0)
            {
                Console.WriteLine("Usage: plugin --port <port>");
                return;
            }

            Console.WriteLine($"[HtmlPreviewPlugin] Connecting to port {port}...");

            TcpClient? client = null;
            NetworkStream? stream = null;
            try
            {
                client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", port);
                stream = client.GetStream();

                string exeDir = AppDomain.CurrentDomain.BaseDirectory;
                string manifestPath = Path.Combine(exeDir, "plugin.json");
                if (!File.Exists(manifestPath))
                {
                    Console.WriteLine($"[HtmlPreviewPlugin] Manifest not found at {manifestPath}");
                    return;
                }

                string token = Environment.GetEnvironmentVariable("SPANCODER_EXT_TOKEN") ?? "";
                string manifestJson = File.ReadAllText(manifestPath);
                byte[] jsonBytes = Encoding.UTF8.GetBytes(manifestJson);
                byte[] buffer = new byte[BinaryMessageSerializer.HeaderSize + sizeof(int) + token.Length * sizeof(char) + sizeof(int) + jsonBytes.Length];
                int len = BinaryMessageSerializer.WriteRegisterExtension(buffer, token, jsonBytes);

                Console.WriteLine("[HtmlPreviewPlugin] Registering extension...");
                await SendMessageAsync(stream, buffer, len);

                byte[] headerBuffer = new byte[BinaryMessageSerializer.HeaderSize];
                while (client.Connected)
                {
                    int readBytes = await ReadExactlyAsync(stream, headerBuffer, 0, headerBuffer.Length);
                    if (readBytes <= 0) break;

                    if (!BinaryMessageSerializer.TryParseHeader(headerBuffer, out var header))
                    {
                        break;
                    }

                    byte[] payload = new byte[header.Length];
                    Array.Copy(headerBuffer, 0, payload, 0, headerBuffer.Length);
                    if (header.Length > headerBuffer.Length)
                    {
                        await ReadExactlyAsync(stream, payload, headerBuffer.Length, header.Length - headerBuffer.Length);
                    }

                    if (header.Type == MessageTypes.ExecuteExtensionCommandWithContext)
                    {
                        BinaryMessageSerializer.ParseExecuteExtensionCommandWithContext(payload, out string commandId, out string activeFilePath, out string activeContent);
                        Console.WriteLine($"[HtmlPreviewPlugin] Received command: {commandId} for path {activeFilePath}");

                        if (commandId == "html-preview.show")
                        {
                            string content = activeContent;
                            if (string.IsNullOrEmpty(content))
                            {
                                content = "<h1>No active document</h1><p>Open an HTML file to preview it.</p>";
                            }

                            // Send panel content update
                            int updateSize = BinaryMessageSerializer.HeaderSize + 8 + "html-preview-panel".Length * sizeof(char) + content.Length * sizeof(char);
                            byte[] updateBuf = new byte[updateSize];
                            int written = BinaryMessageSerializer.WriteUpdateExtensionPanel(updateBuf, "html-preview-panel", content);

                            await SendMessageAsync(stream, updateBuf, written);
                        }
                    }
                    else if (header.Type == MessageTypes.ExtensionSettingChanged)
                    {
                        BinaryMessageSerializer.ParseExtensionSettingChanged(payload, out string settingId, out string value);
                        Console.WriteLine($"[HtmlPreviewPlugin] Setting changed: {settingId} = {value}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HtmlPreviewPlugin] Error: {ex.Message}");
            }
            finally
            {
                stream?.Dispose();
                client?.Dispose();
            }
        }

        private static async Task SendMessageAsync(NetworkStream stream, byte[] buffer, int length)
        {
            await _writeLock.WaitAsync();
            try
            {
                await stream.WriteAsync(buffer, 0, length);
                await stream.FlushAsync();
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private static async Task<int> ReadExactlyAsync(NetworkStream stream, byte[] buffer, int offset, int count)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(offset + totalRead, count - totalRead));
                if (read <= 0) return totalRead;
                totalRead += read;
            }
            return totalRead;
        }
    }
}
