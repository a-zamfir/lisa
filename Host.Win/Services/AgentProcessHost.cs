// File: Host.Win/Services/AgentProcessHost.cs
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

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
        private readonly string _speechRequirementsHashFile;
        private readonly string _callbackToken;
        private readonly int _callbackPort;
        private readonly string _mcpAuthToken;
        private readonly string _providerApiKey;
        private readonly bool _verboseLogging;
        private IntPtr _jobHandle = IntPtr.Zero;

        public AgentProcessHost(
            string agentPath,
            int port,
            string? callbackToken = null,
            int callbackPort = 0,
            string? mcpAuthToken = null,
            string? providerApiKey = null,
            bool verboseLogging = false)
        {
            _agentPath = agentPath;
            _port = port;
            _agentRoot = Path.GetDirectoryName(agentPath) ?? Environment.CurrentDirectory;
            _callbackToken = callbackToken ?? string.Empty;
            _callbackPort = callbackPort;
            _mcpAuthToken = mcpAuthToken ?? string.Empty;
            _providerApiKey = providerApiKey ?? string.Empty;
            _verboseLogging = verboseLogging;
            var localDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LISA");
            Directory.CreateDirectory(localDir);
            _pidFile = Path.Combine(localDir, "agent.pid");
            _requirementsHashFile = Path.Combine(localDir, "agent-requirements.sha256");
            _speechRequirementsHashFile = Path.Combine(localDir, "agent-requirements-speech.sha256");
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

            EnsureWhisperModelAvailable(pythonPath, workingDir);

            TryKillExistingPid();

            var uvicornLogLevel = _verboseLogging ? "info" : "warning";
            ProcessStartInfo CreatePsi(string fileName) => new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = $"-m uvicorn main:app --host 127.0.0.1 --port {_port} --log-level {uvicornLogLevel} --no-access-log",
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
            if (!string.IsNullOrWhiteSpace(_providerApiKey))
            {
                psi.Environment["PROVIDER_API_KEY"] = _providerApiKey;
            }
            psi.Environment["LISA_VERBOSE_LOGGING"] = _verboseLogging ? "1" : "0";
            psi.Environment["HF_HUB_DISABLE_SYMLINKS_WARNING"] = "1";
            psi.Environment["HF_HUB_OFFLINE"] = "1";
            psi.Environment["FASTER_WHISPER_MODEL"] = "small";
            psi.Environment["FASTER_WHISPER_MODEL_DIR"] = Path.Combine(workingDir, "speech", "models", "whisper-small");
            Trace.WriteLine($"Agent env: AGENT_PORT={_port} LISA_CALLBACK_TCP_PORT={_callbackPort} verbose={_verboseLogging}");

            try
            {
                _process = Process.Start(psi);
                if (_process == null)
                {
                    Trace.TraceWarning("Primary agent launch failed with 'python'. Trying 'py -3'.");
                    psi = CreatePsi("py");
                    psi.Arguments = $"-3 -m uvicorn main:app --host 127.0.0.1 --port {_port} --log-level {uvicornLogLevel} --no-access-log";
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
                    if (!string.IsNullOrWhiteSpace(_providerApiKey))
                    {
                        psi.Environment["PROVIDER_API_KEY"] = _providerApiKey;
                    }
                    psi.Environment["LISA_VERBOSE_LOGGING"] = _verboseLogging ? "1" : "0";
                    psi.Environment["HF_HUB_DISABLE_SYMLINKS_WARNING"] = "1";
                    psi.Environment["HF_HUB_OFFLINE"] = "1";
                    psi.Environment["FASTER_WHISPER_MODEL"] = "small";
                    psi.Environment["FASTER_WHISPER_MODEL_DIR"] = Path.Combine(workingDir, "speech", "models", "whisper-small");
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
                        var line = args.Data;
                        if (line.StartsWith("INFO:", StringComparison.OrdinalIgnoreCase))
                        {
                            Trace.WriteLine($"[Agent] {line}");
                        }
                        else if (line.StartsWith("DEBUG:", StringComparison.OrdinalIgnoreCase))
                        {
                            if (_verboseLogging)
                            {
                                Trace.WriteLine($"[Agent] {line}");
                            }
                        }
                        else if (line.StartsWith("WARNING:", StringComparison.OrdinalIgnoreCase))
                        {
                            Trace.TraceWarning($"[Agent] {line}");
                        }
                        else if (line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase) || line.StartsWith("CRITICAL:", StringComparison.OrdinalIgnoreCase))
                        {
                            Trace.TraceError($"[Agent ERR] {line}");
                        }
                        else
                        {
                            Trace.TraceWarning($"[Agent] {line}");
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
                    Trace.TraceWarning($"Failed to write agent pid file: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Trace.TraceError($"Failed to start agent process: {ex.Message}");
            }
        }

        private void EnsureWhisperModelAvailable(string pythonExe, string workingDir)
        {
            try
            {
                var modelDir = Path.Combine(workingDir, "speech", "models", "whisper-small");
                if (Directory.Exists(modelDir))
                {
                    var hasBin = Directory.EnumerateFiles(modelDir, "*.bin", SearchOption.AllDirectories).Any();
                    if (hasBin)
                    {
                        return;
                    }
                }

                var downloadScript = Path.Combine(workingDir, "speech", "download_model.py");
                if (!File.Exists(downloadScript))
                {
                    Trace.TraceWarning($"Whisper model download script not found: {downloadScript}");
                    return;
                }

                // Only attempt download when faster-whisper is available; speech deps are optional.
                if (!RunSilently(pythonExe, "-c \"import faster_whisper\"", workingDir, timeoutMs: 20000, verboseLogging: _verboseLogging))
                {
                    Trace.WriteLine("faster-whisper not installed; skipping Whisper model download.");
                    return;
                }

                Directory.CreateDirectory(modelDir);

                Trace.WriteLine($"Whisper model missing; attempting download to: {modelDir}");
                var ok = RunSilently(
                    pythonExe,
                    $"-u \"{downloadScript}\" --model small --output \"{modelDir}\"",
                    workingDir,
                    timeoutMs: 600000,
                    verboseLogging: _verboseLogging,
                    configure: psi =>
                    {
                        // Allow downloads for this one-shot bootstrap even if the agent runs offline.
                        psi.Environment["HF_HUB_OFFLINE"] = "0";
                        psi.Environment["HF_HUB_DISABLE_SYMLINKS_WARNING"] = "1";
                    });

                if (!ok)
                {
                    Trace.TraceWarning("Whisper model download failed; Talk mode STT may be unavailable until the model is downloaded.");
                }
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Whisper model bootstrap failed: {ex.Message}");
            }
        }

        private string? EnsureVenv(string workingDir)
        {
            var venvPath = Path.Combine(workingDir, ".venv");
            var pythonExe = Path.Combine(venvPath, "Scripts", "python.exe");
            var requirementsLock = Path.Combine(workingDir, "requirements-lock.txt");
            var requirements = File.Exists(requirementsLock) ? requirementsLock : Path.Combine(workingDir, "requirements.txt");
            var speechRequirements = Path.Combine(workingDir, "requirements-speech.txt");

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
                var created = false;
                try
                {
                    if (_verboseLogging)
                    {
                        Trace.WriteLine($"Agent venv: Attempting with 'python' command...");
                    }
                    var createProc = Process.Start(createPsi);
                    createProc?.WaitForExit(15000);
                    if (createProc != null && createProc.ExitCode == 0)
                    {
                        created = true;
                        if (_verboseLogging)
                        {
                            Trace.WriteLine("Agent venv: Created successfully with 'python' command.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (_verboseLogging)
                    {
                        Trace.WriteLine($"Agent venv: 'python' command failed ({ex.Message}), trying 'py -3' fallback...");
                    }
                }

                if (!created)
                {
                    // Try py -3 fallback
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
                                Trace.WriteLine("Agent venv: Created successfully with 'py -3' command.");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Trace.TraceError($"Agent venv creation failed: {ex.Message}");
                    }
                }

                if (!created && _verboseLogging)
                {
                    Trace.TraceWarning("Agent venv: Creation may have failed. Checking for python.exe...");
                }
            }
            else if (_verboseLogging)
            {
                Trace.WriteLine($"Agent venv: Already exists at {venvPath}");
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
                    if (_verboseLogging)
                    {
                        Trace.WriteLine("Agent bootstrap: Upgrading pip...");
                    }
                    RunSilently(pythonExe, "-m pip install --upgrade pip", workingDir, verboseLogging: _verboseLogging);
                    if (_verboseLogging)
                    {
                        var reqFile = Path.GetFileName(requirements);
                        Trace.WriteLine($"Agent bootstrap: Installing {reqFile}...");
                    }
                    if (RunSilently(pythonExe, $"-m pip install -r {Path.GetFileName(requirements)}", workingDir, verboseLogging: _verboseLogging))
                    {
                        WriteHash(_requirementsHashFile, hash);
                        Trace.WriteLine("Agent requirements installed successfully.");
                    }
                    else
                    {
                        Trace.TraceError("Agent requirements installation failed. Check pip output above.");
                    }
                }
                else if (_verboseLogging)
                {
                    Trace.WriteLine("Agent requirements: Hash unchanged, skipping installation.");
                }
            }
            else if (_verboseLogging)
            {
                Trace.WriteLine($"Agent requirements.txt not found at {requirements}");
            }

            if (File.Exists(speechRequirements))
            {
                var hash = ComputeFileHash(speechRequirements);
                var existingHash = ReadHash(_speechRequirementsHashFile);
                Trace.WriteLine($"Agent speech requirements hash: {hash} (stored={existingHash ?? "none"})");
                if (!string.Equals(hash, existingHash, StringComparison.OrdinalIgnoreCase))
                {
                    Trace.WriteLine("Installing optional agent speech requirements...");
                    if (_verboseLogging)
                    {
                        Trace.WriteLine("Agent bootstrap: Installing requirements-speech.txt (prefer binary wheels)...");
                    }
                    // Prefer wheels to avoid long native builds on fresh machines.
                    if (RunSilently(pythonExe, "-m pip install --prefer-binary -r requirements-speech.txt", workingDir, verboseLogging: _verboseLogging))
                    {
                        WriteHash(_speechRequirementsHashFile, hash);
                        Trace.WriteLine("Agent speech requirements installed successfully.");
                    }
                    else
                    {
                        Trace.TraceWarning("Optional speech requirements failed to install; STT endpoints will run in fallback mode.");
                    }
                }
                else if (_verboseLogging)
                {
                    Trace.WriteLine("Agent speech requirements: Hash unchanged, skipping installation.");
                }
            }
            else if (_verboseLogging)
            {
                Trace.WriteLine($"Agent requirements-speech.txt not found at {speechRequirements}");
            }

            return pythonExe;
        }

        private static bool RunSilently(
            string fileName,
            string arguments,
            string workingDir,
            int timeoutMs = 20000,
            bool verboseLogging = false,
            Action<ProcessStartInfo>? configure = null)
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
                configure?.Invoke(psi);
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    var stdout = proc.StandardOutput.ReadToEnd();
                    var stderr = proc.StandardError.ReadToEnd();
                    proc.WaitForExit(timeoutMs);
                    var ok = proc.ExitCode == 0;
                    if ((!string.IsNullOrWhiteSpace(stdout)) && (verboseLogging || !ok))
                    {
                        Trace.WriteLine($"[Agent cmd] {fileName} {arguments} -> {stdout}");
                    }
                    if ((!string.IsNullOrWhiteSpace(stderr)) && (verboseLogging || !ok))
                    {
                        Trace.TraceWarning($"[Agent cmd ERR] {fileName} {arguments} -> {stderr}");
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

                var pidInfo = ReadPidFile(_pidFile);
                if (pidInfo == null) return;

                using var proc = Process.GetProcesses().FirstOrDefault(p => p.Id == pidInfo.Value.Pid);
                if (proc == null) return;
                if (!proc.HasExited)
                {
                    // Validate this is the expected process before killing
                    if (!ValidatePidProcess(proc, pidInfo.Value))
                    {
                        Trace.TraceWarning($"PID {pidInfo.Value.Pid} validation failed - refusing to kill.");
                        return;
                    }

                    Trace.WriteLine($"Killing existing agent process PID {pidInfo.Value.Pid} before start.");
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

                // Try new JSON format first
                if (text.StartsWith("{", StringComparison.Ordinal))
                {
                    var json = JsonSerializer.Deserialize<PidFileInfo>(text);
                    return json;
                }

                // Fall back to legacy plain PID format
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
                // Fall back to legacy format
                File.WriteAllText(path, pid.ToString());
            }
        }

        private bool ValidatePidProcess(Process process, PidFileInfo pidInfo)
        {
            try
            {
                // Check 1: Is this an expected agent process (existing validation)
                if (!IsExpectedAgentProcess(process))
                {
                    Trace.TraceWarning($"PID {pidInfo.Pid} is not an expected agent process.");
                    return false;
                }

                // Check 2: If we have a command line hash, validate it matches
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

                // Check 3: If timestamp is available, ensure it's recent (< 60 seconds old)
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
                // Fall back to filename if WMI fails
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
