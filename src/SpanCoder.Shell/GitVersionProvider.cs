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
        public event Action<string>? BranchChanged;

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

                // 3. Get current branch
                string branch = await GetCurrentBranchAsync();
                BranchChanged?.Invoke(branch);
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

        public async Task<string> GetHeadFileContentAsync(string relativePath)
        {
            if (string.IsNullOrEmpty(_workingDirectory)) return "";
            string gitPath = relativePath.Replace("\\", "/");
            return await RunGitCommandAsync($"show HEAD:\"{gitPath}\"");
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

        public async Task<string?> GetLineBlameAsync(string filePath, int line)
        {
            if (string.IsNullOrEmpty(_workingDirectory)) return null;

            string relativePath = filePath;
            if (File.Exists(filePath))
            {
                try
                {
                    relativePath = Path.GetRelativePath(_workingDirectory, filePath).Replace("\\", "/");
                }
                catch
                {
                    relativePath = filePath;
                }
            }

            string output = await RunGitCommandAsync($"blame -L {line},{line} --porcelain -- \"{relativePath}\"");
            if (string.IsNullOrEmpty(output)) return null;

            string author = "Unknown";
            string summary = "";
            long authorTime = 0;
            string shaAbbrev = "";

            using var reader = new StringReader(output);
            string? firstLine = await reader.ReadLineAsync();
            if (firstLine != null && firstLine.Length >= 8)
            {
                shaAbbrev = firstLine.Substring(0, 8);
                if (shaAbbrev.StartsWith("00000000"))
                {
                    return "You • Uncommitted changes";
                }
            }

            string? blameLine;
            while ((blameLine = await reader.ReadLineAsync()) != null)
            {
                if (blameLine.StartsWith("author "))
                {
                    author = blameLine.Substring(7).Trim();
                }
                else if (blameLine.StartsWith("author-time "))
                {
                    long.TryParse(blameLine.Substring(12).Trim(), out authorTime);
                }
                else if (blameLine.StartsWith("summary "))
                {
                    summary = blameLine.Substring(8).Trim();
                }
            }

            if (string.IsNullOrEmpty(shaAbbrev)) return null;

            string timeStr = authorTime > 0 ? GetRelativeTime(authorTime) : "";
            string timeAndAuthor = string.IsNullOrEmpty(timeStr) ? author : $"{author}, {timeStr}";
            
            return $"{shaAbbrev} ({timeAndAuthor}) • {summary}";
        }

        private string GetRelativeTime(long epochSeconds)
        {
            try
            {
                var time = DateTimeOffset.FromUnixTimeSeconds(epochSeconds).LocalDateTime;
                var span = DateTime.Now - time;
                if (span.TotalDays > 365)
                {
                    int years = (int)(span.TotalDays / 365);
                    return $"{years} year{(years > 1 ? "s" : "")} ago";
                }
                if (span.TotalDays > 30)
                {
                    int months = (int)(span.TotalDays / 30);
                    return $"{months} month{(months > 1 ? "s" : "")} ago";
                }
                if (span.TotalDays >= 1)
                {
                    int days = (int)span.TotalDays;
                    return $"{days} day{(days > 1 ? "s" : "")} ago";
                }
                if (span.TotalHours >= 1)
                {
                    int hours = (int)span.TotalHours;
                    return $"{hours} hour{(hours > 1 ? "s" : "")} ago";
                }
                if (span.TotalMinutes >= 1)
                {
                    int minutes = (int)span.TotalMinutes;
                    return $"{minutes} minute{(minutes > 1 ? "s" : "")} ago";
                }
                return "just now";
            }
            catch
            {
                return "";
            }
        }

        public async Task<string> GetCurrentBranchAsync()
        {
            if (string.IsNullOrEmpty(_workingDirectory)) return "";
            string output = await RunGitCommandAsync("rev-parse --abbrev-ref HEAD");
            return output.Trim();
        }

        public async Task<List<string>> GetLocalBranchesAsync()
        {
            var branches = new List<string>();
            if (string.IsNullOrEmpty(_workingDirectory)) return branches;

            string output = await RunGitCommandAsync("branch");
            using var reader = new StringReader(output);
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                string name = line.Replace("*", "").Trim();
                if (!string.IsNullOrEmpty(name))
                {
                    branches.Add(name);
                }
            }
            return branches;
        }

        public async Task<bool> CheckoutBranchAsync(string branchName)
        {
            if (string.IsNullOrEmpty(_workingDirectory)) return false;
            await RunGitCommandAsync($"checkout \"{branchName}\"");
            return true;
        }

        public async Task<bool> CreateAndCheckoutBranchAsync(string branchName)
        {
            if (string.IsNullOrEmpty(_workingDirectory)) return false;
            await RunGitCommandAsync($"checkout -b \"{branchName}\"");
            return true;
        }

        public async Task PullAsync()
        {
            if (string.IsNullOrEmpty(_workingDirectory)) return;
            await RunGitCommandAsync("pull");
        }

        public async Task StageAllAsync()
        {
            if (string.IsNullOrEmpty(_workingDirectory)) return;
            await RunGitCommandAsync("add .");
        }

        public async Task UnstageAllAsync()
        {
            if (string.IsNullOrEmpty(_workingDirectory)) return;
            await RunGitCommandAsync("restore --staged .");
        }
    }
}
