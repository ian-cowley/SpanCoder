using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SpanCoder.Contracts;

namespace SpanCoder.App
{
    public class ExtensionManager : IExtensionManager, IDisposable
    {
        private TcpListener? _listener;
        private readonly ConcurrentDictionary<string, (TcpClient Client, NetworkStream Stream, object WriteLock)> _activeExtensions = new();
        private readonly List<Process> _extensionProcesses = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly string _pluginsDirectory;

        public int Port { get; private set; }

        public event Action<string, ExtensionManifest>? ExtensionRegistered;
        public event Action<string, string>? PanelContentUpdated;
        public event Action<string>? ExtensionUnregistered;

        public ExtensionManager(string pluginsDirectory)
        {
            _pluginsDirectory = pluginsDirectory;
        }

        public void Start()
        {
            // 1. Start TCP Listener on a dynamic port
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Console.WriteLine($"[ExtensionManager] Listening on port {Port}");

            // 2. Start Accept Loop
            Task.Run(() => AcceptLoop(Port));

            // 3. Scan and Launch plugins
            ScanAndLaunchPlugins(Port);
        }

        private void ScanAndLaunchPlugins(int port)
        {
            if (!Directory.Exists(_pluginsDirectory))
            {
                Directory.CreateDirectory(_pluginsDirectory);
            }

            foreach (var dir in Directory.GetDirectories(_pluginsDirectory))
            {
                string manifestPath = Path.Combine(dir, "plugin.json");
                if (File.Exists(manifestPath))
                {
                    try
                    {
                        string json = File.ReadAllText(manifestPath);
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("entryPoint", out var entryPointEl))
                        {
                            string entryPoint = entryPointEl.GetString() ?? "";
                            string fullPath = Path.Combine(dir, entryPoint);
                            if (File.Exists(fullPath))
                            {
                                LaunchPlugin(fullPath, port);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ExtensionManager] Failed to launch plugin in {dir}: {ex.Message}");
                    }
                }
            }
        }

        private void LaunchPlugin(string path, int port)
        {
            string executable = path;
            string arguments = $"--port {port}";

            if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                executable = "dotnet";
                arguments = $"\"{path}\" --port {port}";
            }

            Console.WriteLine($"[ExtensionManager] Launching plugin process: {executable} {arguments}");
            var psi = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var proc = Process.Start(psi);
            if (proc != null)
            {
                lock (_extensionProcesses)
                {
                    _extensionProcesses.Add(proc);
                }
            }
        }

        public void InstallAndLaunchPlugin(string pluginDir)
        {
            string manifestPath = Path.Combine(pluginDir, "plugin.json");
            if (File.Exists(manifestPath))
            {
                try
                {
                    string json = File.ReadAllText(manifestPath);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("entryPoint", out var entryPointEl))
                    {
                        string entryPoint = entryPointEl.GetString() ?? "";
                        string fullPath = Path.Combine(pluginDir, entryPoint);
                        if (File.Exists(fullPath))
                        {
                            LaunchPlugin(fullPath, Port);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ExtensionManager] Failed to launch dynamic plugin: {ex.Message}");
                }
            }
        }

        public void UninstallPlugin(string extensionId)
        {
            if (_activeExtensions.TryRemove(extensionId, out var ext))
            {
                try
                {
                    ext.Client.Close();
                }
                catch {}
                
                ExtensionUnregistered?.Invoke(extensionId);
            }

            lock (_extensionProcesses)
            {
                for (int i = _extensionProcesses.Count - 1; i >= 0; i--)
                {
                    var proc = _extensionProcesses[i];
                    try
                    {
                        if (proc.HasExited)
                        {
                            _extensionProcesses.RemoveAt(i);
                            continue;
                        }

                        string args = proc.StartInfo.Arguments;
                        string file = proc.StartInfo.FileName;
                        
                        if (args.Contains(extensionId) || file.Contains(extensionId))
                        {
                            proc.Kill();
                            _extensionProcesses.RemoveAt(i);
                        }
                    }
                    catch {}
                }
            }
        }

        private async Task AcceptLoop(int port)
        {
            try
            {
                while (!_cts.Token.IsCancellationRequested && _listener != null)
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    _ = Task.Run(() => HandleClientAsync(client));
                }
            }
            catch (Exception ex)
            {
                if (!_cts.Token.IsCancellationRequested)
                {
                    Console.WriteLine($"[ExtensionManager] Accept error: {ex.Message}");
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            var stream = client.GetStream();
            string? registeredExtensionId = null;

            try
            {
                byte[] headerBuffer = new byte[BinaryMessageSerializer.HeaderSize];
                while (!_cts.Token.IsCancellationRequested && client.Connected)
                {
                    // Read header
                    int readBytes = await ReadExactlyAsync(stream, headerBuffer, 0, headerBuffer.Length, _cts.Token);
                    if (readBytes <= 0) break;

                    if (!BinaryMessageSerializer.TryParseHeader(headerBuffer, out var header))
                    {
                        throw new InvalidDataException("Invalid binary header from extension");
                    }

                    byte[] payload = new byte[header.Length];
                    Array.Copy(headerBuffer, 0, payload, 0, headerBuffer.Length);
                    if (header.Length > headerBuffer.Length)
                    {
                        await ReadExactlyAsync(stream, payload, headerBuffer.Length, header.Length - headerBuffer.Length, _cts.Token);
                    }

                    if (header.Type == MessageTypes.RegisterExtension)
                    {
                        var jsonSpan = BinaryMessageSerializer.ParseRegisterExtension(payload);
                        string json = Encoding.UTF8.GetString(jsonSpan);
                        var manifest = ParseManifest(json);

                        registeredExtensionId = manifest.Id;
                        _activeExtensions[manifest.Id] = (client, stream, new object());

                        Console.WriteLine($"[ExtensionManager] Extension '{manifest.Id}' registered successfully.");
                        ExtensionRegistered?.Invoke(manifest.Id, manifest);
                    }
                    else if (header.Type == MessageTypes.UpdateExtensionPanel)
                    {
                        BinaryMessageSerializer.ParseUpdateExtensionPanel(payload, out string panelId, out string content);
                        PanelContentUpdated?.Invoke(panelId, content);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("[ExtensionManager] Extension connection closed (canceled).");
            }
            catch (Exception ex)
            {
                if (_cts.Token.IsCancellationRequested)
                {
                    Console.WriteLine("[ExtensionManager] Extension connection closed during shutdown.");
                }
                else
                {
                    Console.WriteLine($"[ExtensionManager] Extension communication error: {ex.Message}");
                }
            }
            finally
            {
                if (registeredExtensionId != null)
                {
                    _activeExtensions.TryRemove(registeredExtensionId, out _);
                    ExtensionUnregistered?.Invoke(registeredExtensionId);
                }
                client.Close();
            }
        }

        private async Task<int> ReadExactlyAsync(NetworkStream stream, byte[] buffer, int offset, int count, CancellationToken token)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(offset + totalRead, count - totalRead), token);
                if (read <= 0) return totalRead;
                totalRead += read;
            }
            return totalRead;
        }

        public void ExecuteCommand(string extensionId, string commandId)
        {
            if (_activeExtensions.TryGetValue(extensionId, out var ext))
            {
                byte[] temp = new byte[BinaryMessageSerializer.HeaderSize + 4 + commandId.Length * sizeof(char)];
                int len = BinaryMessageSerializer.WriteExecuteExtensionCommand(temp, commandId);
                byte[] payload = new byte[len];
                Array.Copy(temp, 0, payload, 0, len);

                lock (ext.WriteLock)
                {
                    try
                    {
                        ext.Stream.Write(payload, 0, len);
                        ext.Stream.Flush();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ExtensionManager] Failed to dispatch command '{commandId}' to extension '{extensionId}': {ex.Message}");
                    }
                }
            }
            else
            {
                Console.WriteLine($"[ExtensionManager] Cannot execute command '{commandId}': Extension '{extensionId}' is not connected.");
            }
        }

        private ExtensionManifest ParseManifest(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string id = root.GetProperty("id").GetString() ?? "";
            
            var commands = new List<CommandDescriptor>();
            if (root.TryGetProperty("commands", out var cmdEl))
            {
                foreach (var el in cmdEl.EnumerateArray())
                {
                    string cmdId = el.GetProperty("id").GetString() ?? "";
                    string displayName = el.GetProperty("displayName").GetString() ?? "";
                    string category = el.TryGetProperty("category", out var catEl) ? (catEl.GetString() ?? "") : "";
                    string defaultShortcut = el.TryGetProperty("defaultShortcut", out var scEl) ? (scEl.GetString() ?? "") : "";
                    commands.Add(new CommandDescriptor(cmdId, displayName, category, defaultShortcut));
                }
            }

            var menuItems = new List<MenuItemDescriptor>();
            if (root.TryGetProperty("menuItems", out var menuEl))
            {
                foreach (var el in menuEl.EnumerateArray())
                {
                    string commandId = el.GetProperty("commandId").GetString() ?? "";
                    string menuPath = el.GetProperty("menuPath").GetString() ?? "";
                    int order = el.GetProperty("orderPriority").GetInt32();
                    menuItems.Add(new MenuItemDescriptor(commandId, menuPath, order));
                }
            }

            var panels = new List<PanelDescriptor>();
            if (root.TryGetProperty("panels", out var panelsEl))
            {
                foreach (var el in panelsEl.EnumerateArray())
                {
                    string panelId = el.GetProperty("id").GetString() ?? "";
                    string title = el.GetProperty("title").GetString() ?? "";
                    panels.Add(new PanelDescriptor(panelId, title));
                }
            }

            var languages = new List<LanguageConfigDescriptor>();
            if (root.TryGetProperty("languages", out var langEl))
            {
                foreach (var el in langEl.EnumerateArray())
                {
                    string ext = el.GetProperty("extension").GetString() ?? "";
                    string? lineComment = el.TryGetProperty("lineComment", out var lc) ? lc.GetString() : null;
                    string? blockStart = el.TryGetProperty("blockCommentStart", out var bs) ? bs.GetString() : null;
                    string? blockEnd = el.TryGetProperty("blockCommentEnd", out var be) ? be.GetString() : null;
                    
                    var keywords = new List<string>();
                    if (el.TryGetProperty("keywords", out var kwEl))
                    {
                        foreach (var kw in kwEl.EnumerateArray())
                        {
                            keywords.Add(kw.GetString() ?? "");
                        }
                    }

                    var types = new List<string>();
                    if (el.TryGetProperty("types", out var tyEl))
                    {
                        foreach (var ty in tyEl.EnumerateArray())
                        {
                            types.Add(ty.GetString() ?? "");
                        }
                    }

                    languages.Add(new LanguageConfigDescriptor(ext, lineComment, blockStart, blockEnd, keywords, types));
                }
            }

            var toolbarItems = new List<ToolbarItemDescriptor>();
            if (root.TryGetProperty("toolbarItems", out var tbEl))
            {
                foreach (var el in tbEl.EnumerateArray())
                {
                    string commandId = el.GetProperty("commandId").GetString() ?? "";
                    string displayName = el.GetProperty("displayName").GetString() ?? "";
                    string? iconPath = el.TryGetProperty("iconPath", out var ip) ? ip.GetString() : null;
                    int order = el.TryGetProperty("orderPriority", out var op) ? op.GetInt32() : 100;
                    toolbarItems.Add(new ToolbarItemDescriptor(commandId, displayName, iconPath, order));
                }
            }

            var settings = new List<SettingDescriptor>();
            if (root.TryGetProperty("settings", out var settingsEl))
            {
                foreach (var el in settingsEl.EnumerateArray())
                {
                    string settingId = el.GetProperty("id").GetString() ?? "";
                    string displayName = el.GetProperty("displayName").GetString() ?? "";
                    string type = el.TryGetProperty("type", out var typeProp) ? (typeProp.GetString() ?? "string") : "string";
                    string defaultValue = el.TryGetProperty("defaultValue", out var defProp) ? (defProp.GetString() ?? "") : "";
                    settings.Add(new SettingDescriptor(settingId, displayName, type, defaultValue));
                }
            }

            return new ExtensionManifest(id, commands, menuItems, panels, languages, toolbarItems, settings);
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener?.Stop();

            lock (_extensionProcesses)
            {
                foreach (var proc in _extensionProcesses)
                {
                    try
                    {
                        if (!proc.HasExited)
                        {
                            proc.Kill();
                        }
                    }
                    catch { }
                    proc.Dispose();
                }
                _extensionProcesses.Clear();
            }

            foreach (var ext in _activeExtensions.Values)
            {
                try
                {
                    ext.Client.Close();
                }
                catch { }
            }
            _activeExtensions.Clear();
        }
    }
}
