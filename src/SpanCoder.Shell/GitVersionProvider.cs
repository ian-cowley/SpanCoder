using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace SpanCoder.Shell
{
    public enum GitLineChangeType
    {
        Added,
        Modified,
        Deleted
    }

    public class GitFileStatus
    {
        public string FilePath { get; set; } = "";
        public string Status { get; set; } = ""; // "M", "A", "D", "U" (Untracked)
        public bool Staged { get; set; }
    }

    public class GitVersionProvider
    {
        public event Action<List<GitFileStatus>>? StatusChanged;
        public event Action<string, Dictionary<int, GitLineChangeType>>? LineChangesUpdated;

        private string? _workingDirectory;
        private string? _activeFilePath;
        private bool _isQuerying;

        public void SetWorkingDirectory(string? workingDir)
        {
            if (string.IsNullOrEmpty(workingDir)) return;

            if (File.Exists(workingDir))
            {
                _workingDirectory = Path.GetDirectoryName(workingDir);
            }
            else
            {
                _workingDirectory = workingDir;
            }
        }

        public async Task RefreshAsync(string? activeFilePath)
        {
            if (string.IsNullOrEmpty(_workingDirectory) || _isQuerying) return;
            _isQuerying = true;

            _activeFilePath = activeFilePath;

            try
            {
                // 1. Get status changes
                var changes = await QueryStatusChangesAsync();
                StatusChanged?.Invoke(changes);

                // 2. Get line diff for active file
                if (!string.IsNullOrEmpty(_activeFilePath) && File.Exists(_activeFilePath))
                {
                    var lineChanges = await QueryLineChangesAsync(_activeFilePath);
                    LineChangesUpdated?.Invoke(_activeFilePath, lineChanges);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GitVersionProvider] Refresh failed: {ex.Message}");
            }
            finally
            {
                _isQuerying = false;
            }
        }

        private async Task<List<GitFileStatus>> QueryStatusChangesAsync()
        {
            var changes = new List<GitFileStatus>();
            string output = await RunGitCommandAsync("status --porcelain");

            using var reader = new StringReader(output);
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (line.Length < 3) continue;

                char stagedCode = line[0];
                char unstagedCode = line[1];
                string filePath = line.Substring(3).Trim();

                // Clean up quotes around path if any
                if (filePath.StartsWith("\"") && filePath.EndsWith("\""))
                {
                    filePath = filePath.Substring(1, filePath.Length - 2);
                }

                if (stagedCode == '?' && unstagedCode == '?')
                {
                    changes.Add(new GitFileStatus { FilePath = filePath, Status = "U", Staged = false });
                }
                else
                {
                    if (stagedCode != ' ' && stagedCode != '\0')
                    {
                        changes.Add(new GitFileStatus
                        {
                            FilePath = filePath,
                            Status = MapStatusChar(stagedCode),
                            Staged = true
                        });
                    }
                    if (unstagedCode != ' ' && unstagedCode != '\0' && unstagedCode != '?')
                    {
                        changes.Add(new GitFileStatus
                        {
                            FilePath = filePath,
                            Status = MapStatusChar(unstagedCode),
                            Staged = false
                        });
                    }
                }
            }

            return changes;
        }

        private string MapStatusChar(char c)
        {
            switch (c)
            {
                case 'M': return "M";
                case 'A': return "A";
                case 'D': return "D";
                case 'R': return "R"; // Renamed
                default: return "M";
            }
        }

        private async Task<Dictionary<int, GitLineChangeType>> QueryLineChangesAsync(string filePath)
        {
            var lineChanges = new Dictionary<int, GitLineChangeType>();
            
            // Get relative path of the file
            string relativePath = filePath;
            if (!string.IsNullOrEmpty(_workingDirectory))
            {
                relativePath = Path.GetRelativePath(_workingDirectory, filePath).Replace("\\", "/");
            }

            // Run git diff HEAD -U0
            string output = await RunGitCommandAsync($"diff HEAD -U0 -- \"{relativePath}\"");

            using var reader = new StringReader(output);
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (line.StartsWith("@@"))
                {
                    // Format: @@ -oldStart,oldLen +newStart,newLen @@
                    int plusIdx = line.IndexOf('+');
                    if (plusIdx != -1)
                    {
                        int endIdx = line.IndexOf("@@", plusIdx);
                        if (endIdx != -1)
                        {
                            string part = line.Substring(plusIdx + 1, endIdx - (plusIdx + 1)).Trim();
                            int comma = part.IndexOf(',');
                            int newStart = 0;
                            int newLen = 1;
                            if (comma != -1)
                            {
                                int.TryParse(part.Substring(0, comma), out newStart);
                                int.TryParse(part.Substring(comma + 1), out newLen);
                            }
                            else
                            {
                                int.TryParse(part, out newStart);
                            }

                            // Old length check
                            int minusIdx = line.IndexOf('-');
                            int oldLen = 1;
                            if (minusIdx != -1)
                            {
                                string oldPart = line.Substring(minusIdx + 1, plusIdx - (minusIdx + 1)).Trim();
                                int oldComma = oldPart.IndexOf(',');
                                if (oldComma != -1)
                                {
                                    int.TryParse(oldPart.Substring(oldComma + 1), out oldLen);
                                }
                            }

                            if (oldLen == 0)
                            {
                                for (int l = 0; l < newLen; l++)
                                    lineChanges[newStart + l] = GitLineChangeType.Added;
                            }
                            else if (newLen == 0)
                            {
                                lineChanges[newStart] = GitLineChangeType.Deleted;
                            }
                            else
                            {
                                for (int l = 0; l < newLen; l++)
                                    lineChanges[newStart + l] = GitLineChangeType.Modified;
                            }
                        }
                    }
                }
            }

            return lineChanges;
        }

        public async Task StageFileAsync(string relativePath)
        {
            if (string.IsNullOrEmpty(_workingDirectory)) return;
            await RunGitCommandAsync($"add \"{relativePath}\"");
        }

        public async Task UnstageFileAsync(string relativePath)
        {
            if (string.IsNullOrEmpty(_workingDirectory)) return;
            await RunGitCommandAsync($"restore --staged \"{relativePath}\"");
        }

        public async Task CommitAsync(string message)
        {
            if (string.IsNullOrEmpty(_workingDirectory)) return;
            // Escape double quotes in message
            string escapedMsg = message.Replace("\"", "\\\"");
            await RunGitCommandAsync($"commit -m \"{escapedMsg}\"");
        }

        public async Task PushAsync()
        {
            if (string.IsNullOrEmpty(_workingDirectory)) return;
            await RunGitCommandAsync("push");
        }

        private async Task<string> RunGitCommandAsync(string args)
        {
            if (string.IsNullOrEmpty(_workingDirectory)) return "";

            try
            {
                using var process = new Process();
                process.StartInfo.FileName = "git";
                process.StartInfo.Arguments = args;
                process.StartInfo.WorkingDirectory = _workingDirectory;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true;

                process.Start();

                var outputBuilder = new StringBuilder();
                var outputTask = Task.Run(async () =>
                {
                    byte[] buffer = new byte[4096];
                    while (true)
                    {
                        int read = await process.StandardOutput.BaseStream.ReadAsync(buffer, 0, buffer.Length);
                        if (read <= 0) break;
                        outputBuilder.Append(Encoding.UTF8.GetString(buffer, 0, read));
                    }
                });

                await Task.WhenAny(outputTask, Task.Delay(2000)); // 2-second timeout
                
                if (process.HasExited)
                {
                    await outputTask;
                    return outputBuilder.ToString();
                }
                else
                {
                    process.Kill();
                    return "";
                }
            }
            catch
            {
                return "";
            }
        }
    }
}
