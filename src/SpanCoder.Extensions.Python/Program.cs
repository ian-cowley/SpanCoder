using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SpanCoder.Contracts;

namespace SpanCoder.Extensions.Python
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

            Console.WriteLine($"[PythonPlugin] Connecting to port {port}...");

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
                    Console.WriteLine($"[PythonPlugin] Manifest not found at {manifestPath}");
                    return;
                }

                string token = Environment.GetEnvironmentVariable("SPANCODER_EXT_TOKEN") ?? "";
                string manifestJson = File.ReadAllText(manifestPath);
                byte[] jsonBytes = Encoding.UTF8.GetBytes(manifestJson);
                byte[] buffer = new byte[BinaryMessageSerializer.HeaderSize + sizeof(int) + token.Length * sizeof(char) + sizeof(int) + jsonBytes.Length];
                int len = BinaryMessageSerializer.WriteRegisterExtension(buffer, token, jsonBytes);

                Console.WriteLine("[PythonPlugin] Registering extension...");
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
                        Console.WriteLine($"[PythonPlugin] Received command: {commandId} for path {activeFilePath}");

                        if (commandId == "python.run")
                        {
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await UpdateStatusBarAsync(stream, "python-status", "Python: Running...", "Executing python script");
                                    
                                    var result = await RunPythonScriptAsync(activeFilePath, activeContent);
                                    
                                    string text = result.Success ? "Python: Run Success" : "Python: Run Failed";
                                    string tooltip = result.Output;
                                    if (tooltip.Length > 500)
                                    {
                                        tooltip = tooltip.Substring(0, 500) + "...";
                                    }
                                    
                                    await UpdateStatusBarAsync(stream, "python-status", text, tooltip);
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"[PythonPlugin] Error running script: {ex.Message}");
                                    await UpdateStatusBarAsync(stream, "python-status", "Python: Run Error", ex.Message);
                                }
                            });
                        }
                    }
                    else if (header.Type == MessageTypes.ExtensionSettingChanged)
                    {
                        BinaryMessageSerializer.ParseExtensionSettingChanged(payload, out string settingId, out string value);
                        Console.WriteLine($"[PythonPlugin] Setting changed: {settingId} = {value}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PythonPlugin] Error: {ex.Message}");
            }
            finally
            {
                stream?.Dispose();
                client?.Dispose();
            }
        }

        private static async Task<(bool Success, string Output)> RunPythonScriptAsync(string filePath, string content)
        {
            string[] executables = { "python", "python3", "py" };
            Exception? lastException = null;

            foreach (var exe in executables)
            {
                try
                {
                    bool useStdin = string.IsNullOrEmpty(filePath) || !File.Exists(filePath);
                    var psi = new ProcessStartInfo
                    {
                        FileName = exe,
                        Arguments = useStdin ? "-" : $"\"{filePath}\"",
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
                            if (useStdin)
                            {
                                using (var writer = proc.StandardInput)
                                {
                                    await writer.WriteAsync(content);
                                }
                            }
                            else
                            {
                                proc.StandardInput.Close();
                            }

                            string stdout = await proc.StandardOutput.ReadToEndAsync();
                            string stderr = await proc.StandardError.ReadToEndAsync();

                            await proc.WaitForExitAsync();

                            string combined = string.IsNullOrEmpty(stderr) ? stdout : $"{stdout}\nError:\n{stderr}";
                            if (string.IsNullOrEmpty(combined))
                            {
                                combined = "(No output)";
                            }

                            return (proc.ExitCode == 0, combined);
                        }
                    }
                }
                catch (Exception ex)
                {
                    lastException = ex;
                }
            }

            return (false, $"Python executable not found on system PATH. Error details: {lastException?.Message}");
        }

        private static async Task UpdateStatusBarAsync(NetworkStream stream, string itemId, string text, string tooltip = "", string commandId = "")
        {
            byte[] temp = new byte[BinaryMessageSerializer.HeaderSize + 16 + (itemId.Length + text.Length + tooltip.Length + commandId.Length) * sizeof(char)];
            int len = BinaryMessageSerializer.WriteUpdateExtensionStatusBarItem(temp, itemId, text, tooltip, commandId);
            await SendMessageAsync(stream, temp, len);
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
