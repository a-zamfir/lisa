// File: Host.Win/Services/McpGatewayProcessHost.cs
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Host.Win.Services
{
    /// <summary>
    /// Launches and stops the MCP Gateway orchestrator.
    /// </summary>
    public sealed class McpGatewayProcessHost : IDisposable
    {
        private Process? _process;
        private readonly string _gatewayPath;
        private readonly int _port;
        private readonly string _gatewayRoot;
        private readonly string _pidFile;
        private readonly string _requirementsHashFile;
        private readonly string _authToken;
        private readonly bool _verboseLogging;
        private IntPtr _jobHandle = IntPtr.Zero;

        public McpGatewayProcessHost(string gatewayPath, int port, bool verboseLogging = false)
        {
            _gatewayPath = gatewayPath;
            _port = port;
            _gatewayRoot = Path.GetDirectoryName(gatewayPath) ?? Environment.CurrentDirectory;
            _verboseLogging = verboseLogging;
            var localDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LISA");
            Directory.CreateDirectory(localDir);
            _pidFile = Path.Combine(localDir, "mcp-gateway.pid");
            _requirementsHashFile = Path.Combine(localDir, "mcp-gateway-requirements.sha256");
            _authToken = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        }

        public string AuthToken => _authToken;

        public void Start()
        {
            if (_process != null && !_process.HasExited) return;
            if (!File.Exists(_gatewayPath))
            {
                Trace.TraceWarning($"MCP Gateway path not found: {_gatewayPath}");
                return;
            }

            var workingDir = Path.GetDirectoryName(_gatewayPath) ?? Environment.CurrentDirectory;
            Trace.WriteLine($"MCP Gateway working dir resolved: {workingDir}");
            var pythonPath = EnsureVenv(workingDir);
            if (pythonPath == null)
            {
                Trace.TraceError("Unable to create or locate Python interpreter for MCP Gateway.");
                return;
            }
            Trace.WriteLine($"MCP Gateway python resolved: {pythonPath}");

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
            psi.Environment["MCP_GATEWAY_PORT"] = _port.ToString();
            psi.Environment["MCP_PORT"] = _port.ToString(); // Fallback
            psi.Environment["PYTHONPATH"] = workingDir;
            psi.Environment["MCP_AUTH_TOKEN"] = _authToken;
            psi.Environment["LISA_VERBOSE_LOGGING"] = _verboseLogging ? "1" : "0";
            Trace.WriteLine($"MCP Gateway env: MCP_GATEWAY_PORT={_port} verbose={_verboseLogging}");

            try
            {
                _process = Process.Start(psi);
                if (_process == null)
                {
                    Trace.TraceError("Failed to start MCP Gateway process.");
                    return;
                }

                Trace.WriteLine($"MCP Gateway process started on port {_port} (PID {_process.Id}). WorkingDir={workingDir}");
                AttachToJob(_process);
                _process.OutputDataReceived += (_, args) =>
                {
                    if (!string.IsNullOrWhiteSpace(args.Data))
                    {
                        if (_verboseLogging || !args.Data.StartsWith("DEBUG:", StringComparison.OrdinalIgnoreCase))
                        {
                            Trace.WriteLine($"[MCP Gateway] {args.Data}");
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
                            Trace.WriteLine($"[MCP Gateway] {line}");
                        }
                        else if (line.StartsWith("DEBUG:", StringComparison.OrdinalIgnoreCase))
                        {
                            if (_verboseLogging)
                            {
                                Trace.WriteLine($"[MCP Gateway] {line}");
                            }
                        }
                        else if (line.StartsWith("WARNING:", StringComparison.OrdinalIgnoreCase))
                        {
                            Trace.TraceWarning($"[MCP Gateway] {line}");
                        }
                        else if (line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase) || line.StartsWith("CRITICAL:", StringComparison.OrdinalIgnoreCase))
                        {
                            Trace.TraceError($"[MCP Gateway ERR] {line}");
                        }
                        else
                        {
                            Trace.TraceWarning($"[MCP Gateway] {line}");
                        }
                    }
                };
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();

                try
                {
                    var commandLine = $"{_process.StartInfo.FileName} {_process.StartInfo.Arguments}";
                    WritePidFile(_pidFile, _process.Id, commandLine);
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning($"Failed to write MCP Gateway pid file: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Trace.TraceError($"Failed to start MCP Gateway process: {ex.Message}");
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
                Trace.TraceError($"Failed to stop MCP Gateway process: {ex.Message}");
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
            var requirementsLock = Path.Combine(workingDir, "requirements-lock.txt");
            var requirements = File.Exists(requirementsLock) ? requirementsLock : Path.Combine(workingDir, "requirements.txt");

            if (!Directory.Exists(venvPath) || !File.Exists(pythonExe))
            {
                Trace.WriteLine("Creating MCP Gateway virtual environment...");
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
                var created = false;
                try
                {
                    if (_verboseLogging)
                    {
                        Trace.WriteLine($"MCP Gateway venv: Attempting with 'python' command...");
                    }
                    var createProc = Process.Start(createPsi);
                    createProc?.WaitForExit(15000);
                    if (createProc != null && createProc.ExitCode == 0)
                    {
                        created = true;
                        if (_verboseLogging)
                        {
                            Trace.WriteLine("MCP Gateway venv: Created successfully with 'python' command.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (_verboseLogging)
                    {
                        Trace.WriteLine($"MCP Gateway venv: 'python' command failed ({ex.Message}), trying 'py -3' fallback...");
                    }
                }

                if (!created)
                {
                    createPsi.FileName = "py";
                    createPsi.Arguments = "-3 -m venv .venv";
                    try
                    {
                        var createProc = Process.Start(createPsi);
                        createProc?.WaitForExit(15000);
                        if (createProc != null && createProc.ExitCode == 0)
                        {
                            created = true;
                            if (_verboseLogging)
                            {
                                Trace.WriteLine("MCP Gateway venv: Created successfully with 'py -3' command.");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Trace.TraceError($"MCP Gateway venv creation failed: {ex.Message}");
                    }
                }

                if (!created && _verboseLogging)
                {
                    Trace.TraceWarning("MCP Gateway venv: Creation may have failed. Checking for python.exe...");
                }
            }
            else if (_verboseLogging)
            {
                Trace.WriteLine($"MCP Gateway venv: Already exists at {venvPath}");
            }

            if (!File.Exists(pythonExe))
            {
                Trace.TraceError("MCP Gateway venv creation failed; python.exe missing.");
                return null;
            }

            if (File.Exists(requirements))
            {
                var hash = ComputeFileHash(requirements);
                var existingHash = ReadHash(_requirementsHashFile);
                Trace.WriteLine($"MCP Gateway requirements hash: {hash} (stored={existingHash ?? "none"})");
                if (!string.Equals(hash, existingHash, StringComparison.OrdinalIgnoreCase))
                {
                    Trace.WriteLine("Installing MCP Gateway requirements...");
                    if (_verboseLogging)
                    {
                        Trace.WriteLine("MCP Gateway bootstrap: Upgrading pip...");
                    }
                    RunSilently(pythonExe, "-m pip install --upgrade pip", workingDir, verboseLogging: _verboseLogging);
                    if (_verboseLogging)
                    {
                        var reqFile = Path.GetFileName(requirements);
                        Trace.WriteLine($"MCP Gateway bootstrap: Installing {reqFile}...");
                    }
                    if (RunSilently(pythonExe, $"-m pip install -r {Path.GetFileName(requirements)}", workingDir, verboseLogging: _verboseLogging))
                    {
                        WriteHash(_requirementsHashFile, hash);
                        Trace.WriteLine("MCP Gateway requirements installed successfully.");
                    }
                    else
                    {
                        Trace.TraceError("MCP Gateway requirements installation failed. Check pip output above.");
                    }
                }
                else if (_verboseLogging)
                {
                    Trace.WriteLine("MCP Gateway requirements: Hash unchanged, skipping installation.");
                }
            }
            else if (_verboseLogging)
            {
                Trace.WriteLine($"MCP Gateway requirements.txt not found at {requirements}");
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
                        Trace.WriteLine($"[MCP Gateway cmd] {fileName} {arguments} -> {stdout}");
                    }
                    if ((!string.IsNullOrWhiteSpace(stderr)) && (verboseLogging || !ok))
                    {
                        Trace.TraceWarning($"[MCP Gateway cmd ERR] {fileName} {arguments} -> {stderr}");
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

                var pidInfo = ReadPidFile(_pidFile);
                if (pidInfo == null) return;

                using var proc = Process.GetProcesses().FirstOrDefault(p => p.Id == pidInfo.Value.Pid);
                if (proc == null) return;
                if (!proc.HasExited)
                {
                    if (!ValidatePidProcess(proc, pidInfo.Value))
                    {
                        Trace.TraceWarning($"PID {pidInfo.Value.Pid} validation failed - refusing to kill.");
                        return;
                    }

                    Trace.WriteLine($"Killing existing MCP Gateway process PID {pidInfo.Value.Pid} before start.");
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(5000);
                }
            }
            catch
            {
                // ignore
            }
            finally
            {
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

        private struct PidFileInfo
        {
            public int Pid { get; set; }
            public string CommandLineHash { get; set; }
            public DateTime Timestamp { get; set; }
        }

        private static PidFileInfo? ReadPidFile(string path)
        {
            try
            {
                var text = File.ReadAllText(path).Trim();

                if (text.StartsWith("{", StringComparison.Ordinal))
                {
                    var json = JsonSerializer.Deserialize<PidFileInfo>(text);
                    return json;
                }

                if (int.TryParse(text, out var pid))
                {
                    return new PidFileInfo
                    {
                        Pid = pid,
                        CommandLineHash = string.Empty,
                        Timestamp = DateTime.MinValue
                    };
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        private static void WritePidFile(string path, int pid, string commandLine)
        {
            try
            {
                var info = new PidFileInfo
                {
                    Pid = pid,
                    CommandLineHash = ComputeCommandLineHash(commandLine),
                    Timestamp = DateTime.UtcNow
                };
                var json = JsonSerializer.Serialize(info);
                File.WriteAllText(path, json);
            }
            catch
            {
                File.WriteAllText(path, pid.ToString());
            }
        }

        private bool ValidatePidProcess(Process process, PidFileInfo pidInfo)
        {
            try
            {
                if (!IsExpectedGatewayProcess(process))
                {
                    Trace.TraceWarning($"PID {pidInfo.Pid} is not an expected MCP Gateway process.");
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(pidInfo.CommandLineHash))
                {
                    var cmdLine = GetProcessCommandLine(process);
                    if (cmdLine != null)
                    {
                        var currentHash = ComputeCommandLineHash(cmdLine);
                        if (currentHash != pidInfo.CommandLineHash)
                        {
                            Trace.TraceWarning($"PID {pidInfo.Pid} command line hash mismatch (PID reuse detected).");
                            return false;
                        }
                    }
                }

                if (pidInfo.Timestamp != DateTime.MinValue)
                {
                    var age = DateTime.UtcNow - pidInfo.Timestamp;
                    if (age.TotalSeconds > 60)
                    {
                        Trace.TraceWarning($"PID {pidInfo.Pid} file is stale ({age.TotalSeconds:F0}s old).");
                        return false;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"PID validation failed: {ex.Message}");
                return false;
            }
        }

        private bool IsExpectedGatewayProcess(Process process)
        {
            try
            {
                var exe = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(exe))
                {
                    return exe.StartsWith(_gatewayRoot, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                // Accessing MainModule can fail; do not kill if we cannot verify.
            }
            return false;
        }

        private static string ComputeCommandLineHash(string commandLine)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var bytes = System.Text.Encoding.UTF8.GetBytes(commandLine);
            var hash = sha.ComputeHash(bytes);
            return Convert.ToHexString(hash);
        }

        private static string? GetProcessCommandLine(Process process)
        {
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher(
                    $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {process.Id}");
                using var results = searcher.Get();
                foreach (System.Management.ManagementObject obj in results)
                {
                    return obj["CommandLine"]?.ToString();
                }
            }
            catch
            {
                try
                {
                    return process.MainModule?.FileName;
                }
                catch
                {
                    return null;
                }
            }
            return null;
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
                Trace.TraceWarning($"Failed to attach MCP Gateway to job object: {ex.Message}");
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
