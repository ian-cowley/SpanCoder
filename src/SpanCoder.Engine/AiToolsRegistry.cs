using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SpanCoder.Engine
{
    public static class AiToolsRegistry
    {
        private static readonly string WorkspaceRoot = Directory.GetCurrentDirectory();

        public static List<LlmTool> GetToolsDefinition()
        {
            return new List<LlmTool>
            {
                new LlmTool
                {
                    Function = new LlmFunctionDefinition
                    {
                        Name = "read_file",
                        Description = "Reads a specific line range or full content of a file in the workspace.",
                        Parameters = JsonDocument.Parse(@"
                        {
                            ""type"": ""object"",
                            ""properties"": {
                                ""path"": { ""type"": ""string"", ""description"": ""Absolute path or relative path to the file."" },
                                ""startLine"": { ""type"": ""integer"", ""description"": ""1-based starting line number (optional)."" },
                                ""endLine"": { ""type"": ""integer"", ""description"": ""1-based ending line number (optional)."" }
                            },
                            ""required"": [""path""]
                        }").RootElement
                    }
                },
                new LlmTool
                {
                    Function = new LlmFunctionDefinition
                    {
                        Name = "write_file",
                        Description = "Overwrites or creates a file in the workspace with new content.",
                        Parameters = JsonDocument.Parse(@"
                        {
                            ""type"": ""object"",
                            ""properties"": {
                                ""path"": { ""type"": ""string"", ""description"": ""Absolute path or relative path to the file."" },
                                ""content"": { ""type"": ""string"", ""description"": ""Full contents of the file."" }
                            },
                            ""required"": [""path"", ""content""]
                        }").RootElement
                    }
                },
                new LlmTool
                {
                    Function = new LlmFunctionDefinition
                    {
                        Name = "edit_file_replace",
                        Description = "Replaces a specific unique contiguous block of text in an existing file.",
                        Parameters = JsonDocument.Parse(@"
                        {
                            ""type"": ""object"",
                            ""properties"": {
                                ""path"": { ""type"": ""string"", ""description"": ""Absolute path or relative path to the file."" },
                                ""target"": { ""type"": ""string"", ""description"": ""The exact text to be replaced (must match existing content exactly)."" },
                                ""replacement"": { ""type"": ""string"", ""description"": ""The replacement text."" }
                            },
                            ""required"": [""path"", ""target"", ""replacement""]
                        }").RootElement
                    }
                },
                new LlmTool
                {
                    Function = new LlmFunctionDefinition
                    {
                        Name = "list_workspace_files",
                        Description = "Recursively lists all files in the workspace (excluding bin, obj, .git, and .vs folders).",
                        Parameters = JsonDocument.Parse(@"
                        {
                            ""type"": ""object"",
                            ""properties"": {}
                        }").RootElement
                    }
                },
                new LlmTool
                {
                    Function = new LlmFunctionDefinition
                    {
                        Name = "search_grep",
                        Description = "Performs a regex text search across all workspace files.",
                        Parameters = JsonDocument.Parse(@"
                        {
                            ""type"": ""object"",
                            ""properties"": {
                                ""pattern"": { ""type"": ""string"", ""description"": ""Regular expression search pattern."" }
                            },
                            ""required"": [""pattern""]
                        }").RootElement
                    }
                },
                new LlmTool
                {
                    Function = new LlmFunctionDefinition
                    {
                        Name = "execute_terminal_command",
                        Description = "Executes a shell command in the project directory and returns output.",
                        Parameters = JsonDocument.Parse(@"
                        {
                            ""type"": ""object"",
                            ""properties"": {
                                ""command"": { ""type"": ""string"", ""description"": ""The shell command line to run."" }
                            },
                            ""required"": [""command""]
                        }").RootElement
                    }
                },
                new LlmTool
                {
                    Function = new LlmFunctionDefinition
                    {
                        Name = "run_build_and_test",
                        Description = "Compiles the solution and runs the unit/UI test suite.",
                        Parameters = JsonDocument.Parse(@"
                        {
                            ""type"": ""object"",
                            ""properties"": {}
                        }").RootElement
                    }
                }
            };
        }

        public static async Task<string> ExecuteToolAsync(string name, string argumentsJson)
        {
            try
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                var root = doc.RootElement;

                switch (name)
                {
                    case "read_file":
                        return ExecuteReadFile(root);
                    case "write_file":
                        return ExecuteWriteFile(root);
                    case "edit_file_replace":
                        return ExecuteEditFileReplace(root);
                    case "list_workspace_files":
                        return ExecuteListWorkspaceFiles();
                    case "search_grep":
                        return ExecuteSearchGrep(root);
                    case "execute_terminal_command":
                        return await ExecuteTerminalCommandAsync(root);
                    case "run_build_and_test":
                        return await ExecuteBuildAndTestAsync();
                    default:
                        return $"Error: Unknown tool name '{name}'";
                }
            }
            catch (Exception ex)
            {
                return $"Error parsing arguments: {ex.Message}";
            }
        }

        private static string GetFullPath(string path)
        {
            string fullPath = Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(WorkspaceRoot, path));

            string requiredPrefix = WorkspaceRoot;
            if (!requiredPrefix.EndsWith(Path.DirectorySeparatorChar))
            {
                requiredPrefix += Path.DirectorySeparatorChar;
            }

            if (!fullPath.Equals(WorkspaceRoot, StringComparison.OrdinalIgnoreCase) && 
                !fullPath.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException($"Access denied: Path '{path}' resolves to '{fullPath}', which is outside the workspace root '{WorkspaceRoot}'.");
            }
            return fullPath;
        }

        private static string ExecuteReadFile(JsonElement args)
        {
            if (!args.TryGetProperty("path", out var pathProp) || pathProp.GetString() is not string path)
                return "Error: Missing parameter 'path'";

            try
            {
                string fullPath = GetFullPath(path);
                if (!File.Exists(fullPath)) return $"Error: File not found at '{fullPath}'";

                if (IsBinaryFile(fullPath))
                    return $"Error: File '{path}' appears to be a binary file. Reading binary files is not supported.";

                string[] lines = File.ReadAllLines(fullPath);
                int startLine = args.TryGetProperty("startLine", out var sProp) ? sProp.GetInt32() : 1;
                int endLine = args.TryGetProperty("endLine", out var eProp) ? eProp.GetInt32() : lines.Length;

                startLine = Math.Max(1, startLine);
                endLine = Math.Min(lines.Length, endLine);

                if (startLine > endLine)
                {
                    return $"[File read successfully, but range {startLine}-{endLine} was empty. Total lines: {lines.Length}]";
                }

                var sb = new StringBuilder();
                for (int i = startLine - 1; i < endLine; i++)
                {
                    sb.AppendLine($"{i + 1}: {lines[i]}");
                }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"Error reading file: {ex.Message}";
            }
        }

        private static string ExecuteWriteFile(JsonElement args)
        {
            if (!args.TryGetProperty("path", out var pathProp) || pathProp.GetString() is not string path)
                return "Error: Missing parameter 'path'";
            if (!args.TryGetProperty("content", out var contentProp) || contentProp.GetString() is not string content)
                return "Error: Missing parameter 'content'";

            try
            {
                string fullPath = GetFullPath(path);
                string? dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.WriteAllText(fullPath, content, Encoding.UTF8);
                return $"Successfully wrote content to '{fullPath}'";
            }
            catch (Exception ex)
            {
                return $"Error writing file: {ex.Message}";
            }
        }

        private static string ExecuteEditFileReplace(JsonElement args)
        {
            if (!args.TryGetProperty("path", out var pathProp) || pathProp.GetString() is not string path)
                return "Error: Missing parameter 'path'";
            if (!args.TryGetProperty("target", out var targetProp) || targetProp.GetString() is not string target)
                return "Error: Missing parameter 'target'";
            if (!args.TryGetProperty("replacement", out var replacementProp) || replacementProp.GetString() is not string replacement)
                return "Error: Missing parameter 'replacement'";

            try
            {
                string fullPath = GetFullPath(path);
                if (!File.Exists(fullPath)) return $"Error: File not found at '{fullPath}'";

                if (IsBinaryFile(fullPath))
                    return $"Error: File '{path}' appears to be a binary file. Editing binary files is not supported.";

                string content = File.ReadAllText(fullPath);
                
                // Count occurrences
                int index = content.IndexOf(target, StringComparison.Ordinal);
                if (index == -1)
                {
                    return $"Error: Target text was not found in '{fullPath}'. Check formatting and indentation.";
                }
                
                int secondIndex = content.IndexOf(target, index + target.Length, StringComparison.Ordinal);
                if (secondIndex != -1)
                {
                    return $"Error: Target text matches multiple sections in '{fullPath}'. Provide a larger unique context.";
                }

                string newContent = content.Substring(0, index) + replacement + content.Substring(index + target.Length);
                File.WriteAllText(fullPath, newContent, Encoding.UTF8);
                return $"Successfully edited '{fullPath}'";
            }
            catch (Exception ex)
            {
                return $"Error editing file: {ex.Message}";
            }
        }

        private static string ExecuteListWorkspaceFiles()
        {
            try
            {
                var ignoredDirs = new HashSet<string> { ".git", ".vs", "bin", "obj", "nuget-local", "node_modules" };
                var files = new List<string>();

                void Walk(string dir)
                {
                    foreach (var file in Directory.GetFiles(dir))
                    {
                        files.Add(Path.GetRelativePath(WorkspaceRoot, file).Replace('\\', '/'));
                    }
                    foreach (var subDir in Directory.GetDirectories(dir))
                    {
                        var name = Path.GetFileName(subDir);
                        if (!ignoredDirs.Contains(name))
                        {
                            Walk(subDir);
                        }
                    }
                }

                Walk(WorkspaceRoot);
                return string.Join("\n", files);
            }
            catch (Exception ex)
            {
                return $"Error listing files: {ex.Message}";
            }
        }

        private static string ExecuteSearchGrep(JsonElement args)
        {
            if (!args.TryGetProperty("pattern", out var patProp) || patProp.GetString() is not string pattern)
                return "Error: Missing parameter 'pattern'";

            try
            {
                var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
                var results = new List<string>();
                var ignoredDirs = new HashSet<string> { ".git", ".vs", "bin", "obj", "nuget-local", "node_modules" };

                void Scan(string dir)
                {
                    foreach (var file in Directory.GetFiles(dir))
                    {
                        // Skip binaries
                        string ext = Path.GetExtension(file).ToLower();
                        if (ext == ".dll" || ext == ".exe" || ext == ".nupkg" || ext == ".pdb" || ext == ".png") continue;

                        try
                        {
                            string[] lines = File.ReadAllLines(file);
                            for (int i = 0; i < lines.Length; i++)
                            {
                                if (regex.IsMatch(lines[i]))
                                {
                                    string relative = Path.GetRelativePath(WorkspaceRoot, file).Replace('\\', '/');
                                    results.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
                                    if (results.Count > 100) return; // Limit results
                                }
                            }
                        }
                        catch { }
                    }
                    foreach (var subDir in Directory.GetDirectories(dir))
                    {
                        var name = Path.GetFileName(subDir);
                        if (!ignoredDirs.Contains(name))
                        {
                            Scan(subDir);
                        }
                    }
                }

                Scan(WorkspaceRoot);

                if (results.Count == 0) return "No matches found.";
                if (results.Count > 100) return string.Join("\n", results.Take(100)) + "\n... (more than 100 results found, list truncated)";
                return string.Join("\n", results);
            }
            catch (Exception ex)
            {
                return $"Error running grep: {ex.Message}";
            }
        }

        private static async Task<string> ExecuteTerminalCommandAsync(JsonElement args)
        {
            if (!args.TryGetProperty("command", out var cmdProp) || cmdProp.GetString() is not string command)
                return "Error: Missing parameter 'command'";

            string shell = "powershell.exe";
            string arguments = $"-Command \"{command.Replace("\"", "\\\"")}\"";

            if (!OperatingSystem.IsWindows())
            {
                shell = "/bin/sh";
                arguments = $"-c \"{command.Replace("\"", "\\\"")}\"";
            }

            try
            {
                using var process = new Process();
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = shell,
                    Arguments = arguments,
                    WorkingDirectory = WorkspaceRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                var sb = new StringBuilder();
                process.OutputDataReceived += (s, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) sb.AppendLine($"[Error] {e.Data}"); };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                var timeout = Task.Delay(TimeSpan.FromSeconds(60));
                var runTask = process.WaitForExitAsync();

                if (await Task.WhenAny(runTask, timeout) == timeout)
                {
                    process.Kill(true);
                    return sb.ToString() + "\n[Error: Command execution timed out after 60 seconds]";
                }

                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"Error executing command: {ex.Message}";
            }
        }

        private static async Task<string> ExecuteBuildAndTestAsync()
        {
            string slnxFile = Path.Combine(WorkspaceRoot, "SpanCoder.slnx");
            if (!File.Exists(slnxFile))
            {
                return "Error: SpanCoder.slnx solution file not found in the workspace root.";
            }

            var sb = new StringBuilder();
            sb.AppendLine("=== Triggering Build ===");
            
            // 1. Run build
            var buildOutput = await RunCliAsync("dotnet", $"build \"{slnxFile}\"");
            sb.AppendLine(buildOutput);

            if (buildOutput.Contains("Build FAILED") || buildOutput.Contains("error CS"))
            {
                sb.AppendLine("=== Result: Build Failed ===");
                return sb.ToString();
            }

            sb.AppendLine("=== Triggering Tests ===");
            
            // 2. Run test
            var testOutput = await RunCliAsync("dotnet", $"test \"{slnxFile}\" --no-build");
            sb.AppendLine(testOutput);

            return sb.ToString();
        }

        private static async Task<string> RunCliAsync(string filename, string arguments)
        {
            try
            {
                using var process = new Process();
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = filename,
                    Arguments = arguments,
                    WorkingDirectory = WorkspaceRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                var sb = new StringBuilder();
                process.OutputDataReceived += (s, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) sb.AppendLine(e.Data); };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await process.WaitForExitAsync();
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"Error running {filename} {arguments}: {ex.Message}";
            }
        }

        private static bool IsBinaryFile(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
                byte[] buffer = new byte[8000];
                int read = stream.Read(buffer, 0, buffer.Length);
                for (int i = 0; i < read; i++)
                {
                    if (buffer[i] == 0)
                    {
                        return true;
                    }
                }
                return false;
            }
            catch
            {
                return false;
            }
        }
    }
}
