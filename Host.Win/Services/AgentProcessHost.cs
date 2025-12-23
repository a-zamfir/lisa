// File: Host.Win/Services/AgentProcessHost.cs
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Host.Win.Services
{
    /// <summary>
    /// Launches and stops the local Python FastAPI agent.
    /// </summary>
    public sealed class AgentProcessHost : IDisposable
    {
        private Process? _process;
        private readonly string _agentPath;
        private readonly int _port;
        private readonly string _agentRoot;
        private readonly string _pidFile;
        private readonly string _requirementsHashFile;
        private readonly string _callbackToken;
        private readonly int _callbackPort;
        private readonly string _mcpAuthToken;
        private IntPtr _jobHandle = IntPtr.Zero;

        public AgentProcessHost(
            string agentPath,
            int port,
            string? callbackToken = null,
            int callbackPort = 0,
            string? mcpAuthToken = null)
        {
            _agentPath = agentPath;
            _port = port;
            _agentRoot = Path.GetDirectoryName(agentPath) ?? Environment.CurrentDirectory;
            _callbackToken = callbackToken ?? string.Empty;
            _callbackPort = callbackPort;
            _mcpAuthToken = mcpAuthToken ?? string.Empty;
            var localDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LISA");
            Directory.CreateDirectory(localDir);
            _pidFile = Path.Combine(localDir, "agent.pid");
            _requirementsHashFile = Path.Combine(localDir, "agent-requirements.sha256");
        }

        public void Start()
        {
            if (_process != null && !_process.HasExited) return;
            if (!File.Exists(_agentPath))
            {
                Trace.TraceWarning($"Agent path not found: {_agentPath}");
                return;
            }

            var workingDir = Path.GetDirectoryName(_agentPath) ?? Environment.CurrentDirectory;
            Trace.WriteLine($"Agent working dir resolved: {workingDir}");
            var pythonPath = EnsureVenv(workingDir);
            if (pythonPath == null)
            {
                Trace.TraceError("Unable to create or locate Python interpreter for agent.");
                return;
            }
            Trace.WriteLine($"Agent python resolved: {pythonPath}");

            TryKillExistingPid();

            ProcessStartInfo CreatePsi(string fileName) => new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = $"-m uvicorn main:app --host 127.0.0.1 --port {_port} --log-level warning --no-access-log",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDir,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            var psi = CreatePsi("python");
            psi.FileName = pythonPath;
            psi.Environment["AGENT_PORT"] = _port.ToString();
            psi.Environment["LISA_SETTINGS_PATH"] = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LISA",
                "host-settings.json");
            psi.Environment["PYTHONPATH"] = workingDir;
            if (!string.IsNullOrWhiteSpace(_callbackToken))
            {
                psi.Environment["LISA_CALLBACK_TOKEN"] = _callbackToken;
            }
            if (_callbackPort > 0)
            {
                psi.Environment["LISA_CALLBACK_TCP_PORT"] = _callbackPort.ToString();
            }
            if (!string.IsNullOrWhiteSpace(_mcpAuthToken))
            {
                psi.Environment["MCP_AUTH_TOKEN"] = _mcpAuthToken;
            }
            psi.Environment["HF_HUB_DISABLE_SYMLINKS_WARNING"] = "1";
            psi.Environment["HF_HUB_OFFLINE"] = "1";
            psi.Environment["FASTER_WHISPER_MODEL_DIR"] = Path.Combine(workingDir, "speech", "models", "whisper-small");
            Trace.WriteLine($"Agent env: AGENT_PORT={_port} LISA_CALLBACK_TCP_PORT={_callbackPort}");

            try
            {
                _process = Process.Start(psi);
                if (_process == null)
                {
                    Trace.TraceWarning("Primary agent launch failed with 'python'. Trying 'py -3'.");
                    psi = CreatePsi("py");
                    psi.Arguments = $"-3 -m uvicorn main:app --host 127.0.0.1 --port {_port} --log-level warning --no-access-log";
                    psi.Environment["AGENT_PORT"] = _port.ToString();
                    psi.Environment["LISA_SETTINGS_PATH"] = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "LISA",
                        "host-settings.json");
                    psi.Environment["PYTHONPATH"] = workingDir;
                    if (!string.IsNullOrWhiteSpace(_callbackToken))
                    {
                        psi.Environment["LISA_CALLBACK_TOKEN"] = _callbackToken;
                    }
                    if (_callbackPort > 0)
                    {
                        psi.Environment["LISA_CALLBACK_TCP_PORT"] = _callbackPort.ToString();
                    }
                    if (!string.IsNullOrWhiteSpace(_mcpAuthToken))
                    {
                        psi.Environment["MCP_AUTH_TOKEN"] = _mcpAuthToken;
                    }
                    _process = Process.Start(psi);
                }

                if (_process == null)
                {
                    Trace.TraceError("Failed to start agent process (python/py not found?).");
                    return;
                }

                Trace.WriteLine($"Agent process started on port {_port} (PID {_process.Id}). WorkingDir={workingDir}");
                AttachToJob(_process);
                _process.OutputDataReceived += (_, args) =>
                {
                    if (!string.IsNullOrWhiteSpace(args.Data))
                        Trace.WriteLine($"[Agent] {args.Data}");
                };
                _process.ErrorDataReceived += (_, args) =>
                {
                    if (!string.IsNullOrWhiteSpace(args.Data))
                    {
                        // Uvicorn logs to stderr; treat INFO as normal noise.
                        var line = args.Data;
                        if (line.StartsWith("INFO:", StringComparison.OrdinalIgnoreCase))
                        {
                            Trace.WriteLine($"[Agent] {line}");
                        }
                        else
                        {
                            Trace.TraceError($"[Agent ERR] {line}");
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
                    Trace.TraceWarning($"Failed to write agent pid file: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Trace.TraceError($"Failed to start agent process: {ex.Message}");
            }
        }

        private string? EnsureVenv(string workingDir)
        {
            var venvPath = Path.Combine(workingDir, ".venv");
            var pythonExe = Path.Combine(venvPath, "Scripts", "python.exe");
            var requirements = Path.Combine(workingDir, "requirements.txt");

            if (!Directory.Exists(venvPath) || !File.Exists(pythonExe))
            {
                // Dev-only bootstrapping; production should ship a packaged runtime.
                Trace.WriteLine("Creating agent virtual environment...");
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
                    // Try py -3 fallback
                    createPsi.FileName = "py";
                    createPsi.Arguments = "-3 -m venv .venv";
                    try
                    {
                        var createProc = Process.Start(createPsi);
                        createProc?.WaitForExit(15000);
                    }
                    catch (Exception ex)
                    {
                        Trace.TraceError($"Venv creation failed: {ex.Message}");
                    }
                }
            }

            if (!File.Exists(pythonExe))
            {
                Trace.TraceError("Venv creation failed; python.exe missing.");
                return null;
            }

            if (File.Exists(requirements))
            {
                var hash = ComputeFileHash(requirements);
                var existingHash = ReadHash(_requirementsHashFile);
                Trace.WriteLine($"Agent requirements hash: {hash} (stored={existingHash ?? "none"})");
                if (!string.Equals(hash, existingHash, StringComparison.OrdinalIgnoreCase))
                {
                    Trace.WriteLine("Installing agent requirements...");
                    RunSilently(pythonExe, "-m pip install --upgrade pip", workingDir);
                    if (RunSilently(pythonExe, "-m pip install -r requirements.txt", workingDir))
                    {
                        WriteHash(_requirementsHashFile, hash);
                    }
                }
            }

            return pythonExe;
        }

        private static bool RunSilently(string fileName, string arguments, string workingDir)
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
                    if (!string.IsNullOrWhiteSpace(stdout))
                    {
                        Trace.WriteLine($"[Agent cmd] {fileName} {arguments} -> {stdout}");
                    }
                    if (!string.IsNullOrWhiteSpace(stderr))
                    {
                        Trace.TraceWarning($"[Agent cmd ERR] {fileName} {arguments} -> {stderr}");
                    }
                    return proc.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                Trace.TraceError($"Command failed: {fileName} {arguments} ({ex.Message})");
            }
            return false;
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
                Trace.TraceError($"Failed to stop agent process: {ex.Message}");
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

        private void TryKillExistingPid()
        {
            try
            {
                if (!File.Exists(_pidFile)) return;
                var text = File.ReadAllText(_pidFile).Trim();
                if (!int.TryParse(text, out var pid)) return;
                var proc = Process.GetProcessById(pid);
                if (!proc.HasExited)
                {
                    if (IsExpectedAgentProcess(proc))
                    {
                        Trace.WriteLine($"Killing existing agent process PID {pid} before start.");
                        proc.Kill(entireProcessTree: true);
                        proc.WaitForExit(5000);
                    }
                    else
                    {
                        Trace.TraceWarning($"Refusing to kill PID {pid} (unexpected process).");
                    }
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

        private bool IsExpectedAgentProcess(Process process)
        {
            try
            {
                var exe = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(exe))
                {
                    return exe.StartsWith(_agentRoot, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                // Accessing MainModule can fail; do not kill if we cannot verify.
            }
            return false;
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
                Trace.TraceWarning($"Failed to attach agent to job object: {ex.Message}");
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
