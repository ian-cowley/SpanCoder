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
                if (args[i] == "--mock-silicon-dap")
                {
                    MockSiliconDapServer.Run();
                    return;
                }
            }

            int port = 0;
            bool listen = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--listen" || args[i] == "-l")
                {
                    listen = true;
                }
                else if ((args[i] == "--port" || args[i] == "-p") && i + 1 < args.Length)
                {
                    int.TryParse(args[i + 1], out port);
                }
            }

            if (port == 0)
            {
                Console.WriteLine("Usage: SpanCoder.Engine --port <port> [--listen]");
                return;
            }

            if (listen)
            {
                var listener = new TcpListener(System.Net.IPAddress.Any, port);
                listener.Start();
                Console.WriteLine($"[Engine] Listening for connections on port {port}...");
                while (true)
                {
                    try
                    {
                        var client = listener.AcceptTcpClient();
                        HandleClient(client);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Engine] Listener error: {ex.Message}");
                        Thread.Sleep(1000);
                    }
                }
            }
            else
            {
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

                HandleClient(client);
            }
        }

        private static void HandleClient(TcpClient client)
        {
            Console.WriteLine("[Engine] Client connected. Starting message loop.");
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
                Console.WriteLine("[Engine] Client disconnected.");
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
