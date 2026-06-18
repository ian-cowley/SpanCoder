using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using SpanCoder.Contracts;

namespace SpanCoder.Engine
{
    public class Program
    {
        public static void Main(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--mock-lsp")
                {
                    MockLspServer.Run();
                    return;
                }
                if (args[i] == "--mock-dap")
                {
                    MockDapServer.Run();
                    return;
                }
            }

            int port = 0;
            for (int i = 0; i < args.Length; i++)
            {
                if ((args[i] == "--port" || args[i] == "-p") && i + 1 < args.Length)
                {
                    int.TryParse(args[i + 1], out port);
                }
            }

            if (port == 0)
            {
                Console.WriteLine("Usage: SpanCoder.Engine --port <port>");
                return;
            }

            Console.WriteLine($"[Engine] Connecting to host on port {port}...");
            TcpClient client;
            try
            {
                client = new TcpClient();
                client.Connect("127.0.0.1", port);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Engine] Failed to connect: {ex.Message}");
                return;
            }

            Console.WriteLine("[Engine] Connected. Starting message loop.");
            using var stream = client.GetStream();
            var engineHost = new EngineHost();
            object socketLock = new object();

            // Subscribe to output events and write back to the socket
            engineHost.MessageReceived += (responsePayload) =>
            {
                try
                {
                    lock (socketLock)
                    {
                        stream.Write(responsePayload, 0, responsePayload.Length);
                        stream.Flush();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Engine] Error writing response: {ex.Message}");
                }
            };

            engineHost.Start();

            byte[] headerBuffer = new byte[BinaryMessageSerializer.HeaderSize];
            try
            {
                while (client.Connected)
                {
                    // Read header (13 bytes)
                    ReadExactly(stream, headerBuffer, 0, headerBuffer.Length);
                    
                    // Parse header length (total packet size)
                    if (!BinaryMessageSerializer.TryParseHeader(headerBuffer, out var header))
                    {
                        throw new InvalidDataException("Failed to parse message header.");
                    }

                    int messageLength = header.Length;
                    if (messageLength < headerBuffer.Length || messageLength > 100 * 1024 * 1024)
                    {
                        throw new InvalidDataException($"Invalid message length: {messageLength}");
                    }

                    byte[] fullMessage = new byte[messageLength];
                    Array.Copy(headerBuffer, 0, fullMessage, 0, headerBuffer.Length);

                    if (messageLength > headerBuffer.Length)
                    {
                        ReadExactly(stream, fullMessage, headerBuffer.Length, messageLength - headerBuffer.Length);
                    }

                    // Enqueue message into engine host
                    engineHost.Input.TryWrite(fullMessage);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Engine] Connection closed or error: {ex.Message}");
            }
            finally
            {
                engineHost.Stop();
                client.Close();
                Console.WriteLine("[Engine] Shutdown completed.");
            }
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
    }
}
