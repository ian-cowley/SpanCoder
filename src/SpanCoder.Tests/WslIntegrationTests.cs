using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using SpanCoder.App;
using SpanCoder.Contracts;

namespace SpanCoder.Tests
{
    public class WslIntegrationTests
    {
        private static bool IsWslAvailable()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "wsl.exe",
                    Arguments = "echo OK",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return false;
                string output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit();
                return proc.ExitCode == 0 && output == "OK";
            }
            catch
            {
                return false;
            }
        }

        private static string FindWorkspaceRoot()
        {
            string? dir = AppDomain.CurrentDomain.BaseDirectory;
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir, "SpanCoder.slnx")))
                {
                    return dir;
                }
                dir = Path.GetDirectoryName(dir);
            }
            throw new DirectoryNotFoundException("Could not find workspace root containing SpanCoder.slnx");
        }

        private static string GetWslPath(string windowsPath)
        {
            if (windowsPath.Length >= 2 && windowsPath[1] == ':')
            {
                char drive = char.ToLower(windowsPath[0]);
                string rest = windowsPath.Substring(2).Replace('\\', '/');
                return $"/mnt/{drive}{rest}";
            }
            return windowsPath.Replace('\\', '/');
        }

        private static int GetFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static void RunWslCommand(string command)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "wsl.exe",
                Arguments = command,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) throw new InvalidOperationException($"Failed to run WSL command: {command}");
            proc.WaitForExit();
            if (proc.ExitCode != 0)
            {
                string err = proc.StandardError.ReadToEnd();
                throw new InvalidOperationException($"WSL command failed with exit code {proc.ExitCode}: {err}");
            }
        }

        [Fact]
        public async Task TestWslRemoteConnectionAndPathMapping()
        {
            if (!IsWslAvailable())
            {
                Console.WriteLine("WSL is not available or not running. Skipping WSL Integration Test.");
                return;
            }

            string guid = Guid.NewGuid().ToString("N");
            string rootDir = FindWorkspaceRoot();
            string tempDir = Path.Combine(Path.GetTempPath(), "SpanCoderWslTest_" + guid);
            string workspaceDir = Path.Combine(tempDir, "workspace");
            string publishDir = Path.Combine(tempDir, "publish");

            Directory.CreateDirectory(workspaceDir);
            Directory.CreateDirectory(publishDir);

            // Write a test local file
            string localFilePath = Path.Combine(workspaceDir, "test_file.cs");
            string originalContent = "public class WslTestClass {}";
            File.WriteAllText(localFilePath, originalContent);

            // 1. Publish SpanCoder.Engine for Linux
            string projectPath = Path.Combine(rootDir, "src", "SpanCoder.Engine", "SpanCoder.Engine.csproj");
            var publishPsi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"publish \"{projectPath}\" -r linux-x64 -c Debug --self-contained -o \"{publishDir}\" -p:PublishAot=false",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using (var publishProc = Process.Start(publishPsi))
            {
                Assert.NotNull(publishProc);
                await publishProc.WaitForExitAsync();
                Assert.Equal(0, publishProc.ExitCode);
            }

            // WSL paths
            string wslPublishDir = $"/tmp/spancoder_test_{guid}";
            string wslWorkspaceDir = $"/tmp/spancoder_test_workspace_{guid}";

            // 2. Prepare directories in WSL and copy published engine & test workspace
            RunWslCommand($"mkdir -p {wslPublishDir}");
            RunWslCommand($"mkdir -p {wslWorkspaceDir}");

            // Copy published engine to WSL temp folder
            string wslPublishSource = GetWslPath(publishDir);
            RunWslCommand($"cp -r {wslPublishSource}/* {wslPublishDir}/");
            RunWslCommand($"chmod +x {wslPublishDir}/SpanCoder.Engine");

            // Copy workspace file to WSL remote workspace
            string wslWorkspaceSource = GetWslPath(localFilePath);
            RunWslCommand($"cp {wslWorkspaceSource} {wslWorkspaceDir}/test_file.cs");

            // 3. Find a free port and start the Engine daemon in WSL
            int port = GetFreePort();
            var enginePsi = new ProcessStartInfo
            {
                FileName = "wsl.exe",
                Arguments = $"{wslPublishDir}/SpanCoder.Engine --listen --port {port}",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var engineProc = Process.Start(enginePsi);
            Assert.NotNull(engineProc);

            // Allow WSL server to bind and start listening
            await Task.Delay(2000);

            // Connect from host Windows client to WSL engine via localhost port forwarding
            string mappingSpec = $"{workspaceDir}={wslWorkspaceDir}";
            using var connection = new IpcEngineConnection("127.0.0.1", port, mappingSpec);
            
            try
            {
                connection.Start();

                var loadTcs = new TaskCompletionSource<int>();
                var saveTcs = new TaskCompletionSource<bool>();
                string receivedText = "";

                connection.MessageReceived += (msg) =>
                {
                    if (BinaryMessageSerializer.TryParseHeader(msg, out var header))
                    {
                        if (header.Type == MessageTypes.DocumentChanged)
                        {
                            var text = BinaryMessageSerializer.ParseDocumentChanged(msg, out int docId, out int offset, out int addedLength, out int deletedLength);
                            receivedText = text.ToString();
                            loadTcs.TrySetResult(docId);
                        }
                        else if (header.Type == MessageTypes.SaveFileResponse)
                        {
                            saveTcs.TrySetResult(true);
                        }
                    }
                };

                // Send LoadFile request
                byte[] loadBuffer = new byte[BinaryMessageSerializer.HeaderSize + 4 + localFilePath.Length * 2];
                BinaryMessageSerializer.WriteLoadFile(loadBuffer, localFilePath);
                connection.Send(loadBuffer);

                // Wait for document load response
                var docId = await Task.WhenAny(loadTcs.Task, Task.Delay(5000)) == loadTcs.Task 
                    ? await loadTcs.Task 
                    : throw new TimeoutException("Timeout waiting for LoadFile response from WSL engine");

                Assert.Equal(originalContent, receivedText);

                // Send an edit: Insert " // Edited in WSL" at the end of the file
                string editString = " // Edited in WSL";
                byte[] editBuffer = new byte[BinaryMessageSerializer.HeaderSize + 4 + editString.Length * 2];
                BinaryMessageSerializer.WriteInsertText(editBuffer, docId, originalContent.Length, editString);
                connection.Send(editBuffer);

                // Wait for edit feedback
                await Task.Delay(1000);

                // Send SaveFile request
                byte[] saveBuffer = new byte[BinaryMessageSerializer.HeaderSize];
                BinaryMessageSerializer.WriteSaveFile(saveBuffer, docId);
                connection.Send(saveBuffer);

                // Wait for SaveFileResponse confirmation
                var saved = await Task.WhenAny(saveTcs.Task, Task.Delay(5000)) == saveTcs.Task 
                    ? await saveTcs.Task 
                    : throw new TimeoutException("Timeout waiting for SaveFileResponse from WSL engine");

                Assert.True(saved);

                // 4. Verify file was saved correctly inside the WSL filesystem
                var catPsi = new ProcessStartInfo
                {
                    FileName = "wsl.exe",
                    Arguments = $"cat {wslWorkspaceDir}/test_file.cs",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var catProc = Process.Start(catPsi);
                Assert.NotNull(catProc);
                string wslFileContent = (await catProc.StandardOutput.ReadToEndAsync()).TrimEnd();
                await catProc.WaitForExitAsync();

                Assert.Equal(originalContent + editString, wslFileContent);
            }
            finally
            {
                // Cleanup WSL engine process and directories
                try
                {
                    RunWslCommand($"pkill -f {wslPublishDir}/SpanCoder.Engine");
                    RunWslCommand($"rm -rf {wslPublishDir}");
                    RunWslCommand($"rm -rf {wslWorkspaceDir}");
                }
                catch { }

                // Cleanup Windows temp directory
                try
                {
                    Directory.Delete(tempDir, true);
                }
                catch { }
            }
        }
    }
}
