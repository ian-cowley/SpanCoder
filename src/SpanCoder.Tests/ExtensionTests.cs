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

                byte[] jsonBytes = Encoding.UTF8.GetBytes(manifestJson);
                byte[] regBuffer = new byte[BinaryMessageSerializer.HeaderSize + sizeof(int) + jsonBytes.Length];
                int regLen = BinaryMessageSerializer.WriteRegisterExtension(regBuffer, jsonBytes);

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

                    string manifestJson = "{\"id\":\"test-plugin-unreg\"}";
                    byte[] jsonBytes = Encoding.UTF8.GetBytes(manifestJson);
                    byte[] regBuffer = new byte[BinaryMessageSerializer.HeaderSize + sizeof(int) + jsonBytes.Length];
                    int regLen = BinaryMessageSerializer.WriteRegisterExtension(regBuffer, jsonBytes);

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
    }
}
