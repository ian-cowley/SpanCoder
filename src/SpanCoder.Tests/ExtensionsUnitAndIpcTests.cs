using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SpanCoder.Contracts;
using Xunit;

namespace SpanCoder.Tests
{
    public class ExtensionsUnitAndIpcTests
    {
        private static string FindPluginManifestPath(string projectFolderName)
        {
            string? dir = AppContext.BaseDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                string candidate = Path.Combine(dir, "src", projectFolderName, "plugin.json");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
                dir = Path.GetDirectoryName(dir);
            }
            throw new FileNotFoundException($"Could not locate plugin.json for {projectFolderName}");
        }

        private static async Task<byte[]> ReadFrameAsync(NetworkStream stream, CancellationToken ct)
        {
            byte[] headerBuf = new byte[BinaryMessageSerializer.HeaderSize];
            await ReadExactlyAsync(stream, headerBuf, 0, headerBuf.Length, ct);
            Assert.True(BinaryMessageSerializer.TryParseHeader(headerBuf, out var header));

            byte[] frame = new byte[header.Length];
            Array.Copy(headerBuf, 0, frame, 0, headerBuf.Length);
            if (header.Length > headerBuf.Length)
            {
                await ReadExactlyAsync(stream, frame, headerBuf.Length, header.Length - headerBuf.Length, ct);
            }
            return frame;
        }

        private static async Task ReadExactlyAsync(NetworkStream stream, byte[] buffer, int offset, int count, CancellationToken ct)
        {
            int total = 0;
            while (total < count)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(offset + total, count - total), ct);
                if (read <= 0) throw new EndOfStreamException("Stream closed prematurely.");
                total += read;
            }
        }

        // -----------------------------------------------------------------
        // SpanCoder.Extensions.HtmlPreview
        // -----------------------------------------------------------------

        [Fact]
        public void HtmlPreview_GetPreviewContent_ReturnsFallbackWhenEmptyAndPreservesHtml()
        {
            string fallback = SpanCoder.Extensions.HtmlPreview.Program.GetPreviewContent("");
            Assert.Contains("No active document", fallback);

            string html = "<html><body><h1>Hello</h1></body></html>";
            Assert.Equal(html, SpanCoder.Extensions.HtmlPreview.Program.GetPreviewContent(html));
        }

        [Fact]
        public async Task HtmlPreview_MainWithoutPort_ExitsCleanly()
        {
            await SpanCoder.Extensions.HtmlPreview.Program.Main(Array.Empty<string>());
        }

        [Fact]
        public void HtmlPreview_ManifestJson_IsValid()
        {
            string json = File.ReadAllText(FindPluginManifestPath("SpanCoder.Extensions.HtmlPreview"));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.Equal("html-preview", root.GetProperty("id").GetString());
            Assert.Equal("html-preview.show", root.GetProperty("commands")[0].GetProperty("id").GetString());
            Assert.Equal("html-preview-panel", root.GetProperty("panels")[0].GetProperty("id").GetString());
            Assert.Equal(1, root.GetProperty("menuItems").GetArrayLength());
            Assert.Equal(1, root.GetProperty("toolbarItems").GetArrayLength());
        }

        [Fact]
        public async Task HtmlPreview_TcpIpc_RegistersAndRespondsWithPanelUpdate()
        {
            string manifestPath = FindPluginManifestPath("SpanCoder.Extensions.HtmlPreview");
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var clientTask = SpanCoder.Extensions.HtmlPreview.Program.RunAsync(port, manifestPath, cts.Token);

            using var serverClient = await listener.AcceptTcpClientAsync(cts.Token);
            using var stream = serverClient.GetStream();

            // 1. Expect RegisterExtension
            byte[] regFrame = await ReadFrameAsync(stream, cts.Token);
            Assert.True(BinaryMessageSerializer.TryParseHeader(regFrame, out var regHeader));
            Assert.Equal(MessageTypes.RegisterExtension, regHeader.Type);
            var jsonBytes = BinaryMessageSerializer.ParseRegisterExtension(regFrame, out _);
            Assert.Contains("html-preview", System.Text.Encoding.UTF8.GetString(jsonBytes));

            // 2. Send ExecuteExtensionCommandWithContext for non-empty HTML
            string cmd = "html-preview.show";
            string file = "index.html";
            string html = "<p>Preview</p>";
            byte[] cmdBuf = new byte[BinaryMessageSerializer.HeaderSize + 12 + (cmd.Length + file.Length + html.Length) * 2];
            int cmdLen = BinaryMessageSerializer.WriteExecuteExtensionCommandWithContext(cmdBuf, cmd, file, html);
            await stream.WriteAsync(cmdBuf.AsMemory(0, cmdLen), cts.Token);
            await stream.FlushAsync(cts.Token);

            byte[] updateFrame = await ReadFrameAsync(stream, cts.Token);
            Assert.True(BinaryMessageSerializer.TryParseHeader(updateFrame, out var updateHeader));
            Assert.Equal(MessageTypes.UpdateExtensionPanel, updateHeader.Type);
            BinaryMessageSerializer.ParseUpdateExtensionPanel(updateFrame, out string panelId, out string content);
            Assert.Equal("html-preview-panel", panelId);
            Assert.Equal(html, content);

            // 3. Send ExecuteExtensionCommandWithContext for empty content -> fallback HTML
            byte[] emptyCmdBuf = new byte[BinaryMessageSerializer.HeaderSize + 12 + (cmd.Length + file.Length) * 2];
            int emptyCmdLen = BinaryMessageSerializer.WriteExecuteExtensionCommandWithContext(emptyCmdBuf, cmd, file, "");
            await stream.WriteAsync(emptyCmdBuf.AsMemory(0, emptyCmdLen), cts.Token);
            await stream.FlushAsync(cts.Token);

            byte[] fallbackFrame = await ReadFrameAsync(stream, cts.Token);
            BinaryMessageSerializer.ParseUpdateExtensionPanel(fallbackFrame, out string panelId2, out string content2);
            Assert.Equal("html-preview-panel", panelId2);
            Assert.Contains("No active document", content2);

            cts.Cancel();
            serverClient.Close();
            await clientTask;
        }

        // -----------------------------------------------------------------
        // SpanCoder.Extensions.Languages
        // -----------------------------------------------------------------

        [Fact]
        public async Task Languages_RunPythonScriptAsync_EnforcesPureCSharpPolicy()
        {
            var (success, output) = await SpanCoder.Extensions.Languages.Program.RunPythonScriptAsync("script.py", "print('hi')");
            Assert.False(success);
            Assert.Contains("disabled", output, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Languages_FindCargoProjectDir_FindsNearestCargoTomlOrEmpty()
        {
            Assert.Equal("", SpanCoder.Extensions.Languages.Program.FindCargoProjectDir(""));

            string tempRoot = Path.Combine(Path.GetTempPath(), "SpanCoder_CargoTest_" + Guid.NewGuid().ToString("N"));
            string nestedSrc = Path.Combine(tempRoot, "src", "nested");
            Directory.CreateDirectory(nestedSrc);
            try
            {
                string mainRs = Path.Combine(nestedSrc, "main.rs");
                File.WriteAllText(mainRs, "fn main() {}");

                // No Cargo.toml yet
                Assert.Equal("", SpanCoder.Extensions.Languages.Program.FindCargoProjectDir(mainRs));

                // Create Cargo.toml at tempRoot
                File.WriteAllText(Path.Combine(tempRoot, "Cargo.toml"), "[package]\nname = \"demo\"");
                Assert.Equal(tempRoot, SpanCoder.Extensions.Languages.Program.FindCargoProjectDir(mainRs));
            }
            finally
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, recursive: true);
            }
        }

        [Fact]
        public async Task Languages_MainWithoutPort_ExitsCleanly()
        {
            await SpanCoder.Extensions.Languages.Program.Main(Array.Empty<string>());
        }

        [Fact]
        public void Languages_ManifestJson_DefinesCoreLanguagesAndCommands()
        {
            string json = File.ReadAllText(FindPluginManifestPath("SpanCoder.Extensions.Languages"));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.Equal("languages-extension", root.GetProperty("id").GetString());
            Assert.Equal(9, root.GetProperty("languages").GetArrayLength());
            Assert.Equal(".py", root.GetProperty("languages")[0].GetProperty("extension").GetString());
            Assert.Equal("#", root.GetProperty("languages")[0].GetProperty("lineComment").GetString());
            Assert.Equal(2, root.GetProperty("commands").GetArrayLength());
            Assert.Equal(1, root.GetProperty("settings").GetArrayLength());
            Assert.Equal(1, root.GetProperty("statusBarItems").GetArrayLength());
        }

        [Fact]
        public async Task Languages_TcpIpc_RegistersAndUpdatesStatusBarOnRunPython()
        {
            string manifestPath = FindPluginManifestPath("SpanCoder.Extensions.Languages");
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var clientTask = SpanCoder.Extensions.Languages.Program.RunAsync(port, manifestPath, cts.Token);

            using var serverClient = await listener.AcceptTcpClientAsync(cts.Token);
            using var stream = serverClient.GetStream();

            byte[] regFrame = await ReadFrameAsync(stream, cts.Token);
            Assert.True(BinaryMessageSerializer.TryParseHeader(regFrame, out var regHeader));
            Assert.Equal(MessageTypes.RegisterExtension, regHeader.Type);

            string cmd = "languages.runPython";
            byte[] cmdBuf = new byte[BinaryMessageSerializer.HeaderSize + 4 + cmd.Length * 2];
            int cmdLen = BinaryMessageSerializer.WriteExecuteExtensionCommand(cmdBuf, cmd);
            await stream.WriteAsync(cmdBuf.AsMemory(0, cmdLen), cts.Token);
            await stream.FlushAsync(cts.Token);

            // Expect "Py: Running..." then "Py: Run Failed"
            byte[] status1 = await ReadFrameAsync(stream, cts.Token);
            Assert.True(BinaryMessageSerializer.TryParseHeader(status1, out var h1));
            Assert.Equal(MessageTypes.UpdateExtensionStatusBarItem, h1.Type);
            BinaryMessageSerializer.ParseUpdateExtensionStatusBarItem(status1, out string id1, out string text1, out _, out _);
            Assert.Equal("languages-status", id1);
            Assert.Equal("Py: Running...", text1);

            byte[] status2 = await ReadFrameAsync(stream, cts.Token);
            Assert.True(BinaryMessageSerializer.TryParseHeader(status2, out var h2));
            Assert.Equal(MessageTypes.UpdateExtensionStatusBarItem, h2.Type);
            BinaryMessageSerializer.ParseUpdateExtensionStatusBarItem(status2, out string id2, out string text2, out string tooltip2, out _);
            Assert.Equal("languages-status", id2);
            Assert.Equal("Py: Run Failed", text2);
            Assert.Contains("disabled", tooltip2, StringComparison.OrdinalIgnoreCase);

            cts.Cancel();
            serverClient.Close();
            await clientTask;
        }

        // -----------------------------------------------------------------
        // SpanCoder.Extensions.Prettier
        // -----------------------------------------------------------------

        [Fact]
        public void Prettier_FormatMockJs_IndentsBlocksAndClampsUnmatchedBraces()
        {
            string input = "function test() {\nconst x = 1;\nif (x > 0) {\nreturn x;\n}\n}\n}";
            string formatted = SpanCoder.Extensions.Prettier.Program.FormatMockJs(input);

            string expected = string.Join(Environment.NewLine, new[]
            {
                "function test() {",
                "    const x = 1;",
                "    if (x > 0) {",
                "        return x;",
                "    }",
                "}",
                "}"
            });
            Assert.Equal(expected, formatted);
        }

        [Fact]
        public void Prettier_FormatFallback_HandlesJsonJsCssInvalidJsonAndUnknown()
        {
            string formattedJson = SpanCoder.Extensions.Prettier.Program.FormatFallback("data.json", "{\"a\":1,\"b\":[2,3]}");
            Assert.Contains("  \"a\": 1,", formattedJson);

            string invalidJson = "{not valid json";
            Assert.Equal(invalidJson, SpanCoder.Extensions.Prettier.Program.FormatFallback("bad.json", invalidJson));

            string formattedCss = SpanCoder.Extensions.Prettier.Program.FormatFallback("styles.css", ".btn {\ncolor: red;\n}");
            Assert.Equal($".btn {{{Environment.NewLine}    color: red;{Environment.NewLine}}}", formattedCss);

            string plain = "hello   world";
            Assert.Equal(plain, SpanCoder.Extensions.Prettier.Program.FormatFallback("notes.txt", plain));
        }

        [Fact]
        public async Task Prettier_MainWithoutPort_ExitsCleanly()
        {
            await SpanCoder.Extensions.Prettier.Program.Main(Array.Empty<string>());
        }

        [Fact]
        public void Prettier_ManifestJson_IsValid()
        {
            string json = File.ReadAllText(FindPluginManifestPath("SpanCoder.Extensions.Prettier"));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.Equal("prettier-extension", root.GetProperty("id").GetString());
            Assert.Equal(3, root.GetProperty("formatters").GetArrayLength());
            Assert.Equal("prettier.format", root.GetProperty("commands")[0].GetProperty("id").GetString());
            Assert.Equal(1, root.GetProperty("settings").GetArrayLength());
        }

        [Fact]
        public async Task Prettier_TcpIpc_RegistersWithHost()
        {
            string manifestPath = FindPluginManifestPath("SpanCoder.Extensions.Prettier");
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var clientTask = SpanCoder.Extensions.Prettier.Program.RunAsync(port, manifestPath, cts.Token);

            using var serverClient = await listener.AcceptTcpClientAsync(cts.Token);
            using var stream = serverClient.GetStream();

            byte[] regFrame = await ReadFrameAsync(stream, cts.Token);
            Assert.True(BinaryMessageSerializer.TryParseHeader(regFrame, out var regHeader));
            Assert.Equal(MessageTypes.RegisterExtension, regHeader.Type);
            var jsonBytes = BinaryMessageSerializer.ParseRegisterExtension(regFrame, out _);
            Assert.Contains("prettier-extension", System.Text.Encoding.UTF8.GetString(jsonBytes));

            cts.Cancel();
            serverClient.Close();
            await clientTask;
        }
    }
}
