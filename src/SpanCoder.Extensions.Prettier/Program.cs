using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SpanCoder.Contracts;

namespace SpanCoder.Extensions.Prettier
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

            Console.WriteLine($"[PrettierPlugin] Connecting to port {port}...");

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
                    Console.WriteLine($"[PrettierPlugin] Manifest not found at {manifestPath}");
                    return;
                }

                string token = Environment.GetEnvironmentVariable("SPANCODER_EXT_TOKEN") ?? "";
                string manifestJson = File.ReadAllText(manifestPath);
                byte[] jsonBytes = Encoding.UTF8.GetBytes(manifestJson);
                byte[] buffer = new byte[BinaryMessageSerializer.HeaderSize + sizeof(int) + token.Length * sizeof(char) + sizeof(int) + jsonBytes.Length];
                int len = BinaryMessageSerializer.WriteRegisterExtension(buffer, token, jsonBytes);

                Console.WriteLine("[PrettierPlugin] Registering extension...");
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

                    if (header.Type == MessageTypes.ExecuteExtensionCommand)
                    {
                        string commandId = BinaryMessageSerializer.ParseExecuteExtensionCommand(payload);
                        Console.WriteLine($"[PrettierPlugin] Received command: {commandId}");
                    }
                    else if (header.Type == MessageTypes.ExtensionSettingChanged)
                    {
                        BinaryMessageSerializer.ParseExtensionSettingChanged(payload, out string settingId, out string value);
                        Console.WriteLine($"[PrettierPlugin] Setting changed: {settingId} = {value}");
                    }
                    else if (header.Type == MessageTypes.FormatDocumentRequest)
                    {
                        BinaryMessageSerializer.ParseFormatDocumentRequest(payload, out int docId, out string filePath, out string content);
                        Console.WriteLine($"[PrettierPlugin] Format request for: {filePath} (doc {docId})");

                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                string formatted = await FormatContentAsync(filePath, content);
                                
                                int respSizeNeeded = BinaryMessageSerializer.HeaderSize + sizeof(int) + formatted.Length * sizeof(char);
                                byte[] respBuffer = new byte[respSizeNeeded];
                                int respLen = BinaryMessageSerializer.WriteFormatDocumentResponse(respBuffer, docId, formatted);

                                await SendMessageAsync(stream, respBuffer, respLen);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[PrettierPlugin] Error processing format request: {ex.Message}");
                            }
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PrettierPlugin] Error: {ex.Message}");
            }
            finally
            {
                stream?.Dispose();
                client?.Dispose();
            }
        }

        private static async Task<string> FormatContentAsync(string filePath, string content)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c npx prettier --stdin-filepath \"{filePath}\"",
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var proc = Process.Start(psi))
                {
                    if (proc != null)
                    {
                        using (var writer = proc.StandardInput)
                        {
                            await writer.WriteAsync(content);
                        }

                        string output = await proc.StandardOutput.ReadToEndAsync();
                        string error = await proc.StandardError.ReadToEndAsync();

                        await proc.WaitForExitAsync();

                        if (proc.ExitCode == 0 && !string.IsNullOrEmpty(output))
                        {
                            return output;
                        }
                        else
                        {
                            Console.WriteLine($"[PrettierPlugin] Prettier process failed (ExitCode: {proc.ExitCode}). Error: {error}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PrettierPlugin] Prettier execution failed: {ex.Message}. Falling back to mock.");
            }

            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext == ".json")
            {
                try
                {
                    using (var doc = JsonDocument.Parse(content))
                    {
                        var options = new JsonSerializerOptions { WriteIndented = true };
                        return JsonSerializer.Serialize(doc, options);
                    }
                }
                catch
                {
                }
            }
            else if (ext == ".js" || ext == ".css")
            {
                return FormatMockJs(content);
            }

            return content;
        }

        private static string FormatMockJs(string code)
        {
            var lines = code.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            int indentLevel = 0;
            var result = new System.Collections.Generic.List<string>();
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("}"))
                {
                    indentLevel = Math.Max(0, indentLevel - 1);
                }
                string indent = new string(' ', indentLevel * 4);
                result.Add(trimmed.Length > 0 ? indent + trimmed : "");
                if (trimmed.EndsWith("{"))
                {
                    indentLevel++;
                }
            }
            return string.Join(Environment.NewLine, result);
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
