// File: Host.Win/Services/McpProcessHost.cs
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Host.Win.Services
{
    /// <summary>
    /// Launches and stops the local MCP server.
    /// </summary>
    public sealed class McpProcessHost : IDisposable
    {
        private Process? _process;
        private readonly string _mcpPath;
        private readonly int _port;
        private readonly string _pidFile;
        private readonly string _requirementsHashFile;
        private readonly string _authToken;
        private readonly bool _verboseLogging;
        private IntPtr _jobHandle = IntPtr.Zero;

        public McpProcessHost(string mcpPath, int port, bool verboseLogging = false)
        {
            _mcpPath = mcpPath;
            _port = port;
            _verboseLogging = verboseLogging;
            var localDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LISA");
            Directory.CreateDirectory(localDir);
            _pidFile = Path.Combine(localDir, "mcp.pid");
            _requirementsHashFile = Path.Combine(localDir, "mcp-requirements.sha256");
            _authToken = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        }

        public string AuthToken => _authToken;

        public void Start()
        {
            if (_process != null && !_process.HasExited) return;
            if (!File.Exists(_mcpPath))
            {
                Trace.TraceWarning($"MCP path not found: {_mcpPath}");
                return;
            }

            var workingDir = Path.GetDirectoryName(_mcpPath) ?? Environment.CurrentDirectory;
            Trace.WriteLine($"MCP working dir resolved: {workingDir}");
            var pythonPath = EnsureVenv(workingDir);
            if (pythonPath == null)
            {
                Trace.TraceError("Unable to create or locate Python interpreter for MCP.");
                return;
            }
            Trace.WriteLine($"MCP python resolved: {pythonPath}");

            TryKillExistingPid();

            var psi = new ProcessStartInfo
            {
                FileName = pythonPath,
                Arguments = "main.py",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDir,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            psi.Environment["MCP_PORT"] = _port.ToString();
            psi.Environment["PYTHONPATH"] = workingDir;
            psi.Environment["MCP_AUTH_TOKEN"] = _authToken;
            psi.Environment["LISA_VERBOSE_LOGGING"] = _verboseLogging ? "1" : "0";
            Trace.WriteLine($"MCP env: MCP_PORT={_port} verbose={_verboseLogging}");

            try
            {
                _process = Process.Start(psi);
                if (_process == null)
                {
                    Trace.TraceError("Failed to start MCP process.");
                    return;
                }

                Trace.WriteLine($"MCP process started on port {_port} (PID {_process.Id}). WorkingDir={workingDir}");
                AttachToJob(_process);
                _process.OutputDataReceived += (_, args) =>
                {
                    if (!string.IsNullOrWhiteSpace(args.Data))
                    {
                        if (_verboseLogging || !args.Data.StartsWith("DEBUG:", StringComparison.OrdinalIgnoreCase))
                        {
                            Trace.WriteLine($"[MCP] {args.Data}");
                        }
                    }
                };
                _process.ErrorDataReceived += (_, args) =>
                {
                    if (!string.IsNullOrWhiteSpace(args.Data))
                    {
                        var line = args.Data;
                        if (line.StartsWith("INFO:", StringComparison.OrdinalIgnoreCase))
                        {
                            Trace.WriteLine($"[MCP] {line}");
                        }
                        else if (line.StartsWith("DEBUG:", StringComparison.OrdinalIgnoreCase))
                        {
                            if (_verboseLogging)
                            {
                                Trace.WriteLine($"[MCP] {line}");
                            }
                        }
                        else if (line.StartsWith("WARNING:", StringComparison.OrdinalIgnoreCase))
                        {
                            Trace.TraceWarning($"[MCP] {line}");
                        }
                        else if (line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase) || line.StartsWith("CRITICAL:", StringComparison.OrdinalIgnoreCase))
                        {
                            Trace.TraceError($"[MCP ERR] {line}");
                        }
                        else
                        {
                            Trace.TraceWarning($"[MCP] {line}");
                        }
                    }
                };
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();

                try
                {
                    File.WriteAllText(_pidFile, _process.Id.ToString());
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning($"Failed to write MCP pid file: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Trace.TraceError($"Failed to start MCP process: {ex.Message}");
            }
        }

        public void Stop()
        {
            try
            {
                TryKillExistingPid();
                if (_process != null && !_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(5000);
                }
            }
            catch (Exception ex)
            {
                Trace.TraceError($"Failed to stop MCP process: {ex.Message}");
            }
            finally
            {
                if (_jobHandle != IntPtr.Zero)
                {
                    CloseHandle(_jobHandle);
                    _jobHandle = IntPtr.Zero;
                }
                try
                {
                    if (File.Exists(_pidFile))
                    {
                        File.Delete(_pidFile);
                    }
                }
                catch { }
            }
        }

        public void Dispose()
        {
            Stop();
            _process?.Dispose();
            _process = null;
        }

        private string? EnsureVenv(string workingDir)
        {
            var venvPath = Path.Combine(workingDir, ".venv");
            var pythonExe = Path.Combine(venvPath, "Scripts", "python.exe");
            var requirements = Path.Combine(workingDir, "requirements.txt");

            if (!Directory.Exists(venvPath) || !File.Exists(pythonExe))
            {
                Trace.WriteLine("Creating MCP virtual environment...");
                var createPsi = new ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = "-m venv .venv",
                    WorkingDirectory = workingDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                try
                {
                    var createProc = Process.Start(createPsi);
                    createProc?.WaitForExit(15000);
                }
                catch
                {
                    createPsi.FileName = "py";
                    createPsi.Arguments = "-3 -m venv .venv";
                    try
                    {
                        var createProc = Process.Start(createPsi);
                        createProc?.WaitForExit(15000);
                    }
                    catch (Exception ex)
                    {
                        Trace.TraceError($"MCP venv creation failed: {ex.Message}");
                    }
                }
            }

            if (!File.Exists(pythonExe))
            {
                Trace.TraceError("MCP venv creation failed; python.exe missing.");
                return null;
            }

            if (File.Exists(requirements))
            {
                var hash = ComputeFileHash(requirements);
                var existingHash = ReadHash(_requirementsHashFile);
                Trace.WriteLine($"MCP requirements hash: {hash} (stored={existingHash ?? "none"})");
                if (!string.Equals(hash, existingHash, StringComparison.OrdinalIgnoreCase))
                {
                    Trace.WriteLine("Installing MCP requirements...");
                    RunSilently(pythonExe, "-m pip install --upgrade pip", workingDir, verboseLogging: _verboseLogging);
                    if (RunSilently(pythonExe, "-m pip install -r requirements.txt", workingDir, verboseLogging: _verboseLogging))
                    {
                        WriteHash(_requirementsHashFile, hash);
                    }
                }
            }

            return pythonExe;
        }

        private static bool RunSilently(string fileName, string arguments, string workingDir, bool verboseLogging = false)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    WorkingDirectory = workingDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    var stdout = proc.StandardOutput.ReadToEnd();
                    var stderr = proc.StandardError.ReadToEnd();
                    proc.WaitForExit(20000);
                    var ok = proc.ExitCode == 0;
                    if ((!string.IsNullOrWhiteSpace(stdout)) && (verboseLogging || !ok))
                    {
                        Trace.WriteLine($"[MCP cmd] {fileName} {arguments} -> {stdout}");
                    }
                    if ((!string.IsNullOrWhiteSpace(stderr)) && (verboseLogging || !ok))
                    {
                        Trace.TraceWarning($"[MCP cmd ERR] {fileName} {arguments} -> {stderr}");
                    }
                    return ok;
                }
            }
            catch (Exception ex)
            {
                Trace.TraceError($"Command failed: {fileName} {arguments} ({ex.Message})");
            }
            return false;
        }

        private void TryKillExistingPid()
        {
            try
            {
                if (!File.Exists(_pidFile)) return;
                var text = File.ReadAllText(_pidFile).Trim();
                if (!int.TryParse(text, out var pid)) return;
                using var proc = Process.GetProcesses().FirstOrDefault(p => p.Id == pid);
                if (proc == null) return;
                if (!proc.HasExited)
                {
                    Trace.WriteLine($"Killing existing MCP process PID {pid} before start.");
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(5000);
                }
            }
            catch
            {
                // ignore
            }
        }

        private static string ComputeFileHash(string path)
        {
            using var stream = File.OpenRead(path);
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = sha.ComputeHash(stream);
            return Convert.ToHexString(hash);
        }

        private static string? ReadHash(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
            }
            catch
            {
                return null;
            }
        }

        private static void WriteHash(string path, string hash)
        {
            try
            {
                File.WriteAllText(path, hash);
            }
            catch
            {
                // ignore
            }
        }

        private void AttachToJob(Process process)
        {
            try
            {
                if (_jobHandle == IntPtr.Zero)
                {
                    _jobHandle = CreateJobObject(IntPtr.Zero, null);
                    var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                    {
                        BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                        {
                            LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                        }
                    };
                    int length = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
                    SetInformationJobObject(_jobHandle, JobObjectInfoType.ExtendedLimitInformation, ref info, (uint)length);
                }
                AssignProcessToJobObject(_jobHandle, process.Handle);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Failed to attach MCP to job object: {ex.Message}");
            }
        }

        private const int JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

        private enum JobObjectInfoType
        {
            ExtendedLimitInformation = 9
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public int LimitFlags;
            public nuint MinimumWorkingSetSize;
            public nuint MaximumWorkingSetSize;
            public int ActiveProcessLimit;
            public long Affinity;
            public int PriorityClass;
            public int SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public nuint ProcessMemoryLimit;
            public nuint JobMemoryLimit;
            public nuint PeakProcessMemoryUsed;
            public nuint PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? name);

        [DllImport("kernel32.dll")]
        private static extern bool SetInformationJobObject(IntPtr hJob, JobObjectInfoType infoType, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll")]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
