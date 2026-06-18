using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using SpanCoder.Contracts;

namespace SpanCoder.Extensions.Languages
{
    class Program
    {
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

            Console.WriteLine($"[LanguagesPlugin] Connecting to port {port}...");
            
            TcpClient? client = null;
            NetworkStream? stream = null;
            try
            {
                client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", port);
                stream = client.GetStream();

                // Read plugin.json
                string exeDir = AppDomain.CurrentDomain.BaseDirectory;
                string manifestPath = Path.Combine(exeDir, "plugin.json");
                if (!File.Exists(manifestPath))
                {
                    Console.WriteLine($"[LanguagesPlugin] Manifest not found at {manifestPath}");
                    return;
                }

                string manifestJson = File.ReadAllText(manifestPath);
                byte[] jsonBytes = Encoding.UTF8.GetBytes(manifestJson);
                byte[] buffer = new byte[BinaryMessageSerializer.HeaderSize + sizeof(int) + jsonBytes.Length];
                int len = BinaryMessageSerializer.WriteRegisterExtension(buffer, jsonBytes);

                Console.WriteLine("[LanguagesPlugin] Registering extension...");
                await stream.WriteAsync(buffer, 0, len);
                await stream.FlushAsync();

                // Start Read Loop
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

                    if (header.Type == MessageTypes.ExecuteExtensionCommand)
                    {
                        string commandId = BinaryMessageSerializer.ParseExecuteExtensionCommand(payload);
                        Console.WriteLine($"[LanguagesPlugin] Received command: {commandId}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LanguagesPlugin] Error: {ex.Message}");
            }
            finally
            {
                stream?.Dispose();
                client?.Dispose();
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
