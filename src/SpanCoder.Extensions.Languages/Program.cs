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
        private static readonly System.Threading.SemaphoreSlim _writeLock = new(1, 1);

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

                string token = Environment.GetEnvironmentVariable("SPANCODER_EXT_TOKEN") ?? "";
                string manifestJson = File.ReadAllText(manifestPath);
                byte[] jsonBytes = Encoding.UTF8.GetBytes(manifestJson);
                byte[] buffer = new byte[BinaryMessageSerializer.HeaderSize + sizeof(int) + token.Length * sizeof(char) + sizeof(int) + jsonBytes.Length];
                int len = BinaryMessageSerializer.WriteRegisterExtension(buffer, token, jsonBytes);

                Console.WriteLine("[LanguagesPlugin] Registering extension...");
                await SendMessageAsync(stream, buffer, len);

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

                    if (header.Type == MessageTypes.ExecuteExtensionCommand || header.Type == MessageTypes.ExecuteExtensionCommandWithContext)
                    {
                        string commandId;
                        string activeFilePath = "";
                        string activeContent = "";

                        if (header.Type == MessageTypes.ExecuteExtensionCommandWithContext)
                        {
                            BinaryMessageSerializer.ParseExecuteExtensionCommandWithContext(payload, out commandId, out activeFilePath, out activeContent);
                        }
                        else
                        {
                            commandId = BinaryMessageSerializer.ParseExecuteExtensionCommand(payload);
                        }

                        Console.WriteLine($"[LanguagesPlugin] Received command: {commandId}");

                        if (commandId == "languages.runPython")
                        {
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await UpdateStatusBarAsync(stream, "languages-status", "Py: Running...", "Python script is executing");
                                    var result = await RunPythonScriptAsync(activeFilePath, activeContent);
                                    string text = result.Success ? "Py: Run Success" : "Py: Run Failed";
                                    string tooltip = result.Output;
                                    if (tooltip.Length > 500) tooltip = tooltip.Substring(0, 500) + "...";
                                    await UpdateStatusBarAsync(stream, "languages-status", text, tooltip);
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"[LanguagesPlugin] Error running script: {ex.Message}");
                                    await UpdateStatusBarAsync(stream, "languages-status", "Py: Run Error", ex.Message);
                                }
                            });
                        }
                        else if (commandId == "languages.cargoBuild")
                        {
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await UpdateStatusBarAsync(stream, "languages-status", "Cargo: Building...", "Cargo build is executing");
                                    var result = await RunCargoBuildAsync(activeFilePath);
                                    string text = result.Success ? "Cargo: Build Success" : "Cargo: Build Failed";
                                    string tooltip = result.Output;
                                    if (tooltip.Length > 500) tooltip = tooltip.Substring(0, 500) + "...";
                                    await UpdateStatusBarAsync(stream, "languages-status", text, tooltip);
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"[LanguagesPlugin] Error running build: {ex.Message}");
                                    await UpdateStatusBarAsync(stream, "languages-status", "Cargo: Build Error", ex.Message);
                                }
                            });
                        }
                    }
                    else if (header.Type == MessageTypes.ExtensionSettingChanged)
                    {
                        BinaryMessageSerializer.ParseExtensionSettingChanged(payload, out string settingId, out string value);
                        Console.WriteLine($"[LanguagesPlugin] Setting changed: {settingId} = {value}");
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

        private static async Task<(bool Success, string Output)> RunPythonScriptAsync(string filePath, string content)
        {
            string[] executables = { "python", "python3", "py" };
            Exception? lastException = null;

            foreach (var exe in executables)
            {
                try
                {
                    bool useStdin = string.IsNullOrEmpty(filePath) || !File.Exists(filePath);
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = exe,
                        Arguments = useStdin ? "-" : $"\"{filePath}\"",
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using (var proc = System.Diagnostics.Process.Start(psi))
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

            return (false, $"Python executable not found on system PATH. Error: {lastException?.Message}");
        }

        private static string FindCargoProjectDir(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return "";
            try
            {
                string? dir = Path.GetDirectoryName(filePath);
                while (!string.IsNullOrEmpty(dir))
                {
                    if (File.Exists(Path.Combine(dir, "Cargo.toml")))
                    {
                        return dir;
                    }
                    dir = Path.GetDirectoryName(dir);
                }
            }
            catch { }
            return "";
        }

        private static async Task<(bool Success, string Output)> RunCargoBuildAsync(string filePath)
        {
            string workingDir = FindCargoProjectDir(filePath);
            if (string.IsNullOrEmpty(workingDir))
            {
                if (!string.IsNullOrEmpty(filePath))
                {
                    workingDir = Path.GetDirectoryName(filePath) ?? "";
                }
            }

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cargo",
                    Arguments = "build",
                    WorkingDirectory = workingDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var proc = System.Diagnostics.Process.Start(psi))
                {
                    if (proc != null)
                    {
                        string stdout = await proc.StandardOutput.ReadToEndAsync();
                        string stderr = await proc.StandardError.ReadToEndAsync();

                        await proc.WaitForExitAsync();

                        string combined = string.IsNullOrEmpty(stderr) ? stdout : $"{stdout}\nStderr:\n{stderr}";
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
                return (false, $"Cargo execution failed. Make sure Rust/Cargo is installed. Error: {ex.Message}");
            }

            return (false, "Failed to start Cargo process.");
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

        private static async Task UpdateStatusBarAsync(NetworkStream stream, string itemId, string text, string tooltip = "", string commandId = "")
        {
            byte[] temp = new byte[BinaryMessageSerializer.HeaderSize + 16 + (itemId.Length + text.Length + tooltip.Length + commandId.Length) * sizeof(char)];
            int len = BinaryMessageSerializer.WriteUpdateExtensionStatusBarItem(temp, itemId, text, tooltip, commandId);
            await SendMessageAsync(stream, temp, len);
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
