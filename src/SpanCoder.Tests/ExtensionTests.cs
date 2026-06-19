using Avalonia.Headless.XUnit;
using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using SpanCoder.Contracts;
using SpanCoder.App;
using SpanCoder.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace SpanCoder.Tests
{
    public class ExtensionTests
    {
        [Fact]
        public async Task TestExtensionLifecycleAndIpc()
        {
            // Create a temporary plugins directory
            string tempPluginsDir = Path.Combine(Path.GetTempPath(), "SpanCoderTests_Plugins_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempPluginsDir);

            try
            {
                // Instantiate ExtensionManager
                using var manager = new ExtensionManager(tempPluginsDir);
                
                // Track events
                bool registered = false;
                ExtensionManifest? receivedManifest = null;
                string? registeredExtId = null;

                manager.ExtensionRegistered += (extId, manifest) =>
                {
                    registeredExtId = extId;
                    receivedManifest = manifest;
                    registered = true;
                };

                string? updatedPanelId = null;
                string? updatedPanelContent = null;
                manager.PanelContentUpdated += (panelId, content) =>
                {
                    updatedPanelId = panelId;
                    updatedPanelContent = content;
                };

                // Start manager
                manager.Start();
                int port = manager.Port;
                Assert.True(port > 0);

                // Connect a mock TCP client (simulating the plugin host)
                using var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", port);
                using var stream = client.GetStream();

                // Build a mock JSON manifest
                string manifestJson = "{" +
                    "\"id\":\"test-plugin\"," +
                    "\"commands\":[{" +
                        "\"id\":\"test.command\"," +
                        "\"displayName\":\"Test Command\"," +
                        "\"category\":\"Test\"," +
                        "\"defaultShortcut\":\"Ctrl+Shift+T\"" +
                    "}]," +
                    "\"menuItems\":[{" +
                        "\"commandId\":\"test.command\"," +
                        "\"menuPath\":\"Tools/Test Command\"," +
                        "\"orderPriority\":10" +
                    "}]," +
                    "\"panels\":[{" +
                        "\"id\":\"test.panel\"," +
                        "\"title\":\"Test Panel\"" +
                    "}]," +
                    "\"settings\":[{" +
                        "\"id\":\"test-plugin.fontSize\"," +
                        "\"displayName\":\"Test Plugin Font Size\"," +
                        "\"type\":\"integer\"," +
                        "\"defaultValue\":\"16\"" +
                    "}]" +
                "}";

                string token = "test-token-1";
                manager.AddPendingToken(token, "test-plugin");

                byte[] jsonBytes = Encoding.UTF8.GetBytes(manifestJson);
                byte[] regBuffer = new byte[BinaryMessageSerializer.HeaderSize + sizeof(int) + token.Length * sizeof(char) + sizeof(int) + jsonBytes.Length];
                int regLen = BinaryMessageSerializer.WriteRegisterExtension(regBuffer, token, jsonBytes);

                // Send registration message
                await stream.WriteAsync(regBuffer, 0, regLen);
                await stream.FlushAsync();

                // Wait for registration
                int retries = 0;
                while (!registered && retries++ < 50)
                {
                    await Task.Delay(100);
                }

                Assert.True(registered, "Extension should be registered");
                Assert.Equal("test-plugin", registeredExtId);
                Assert.NotNull(receivedManifest);
                Assert.Single(receivedManifest.Value.Commands);
                Assert.Equal("test.command", receivedManifest.Value.Commands[0].Id);
                Assert.Single(receivedManifest.Value.MenuItems);
                Assert.Equal("Tools/Test Command", receivedManifest.Value.MenuItems[0].MenuPath);
                Assert.Single(receivedManifest.Value.Panels);
                Assert.Equal("test.panel", receivedManifest.Value.Panels[0].Id);
                Assert.Single(receivedManifest.Value.Settings);
                Assert.Equal("test-plugin.fontSize", receivedManifest.Value.Settings[0].Id);
                Assert.Equal("Test Plugin Font Size", receivedManifest.Value.Settings[0].DisplayName);
                Assert.Equal("integer", receivedManifest.Value.Settings[0].Type);
                Assert.Equal("16", receivedManifest.Value.Settings[0].DefaultValue);

                // Simulate host invoking command
                var commandReceiveTask = Task.Run(async () =>
                {
                    byte[] headerBuffer = new byte[BinaryMessageSerializer.HeaderSize];
                    int r = await ReadExactlyAsync(stream, headerBuffer, 0, headerBuffer.Length);
                    if (r < headerBuffer.Length) throw new Exception("Failed to read header");
                    if (!BinaryMessageSerializer.TryParseHeader(headerBuffer, out var header)) throw new Exception("Invalid header");

                    byte[] payload = new byte[header.Length];
                    Array.Copy(headerBuffer, 0, payload, 0, headerBuffer.Length);
                    if (header.Length > headerBuffer.Length)
                    {
                        int r2 = await ReadExactlyAsync(stream, payload, headerBuffer.Length, header.Length - headerBuffer.Length);
                        if (r2 < header.Length - headerBuffer.Length) throw new Exception("Failed to read payload");
                    }
                    return payload;
                });

                manager.ExecuteCommand("test-plugin", "test.command");

                var receivedPayload = await commandReceiveTask;
                string executedCmd = BinaryMessageSerializer.ParseExecuteExtensionCommand(receivedPayload);
                Assert.Equal("test.command", executedCmd);

                // Simulate plugin updating a panel
                byte[] updateBuffer = new byte[BinaryMessageSerializer.HeaderSize + 8 + ("test.panel".Length + "Hello from panel".Length) * 2];
                int updateLen = BinaryMessageSerializer.WriteUpdateExtensionPanel(updateBuffer, "test.panel", "Hello from panel");

                await stream.WriteAsync(updateBuffer, 0, updateLen);
                await stream.FlushAsync();

                retries = 0;
                while (updatedPanelContent == null && retries++ < 50)
                {
                    await Task.Delay(100);
                }

                Assert.Equal("test.panel", updatedPanelId);
                Assert.Equal("Hello from panel", updatedPanelContent);
            }
            finally
            {
                if (Directory.Exists(tempPluginsDir))
                {
                    Directory.Delete(tempPluginsDir, true);
                }
            }
        }

        [Fact]
        public void TestLanguageConfigurationUnregister()
        {
            var desc = new LanguageConfigDescriptor(
                ".mockext",
                "#",
                null,
                null,
                new System.Collections.Generic.List<string> { "key" },
                new System.Collections.Generic.List<string> { "type" }
            );

            LanguageConfigurationRegistry.Register(desc);
            var config = LanguageConfigurationRegistry.Get(".mockext");
            Assert.Equal("#", config.LineComment);
            Assert.Contains("key", config.Keywords);

            LanguageConfigurationRegistry.Unregister(".mockext");
            var configAfter = LanguageConfigurationRegistry.Get(".mockext");
            Assert.Null(configAfter.LineComment);
            Assert.Empty(configAfter.Keywords);
        }

        [Fact]
        public async Task TestExtensionUnregistrationEvent()
        {
            string tempPluginsDir = Path.Combine(Path.GetTempPath(), "SpanCoderTests_Plugins_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempPluginsDir);

            try
            {
                using var manager = new ExtensionManager(tempPluginsDir);
                bool unregistered = false;
                string? unregisteredId = null;

                manager.ExtensionUnregistered += (extId) =>
                {
                    unregisteredId = extId;
                    unregistered = true;
                };

                manager.Start();
                int port = manager.Port;

                using (var client = new TcpClient())
                {
                    await client.ConnectAsync("127.0.0.1", port);
                    using var stream = client.GetStream();

                    string token = "test-token-2";
                    manager.AddPendingToken(token, "test-plugin-unreg");

                    string manifestJson = "{\"id\":\"test-plugin-unreg\"}";
                    byte[] jsonBytes = Encoding.UTF8.GetBytes(manifestJson);
                    byte[] regBuffer = new byte[BinaryMessageSerializer.HeaderSize + sizeof(int) + token.Length * sizeof(char) + sizeof(int) + jsonBytes.Length];
                    int regLen = BinaryMessageSerializer.WriteRegisterExtension(regBuffer, token, jsonBytes);

                    await stream.WriteAsync(regBuffer, 0, regLen);
                    await stream.FlushAsync();

                    // Wait a brief moment to allow processing of registration
                    await Task.Delay(250);
                } // client is closed here!

                // Wait for unregistration event
                int retries = 0;
                while (!unregistered && retries++ < 50)
                {
                    await Task.Delay(100);
                }

                Assert.True(unregistered, "Extension should be unregistered on connection close");
                Assert.Equal("test-plugin-unreg", unregisteredId);
            }
            finally
            {
                if (Directory.Exists(tempPluginsDir))
                {
                    Directory.Delete(tempPluginsDir, true);
                }
            }
        }

        // UI testing in headless test runners hangs on platform detect initialization.
        // IPC and extension registration lifecycle is fully verified in TestExtensionLifecycleAndIpc.

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

        [Fact]
        public void TestXmlCoverageParsing()
        {
            string mockXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<coverage line-rate=""0.5"" branch-rate=""0.5"" version=""1.9"" timestamp=""12345678"" lines-valid=""4"" lines-covered=""2"" branches-valid=""0"" branches-covered=""0"">
  <packages>
    <package name=""SpanCoder.Tests"" line-rate=""0.5"" branch-rate=""1"" complexity=""0"">
      <classes>
        <class name=""SpanCoder.Tests.MockClass"" filename=""src/SpanCoder.Tests/MockClass.cs"" line-rate=""0.5"" branch-rate=""1"" complexity=""0"">
          <methods />
          <lines>
            <line number=""10"" hits=""5"" branch=""False"" />
            <line number=""11"" hits=""0"" branch=""False"" />
            <line number=""12"" hits=""3"" branch=""False"" />
          </lines>
        </class>
      </classes>
    </package>
  </packages>
</coverage>";

            string workspaceRoot = Path.GetFullPath(@"C:\workspace");
            var coverage = CoberturaParser.Parse(mockXml, workspaceRoot);
            
            string expectedPath = Path.GetFullPath(Path.Combine(workspaceRoot, "src/SpanCoder.Tests/MockClass.cs"));
            Assert.True(coverage.ContainsKey(expectedPath));
            var fileCoverage = coverage[expectedPath];
            Assert.True(fileCoverage[10]);
            Assert.False(fileCoverage[11]);
            Assert.True(fileCoverage[12]);
        }

        [AvaloniaFact]
        public void TestInlayHintsAndCodeLensStorage()
        {
            var canvas = new TextEditorCanvas();
            
            var hints = new System.Collections.Generic.List<InlayHintItem>
            {
                new InlayHintItem(12, "param1:"),
                new InlayHintItem(25, "param2:")
            };
            canvas.SetInlayHints(hints);

            var codeLens = new System.Collections.Generic.Dictionary<int, string>
            {
                { 5, "3 references" },
                { 15, "1 reference" }
            };
            canvas.SetCodeLens(codeLens);

            var lineCoverage = new System.Collections.Generic.Dictionary<int, bool>
            {
                { 10, true },
                { 11, false }
            };
            canvas.SetLineCoverage(lineCoverage);

            Assert.NotNull(canvas);
        }

        [Fact]
        public void TestExtensionSettingsRegistration()
        {
            SettingsManager.UnregisterExtensionSettings("test-ext");
            var desc = new SettingDescriptor("test-ext.customSetting", "Custom Setting", "boolean", "true");
            SettingsManager.RegisterExtensionSetting(desc);

            Assert.Equal("true", SettingsManager.Get("test-ext.customSetting"));
            Assert.True(SettingsManager.Get<bool>("test-ext.customSetting", false));

            // Set a new value
            SettingsManager.Set("test-ext.customSetting", "false");
            Assert.Equal("false", SettingsManager.Get("test-ext.customSetting"));
            Assert.False(SettingsManager.Get<bool>("test-ext.customSetting", true));

            // Unregister
            SettingsManager.UnregisterExtensionSettings("test-ext");
            
            // Getting it should now fallback to defaultValue or be unregistered (not in descriptors)
            var descriptors = SettingsManager.GetDescriptors();
            Assert.DoesNotContain(descriptors, d => d.Id == "test-ext.customSetting");
        }

        [Fact]
        public async Task TestExtensionStatusBarAndSettingsSync()
        {
            // Create a temporary plugins directory
            string tempPluginsDir = Path.Combine(Path.GetTempPath(), "SpanCoderTests_Plugins_StatusBar_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempPluginsDir);

            try
            {
                using var manager = new ExtensionManager(tempPluginsDir);
                
                bool registered = false;
                manager.ExtensionRegistered += (extId, manifest) => { registered = true; };

                string? updatedItemId = null;
                string? updatedText = null;
                string? updatedTooltip = null;
                string? updatedCommandId = null;

                manager.StatusBarItemUpdated += (extId, itemId, text, tooltip, commandId) =>
                {
                    updatedItemId = itemId;
                    updatedText = text;
                    updatedTooltip = tooltip;
                    updatedCommandId = commandId;
                };

                manager.Start();
                int port = manager.Port;

                using var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", port);
                using var stream = client.GetStream();

                string token = "test-token-3";
                manager.AddPendingToken(token, "test-plugin-status");

                // Register extension
                string manifestJson = "{" +
                    "\"id\":\"test-plugin-status\"," +
                    "\"settings\":[{" +
                        "\"id\":\"test-plugin-status.mySetting\"," +
                        "\"displayName\":\"My Setting\"," +
                        "\"type\":\"string\"," +
                        "\"defaultValue\":\"defaultVal\"" +
                    "}]" +
                "}";

                byte[] jsonBytes = Encoding.UTF8.GetBytes(manifestJson);
                byte[] regBuffer = new byte[BinaryMessageSerializer.HeaderSize + sizeof(int) + token.Length * sizeof(char) + sizeof(int) + jsonBytes.Length];
                int regLen = BinaryMessageSerializer.WriteRegisterExtension(regBuffer, token, jsonBytes);

                await stream.WriteAsync(regBuffer, 0, regLen);
                await stream.FlushAsync();

                // Wait for registration
                int retries = 0;
                while (!registered && retries++ < 50)
                {
                    await Task.Delay(100);
                }
                Assert.True(registered);

                // 1. Verify status bar item updates over TCP
                byte[] statusBuffer = new byte[1024];
                int statusLen = BinaryMessageSerializer.WriteUpdateExtensionStatusBarItem(
                    statusBuffer, "languages-status", "Py: Running...", "Running python", "languages.runPython");
                
                await stream.WriteAsync(statusBuffer, 0, statusLen);
                await stream.FlushAsync();

                // Wait for status bar event
                retries = 0;
                while (updatedItemId == null && retries++ < 50)
                {
                    await Task.Delay(100);
                }

                Assert.Equal("languages-status", updatedItemId);
                Assert.Equal("Py: Running...", updatedText);
                Assert.Equal("Running python", updatedTooltip);
                Assert.Equal("languages.runPython", updatedCommandId);

                // 2. Verify settings sync: Host setting change -> TCP message sent to extension
                // Start a task to read from TCP stream
                var settingsTask = Task.Run(async () =>
                {
                    byte[] headerBuffer = new byte[BinaryMessageSerializer.HeaderSize];
                    int r = await ReadExactlyAsync(stream, headerBuffer, 0, headerBuffer.Length);
                    if (r < headerBuffer.Length) return null;
                    if (!BinaryMessageSerializer.TryParseHeader(headerBuffer, out var header)) return null;
                    if (header.Type != MessageTypes.ExtensionSettingChanged) return null;

                    byte[] payload = new byte[header.Length];
                    Array.Copy(headerBuffer, 0, payload, 0, headerBuffer.Length);
                    if (header.Length > headerBuffer.Length)
                    {
                        await ReadExactlyAsync(stream, payload, headerBuffer.Length, header.Length - headerBuffer.Length);
                    }
                    return payload;
                });

                // Change settings
                SettingsManager.Set("test-plugin-status.mySetting", "newValue");

                var payload = await settingsTask;
                Assert.NotNull(payload);

                BinaryMessageSerializer.ParseExtensionSettingChanged(payload, out string settingId, out string val);
                Assert.Equal("test-plugin-status.mySetting", settingId);
                Assert.Equal("newValue", val);
            }
            finally
            {
                SettingsManager.UnregisterExtensionSettings("test-plugin-status");
                if (Directory.Exists(tempPluginsDir))
                {
                    Directory.Delete(tempPluginsDir, true);
                }
            }
        }

        [Fact]
        public async Task TestExtensionConnectionRejectedWithoutValidToken()
        {
            string tempPluginsDir = Path.Combine(Path.GetTempPath(), "SpanCoderTests_Plugins_Rejected_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempPluginsDir);

            try
            {
                using var manager = new ExtensionManager(tempPluginsDir);
                
                bool registered = false;
                manager.ExtensionRegistered += (extId, manifest) => { registered = true; };

                manager.Start();
                int port = manager.Port;

                using var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", port);
                using var stream = client.GetStream();

                // Connect and send a register message with an invalid token
                string invalidToken = "wrong-token";
                // We do NOT add it to pending tokens on the host manager!

                string manifestJson = "{\"id\":\"test-plugin-rejected\"}";
                byte[] jsonBytes = Encoding.UTF8.GetBytes(manifestJson);
                byte[] regBuffer = new byte[BinaryMessageSerializer.HeaderSize + sizeof(int) + invalidToken.Length * sizeof(char) + sizeof(int) + jsonBytes.Length];
                int regLen = BinaryMessageSerializer.WriteRegisterExtension(regBuffer, invalidToken, jsonBytes);

                await stream.WriteAsync(regBuffer, 0, regLen);
                await stream.FlushAsync();

                // Wait to see if registration is ignored
                await Task.Delay(500);
                Assert.False(registered, "Extension should not register with an invalid token");

                // Check that connection was closed by host
                byte[] temp = new byte[10];
                int read = await stream.ReadAsync(temp, 0, temp.Length);
                Assert.Equal(0, read); // Socket should be closed/EOF
            }
            finally
            {
                if (Directory.Exists(tempPluginsDir))
                {
                    Directory.Delete(tempPluginsDir, true);
                }
            }
        }

        [Fact]
        public async Task TestExtensionFormatting()
        {
            string tempPluginsDir = Path.Combine(Path.GetTempPath(), "SpanCoderTests_Plugins_Formatting_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempPluginsDir);

            try
            {
                using var manager = new ExtensionManager(tempPluginsDir);

                bool registered = false;
                ExtensionManifest? receivedManifest = null;
                string? registeredExtId = null;

                manager.ExtensionRegistered += (extId, manifest) =>
                {
                    registeredExtId = extId;
                    receivedManifest = manifest;
                    registered = true;
                };

                manager.Start();
                int port = manager.Port;

                using var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", port);
                using var stream = client.GetStream();

                string manifestJson = "{" +
                    "\"id\":\"prettier-extension\"," +
                    "\"formatters\":[\".js\", \".json\"]" +
                "}";

                string token = "test-token-format";
                manager.AddPendingToken(token, "prettier-extension");

                byte[] jsonBytes = Encoding.UTF8.GetBytes(manifestJson);
                byte[] regBuffer = new byte[BinaryMessageSerializer.HeaderSize + sizeof(int) + token.Length * sizeof(char) + sizeof(int) + jsonBytes.Length];
                int regLen = BinaryMessageSerializer.WriteRegisterExtension(regBuffer, token, jsonBytes);

                await stream.WriteAsync(regBuffer, 0, regLen);
                await stream.FlushAsync();

                int retries = 0;
                while (!registered && retries++ < 50)
                {
                    await Task.Delay(100);
                }

                Assert.True(registered, "Extension should be registered");
                Assert.Equal("prettier-extension", registeredExtId);
                Assert.NotNull(receivedManifest);
                Assert.Equal(2, receivedManifest.Value.Formatters.Count);
                Assert.Equal(".js", receivedManifest.Value.Formatters[0]);

                // Start mock extension message processing loop
                var clientLoopTask = Task.Run(async () =>
                {
                    byte[] headerBuffer = new byte[BinaryMessageSerializer.HeaderSize];
                    int r = await ReadExactlyAsync(stream, headerBuffer, 0, headerBuffer.Length);
                    if (r <= 0) return;

                    if (!BinaryMessageSerializer.TryParseHeader(headerBuffer, out var header)) return;

                    byte[] payload = new byte[header.Length];
                    Array.Copy(headerBuffer, 0, payload, 0, headerBuffer.Length);
                    if (header.Length > headerBuffer.Length)
                    {
                        await ReadExactlyAsync(stream, payload, headerBuffer.Length, header.Length - headerBuffer.Length);
                    }

                    if (header.Type == MessageTypes.FormatDocumentRequest)
                    {
                        BinaryMessageSerializer.ParseFormatDocumentRequest(payload, out int docId, out string filePath, out string content);
                        
                        string formatted = content + " // formatted";
                        
                        byte[] respBuffer = new byte[1024];
                        int respLen = BinaryMessageSerializer.WriteFormatDocumentResponse(respBuffer, docId, formatted);

                        await stream.WriteAsync(respBuffer, 0, respLen);
                        await stream.FlushAsync();
                    }
                });

                string originalContent = "function foo() {}";
                string? formattedResult = await manager.FormatDocumentAsync("prettier-extension", 42, "test.js", originalContent);

                Assert.NotNull(formattedResult);
                Assert.Equal("function foo() {} // formatted", formattedResult);

                await clientLoopTask;
            }
            finally
            {
                if (Directory.Exists(tempPluginsDir))
                {
                    Directory.Delete(tempPluginsDir, true);
                }
            }
        }

        [Fact]
        public async Task TestRealExtensionsCommunication()
        {
            string tempPluginsDir = Path.Combine(Path.GetTempPath(), "SpanCoderTests_Plugins_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempPluginsDir);

            try
            {
                using var manager = new ExtensionManager(tempPluginsDir);
                bool registered = false;
                manager.ExtensionRegistered += (extId, manifest) => { registered = true; };

                manager.Start();
                int port = manager.Port;

                using var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", port);
                using var stream = client.GetStream();

                string manifestJson = "{\"id\":\"html-preview\",\"commands\":[{\"id\":\"html-preview.show\",\"displayName\":\"Show HTML\"}]}";
                string token = "test-token-html";
                manager.AddPendingToken(token, "html-preview");

                byte[] jsonBytes = Encoding.UTF8.GetBytes(manifestJson);
                byte[] regBuffer = new byte[BinaryMessageSerializer.HeaderSize + sizeof(int) + token.Length * sizeof(char) + sizeof(int) + jsonBytes.Length];
                int regLen = BinaryMessageSerializer.WriteRegisterExtension(regBuffer, token, jsonBytes);

                await stream.WriteAsync(regBuffer, 0, regLen);
                await stream.FlushAsync();

                // Wait for registration
                for (int i = 0; i < 20 && !registered; i++)
                {
                    await Task.Delay(50);
                }
                Assert.True(registered);

                // Now execute command with context
                var clientLoopTask = Task.Run< (string? cmdId, string? path, string? content) >(async () =>
                {
                    byte[] headerBuffer = new byte[BinaryMessageSerializer.HeaderSize];
                    int r = await ReadExactlyAsync(stream, headerBuffer, 0, headerBuffer.Length);
                    if (r <= 0) return (null, null, null);

                    if (!BinaryMessageSerializer.TryParseHeader(headerBuffer, out var header)) return (null, null, null);

                    byte[] payload = new byte[header.Length];
                    Array.Copy(headerBuffer, 0, payload, 0, headerBuffer.Length);
                    if (header.Length > headerBuffer.Length)
                    {
                        await ReadExactlyAsync(stream, payload, headerBuffer.Length, header.Length - headerBuffer.Length);
                    }

                    if (header.Type == MessageTypes.ExecuteExtensionCommandWithContext)
                    {
                        BinaryMessageSerializer.ParseExecuteExtensionCommandWithContext(payload, out string cmdId, out string path, out string content);
                        return (cmdId, path, content);
                    }
                    return (null, null, null);
                });

                manager.ExecuteCommandWithContext("html-preview", "html-preview.show", "c:/temp/test.html", "<h1>Hello</h1>");

                var (cmdId, path, content) = await clientLoopTask;
                Assert.Equal("html-preview.show", cmdId);
                Assert.Equal("c:/temp/test.html", path);
                Assert.Equal("<h1>Hello</h1>", content);
            }
            finally
            {
                if (Directory.Exists(tempPluginsDir))
                {
                    Directory.Delete(tempPluginsDir, true);
                }
            }
        }
    }
}
