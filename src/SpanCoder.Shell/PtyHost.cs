using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace SpanCoder.Shell
{
    public class PtyHost : IDisposable
    {
        public event Action<byte[], int>? DataReceived;
        public event Action? Exited;

        private Process? _process;
        private SafeFileHandle? _hInputWrite;
        private SafeFileHandle? _hOutputRead;
        private IntPtr _hPC = IntPtr.Zero;
        private bool _isFallback;
        private bool _isDisposed;
        private CancellationTokenSource? _cts;

        private Stream? _fallbackInput;

        public bool IsFallback => _isFallback;

        public bool Start(string shellPath, string[] args, string workingDir, int cols, int rows)
        {
            _cts = new CancellationTokenSource();

            // Try native PTY
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    if (StartWindowsConPty(shellPath, args, workingDir, cols, rows))
                    {
                        return true;
                    }
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    if (StartUnixPty(shellPath, args, workingDir, cols, rows))
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PtyHost] Native PTY start failed: {ex.Message}. Falling back to standard redirection.");
            }

            // Fallback to standard process redirection
            return StartFallback(shellPath, args, workingDir);
        }

        public void Write(string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            Write(bytes, 0, bytes.Length);
        }

        public void Write(byte[] bytes, int offset, int count)
        {
            if (_isDisposed) return;

            try
            {
                if (_isFallback)
                {
                    _fallbackInput?.Write(bytes, offset, count);
                    _fallbackInput?.Flush();
                }
                else
                {
                    if (_hInputWrite != null && !_hInputWrite.IsInvalid)
                    {
                        using var fs = new FileStream(_hInputWrite, FileAccess.Write);
                        fs.Write(bytes, offset, count);
                        fs.Flush();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PtyHost] Write failed: {ex.Message}");
            }
        }

        public void Resize(int cols, int rows)
        {
            if (_isDisposed || _isFallback) return;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && _hPC != IntPtr.Zero)
            {
                COORD size = new COORD { X = (short)cols, Y = (short)rows };
                ResizePseudoConsole(_hPC, size);
            }
        }

        private bool StartFallback(string shellPath, string[] args, string workingDir)
        {
            _isFallback = true;
            _process = new Process();
            _process.StartInfo.FileName = shellPath;
            _process.StartInfo.Arguments = string.Join(" ", args);
            _process.StartInfo.WorkingDirectory = workingDir;
            _process.StartInfo.RedirectStandardInput = true;
            _process.StartInfo.RedirectStandardOutput = true;
            _process.StartInfo.RedirectStandardError = true;
            _process.StartInfo.UseShellExecute = false;
            _process.StartInfo.CreateNoWindow = true;

            try
            {
                if (!_process.Start()) return false;
            }
            catch
            {
                // Try cmd/bash defaults if custom shellPath fails
                try
                {
                    _process.StartInfo.FileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd.exe" : "/bin/sh";
                    _process.StartInfo.Arguments = "";
                    if (!_process.Start()) return false;
                }
                catch
                {
                    return false;
                }
            }

            _fallbackInput = _process.StandardInput.BaseStream;

            // Asynchronously read stdout and stderr
            StartReadLoop(_process.StandardOutput.BaseStream);
            StartReadLoop(_process.StandardError.BaseStream);

            // Wait for exit
            Task.Run(async () =>
            {
                try
                {
                    await _process.WaitForExitAsync();
                    Exited?.Invoke();
                }
                catch {}
            });

            return true;
        }

        private void StartReadLoop(Stream stream)
        {
            Task.Run(async () =>
            {
                byte[] buffer = new byte[4096];
                try
                {
                    while (!_isDisposed && _cts != null && !_cts.Token.IsCancellationRequested)
                    {
                        int read = await stream.ReadAsync(buffer, 0, buffer.Length, _cts.Token);
                        if (read <= 0) break;
                        DataReceived?.Invoke(buffer, read);
                    }
                }
                catch {}
            });
        }

        #region Windows ConPTY

        [StructLayout(LayoutKind.Sequential)]
        private struct COORD
        {
            public short X;
            public short Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFOEX
        {
            public STARTUPINFO StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint flags, out IntPtr phpc);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int ClosePseudoConsole(IntPtr hpc);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int ResizePseudoConsole(IntPtr hpc, COORD size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, uint nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr Attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcessW(
            string lpApplicationName,
            string lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string lpCurrentDirectory,
            ref STARTUPINFOEX lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
        private const IntPtr PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = (IntPtr)0x00020016;

        private bool StartWindowsConPty(string shellPath, string[] args, string workingDir, int cols, int rows)
        {
            // 1. Create pipes
            if (!CreatePipe(out var hInputRead, out _hInputWrite, IntPtr.Zero, 0)) return false;
            if (!CreatePipe(out _hOutputRead, out var hOutputWrite, IntPtr.Zero, 0)) return false;

            // 2. Create pseudoconsole
            COORD size = new COORD { X = (short)cols, Y = (short)rows };
            int hr = CreatePseudoConsole(size, hInputRead, hOutputWrite, 0, out _hPC);
            
            // Close handles we passed to the console, they are duplicated inside
            hInputRead.Close();
            hOutputWrite.Close();

            if (hr != 0) return false;

            // 3. Initialize Startup Info with PTY attribute
            IntPtr lpSize = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref lpSize);
            
            IntPtr lpAttributeList = Marshal.AllocHGlobal(lpSize);
            if (!InitializeProcThreadAttributeList(lpAttributeList, 1, 0, ref lpSize))
            {
                Marshal.FreeHGlobal(lpAttributeList);
                return false;
            }

            // Pin pseudoconsole pointer
            IntPtr pPC = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(pPC, _hPC);

            if (!UpdateProcThreadAttribute(lpAttributeList, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, pPC, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            {
                Marshal.FreeHGlobal(pPC);
                Marshal.FreeHGlobal(lpAttributeList);
                return false;
            }

            var siEx = new STARTUPINFOEX();
            siEx.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            siEx.lpAttributeList = lpAttributeList;

            string cmdLine = $"\"{shellPath}\" {string.Join(" ", args)}";
            
            // 4. Create Process
            bool success = CreateProcessW(
                shellPath,
                cmdLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                EXTENDED_STARTUPINFO_PRESENT,
                IntPtr.Zero,
                workingDir,
                ref siEx,
                out var pi);

            // Free allocated attributes list and pinned pointer
            Marshal.FreeHGlobal(pPC);
            Marshal.FreeHGlobal(lpAttributeList);

            if (!success)
            {
                ClosePseudoConsole(_hPC);
                _hPC = IntPtr.Zero;
                return false;
            }

            // Close process/thread handles we don't need directly
            if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
            if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);

            // Start reading PTY stdout
            Task.Run(() =>
            {
                byte[] buffer = new byte[4096];
                try
                {
                    using var fs = new FileStream(_hOutputRead, FileAccess.Read);
                    while (!_isDisposed && _cts != null && !_cts.Token.IsCancellationRequested)
                    {
                        int read = fs.Read(buffer, 0, buffer.Length);
                        if (read <= 0) break;
                        DataReceived?.Invoke(buffer, read);
                    }
                }
                catch {}
                finally
                {
                    Exited?.Invoke();
                }
            });

            return true;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        #endregion

        #region Unix PTY

        [DllImport("libutil", SetLastError = true)] // Linux
        private static extern int forkpty(out int amaster, StringBuilder? name, IntPtr termp, IntPtr winp);

        [DllImport("libc", EntryPoint = "forkpty", SetLastError = true)] // macOS
        private static extern int forkpty_mac(out int amaster, StringBuilder? name, IntPtr termp, IntPtr winp);

        [DllImport("libc", SetLastError = true)]
        private static extern int execvp(string file, string?[] argv);

        [DllImport("libc", SetLastError = true)]
        private static extern int chdir(string path);

        [DllImport("libc", SetLastError = true)]
        private static extern int write(int fd, byte[] buf, int count);

        [DllImport("libc", SetLastError = true)]
        private static extern int read(int fd, byte[] buf, int count);

        [DllImport("libc", SetLastError = true)]
        private static extern int close(int fd);

        private int _unixFd = -1;

        private bool StartUnixPty(string shellPath, string[] args, string workingDir, int cols, int rows)
        {
            int masterFd;
            int pid;

            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    pid = forkpty_mac(out masterFd, null, IntPtr.Zero, IntPtr.Zero);
                }
                else
                {
                    pid = forkpty(out masterFd, null, IntPtr.Zero, IntPtr.Zero);
                }
            }
            catch
            {
                return false;
            }

            if (pid < 0) return false;

            if (pid == 0)
            {
                // Child process
                chdir(workingDir);
                string?[] argv = new string?[args.Length + 2];
                argv[0] = shellPath;
                for (int i = 0; i < args.Length; i++) argv[i + 1] = args[i];
                argv[args.Length + 1] = null;

                execvp(shellPath, argv);
                Environment.Exit(-1);
            }

            // Parent process
            _unixFd = masterFd;

            Task.Run(() =>
            {
                byte[] buffer = new byte[4096];
                try
                {
                    while (!_isDisposed && _unixFd != -1)
                    {
                        int bytesRead = read(_unixFd, buffer, buffer.Length);
                        if (bytesRead <= 0) break;
                        DataReceived?.Invoke(buffer, bytesRead);
                    }
                }
                catch {}
                finally
                {
                    Exited?.Invoke();
                }
            });

            return true;
        }

        #endregion

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            _cts?.Cancel();
            _cts?.Dispose();

            try
            {
                if (_isFallback)
                {
                    _fallbackInput?.Dispose();
                    _process?.Kill();
                    _process?.Dispose();
                }
                else
                {
                    _hInputWrite?.Close();
                    _hOutputRead?.Close();

                    if (_hPC != IntPtr.Zero)
                    {
                        ClosePseudoConsole(_hPC);
                        _hPC = IntPtr.Zero;
                    }

                    if (_unixFd != -1)
                    {
                        close(_unixFd);
                        _unixFd = -1;
                    }
                }
            }
            catch {}
        }
    }
}
