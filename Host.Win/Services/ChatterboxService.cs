// File: Host.Win/Services/ChatterboxService.cs
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Host.Win.Models;

namespace Host.Win.Services
{
    /// <summary>
    /// Manages a long-running Chatterbox TTS process that keeps the model loaded in memory.
    /// Uses turbo mode with a reference audio for voice cloning.
    /// </summary>
    public sealed class ChatterboxService : IDisposable
    {
        private const string Prefix = "[TTS/chatterbox]";
        private static readonly TimeSpan ModelLoadTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan GenerationTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan WavPollInterval = TimeSpan.FromMilliseconds(100);

        private readonly object _lock = new();
        private Process? _process;
        private string? _outputPath;
        private bool _isReady;
        private bool _isDisposed;
        private HostSettings? _currentSettings;

        public bool IsRunning
        {
            get
            {
                lock (_lock)
                {
                    return _process != null && !_process.HasExited;
                }
            }
        }

        public bool IsReady
        {
            get
            {
                lock (_lock)
                {
                    return _isReady && IsRunning;
                }
            }
        }

        public async Task<bool> EnsureRunningAsync(HostSettings? settings, CancellationToken cancellationToken = default)
        {
            if (_isDisposed)
            {
                return false;
            }

            lock (_lock)
            {
                if (_isReady && _process != null && !_process.HasExited)
                {
                    return true;
                }
            }

            return await StartProcessAsync(settings, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Pre-warms the Chatterbox model by starting the process in the background.
        /// Call this at app startup to avoid delay on first TTS request.
        /// </summary>
        public void PreWarm(HostSettings? settings)
        {
            if (settings?.TtsEngine != "chatterbox")
            {
                return;
            }

            Task.Run(async () =>
            {
                try
                {
                    Trace.WriteLine($"{Prefix} pre-warming model");
                    var success = await EnsureRunningAsync(settings).ConfigureAwait(false);
                    if (success)
                    {
                        Trace.WriteLine($"{Prefix} model ready");
                    }
                    else
                    {
                        Trace.TraceWarning($"{Prefix} pre-warm failed");
                    }
                }
                catch (Exception ex)
                {
                    Trace.TraceError($"{Prefix} pre-warm error: {ex.Message}");
                }
            });
        }

        public async Task<byte[]?> GenerateAsync(string text, HostSettings? settings, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            if (!await EnsureRunningAsync(settings, cancellationToken).ConfigureAwait(false))
            {
                Trace.TraceWarning($"{Prefix} process not running; cannot generate");
                return null;
            }

            string outputPath;
            Process process;

            lock (_lock)
            {
                if (_process == null || _process.HasExited || string.IsNullOrEmpty(_outputPath))
                {
                    Trace.TraceWarning($"{Prefix} process not ready for generation");
                    return null;
                }

                process = _process;
                outputPath = _outputPath;
            }

            try
            {
                // Delete existing output file before generating
                if (File.Exists(outputPath))
                {
                    File.Delete(outputPath);
                }

                var startTime = DateTime.UtcNow;
                var fileInfo = new FileInfo(outputPath);

                // Send text to stdin
                await process.StandardInput.WriteLineAsync(text).ConfigureAwait(false);
                await process.StandardInput.FlushAsync().ConfigureAwait(false);

                if (settings?.VerboseLogging == true)
                {
                    Trace.WriteLine($"{Prefix} sent text preview: {text.Substring(0, Math.Min(50, text.Length))}...");
                }

                // Wait for WAV file to be created and stabilize
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(GenerationTimeout);

                long lastSize = -1;
                int stableCount = 0;
                const int requiredStableChecks = 3;

                while (!timeoutCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(WavPollInterval, timeoutCts.Token).ConfigureAwait(false);

                    fileInfo.Refresh();
                    if (!fileInfo.Exists)
                    {
                        continue;
                    }

                    var currentSize = fileInfo.Length;
                    if (currentSize > 0 && currentSize == lastSize)
                    {
                        stableCount++;
                        if (stableCount >= requiredStableChecks)
                        {
                            // File is stable, read it
                            var elapsed = DateTime.UtcNow - startTime;
                            if (settings?.VerboseLogging == true)
                            {
                                Trace.WriteLine($"{Prefix} generated audio in {elapsed.TotalSeconds:F2}s size={currentSize} bytes");
                            }

                            return await File.ReadAllBytesAsync(outputPath, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        stableCount = 0;
                        lastSize = currentSize;
                    }
                }

                Trace.TraceWarning($"{Prefix} generation timed out");
                return null;
            }
            catch (OperationCanceledException)
            {
                Trace.WriteLine($"{Prefix} generation cancelled");
                return null;
            }
            catch (Exception ex)
            {
                Trace.TraceError($"{Prefix} generation failed: {ex.Message}");
                return null;
            }
        }

        private async Task<bool> StartProcessAsync(HostSettings? settings, CancellationToken cancellationToken)
        {
            Shutdown();

            var pythonPath = ResolvePythonPath(settings);
            var workingDir = ResolveWorkingDir(settings);
            var refAudio = settings?.ChatterboxRefAudio ?? "female_ref.wav";

            if (string.IsNullOrWhiteSpace(pythonPath))
            {
                Trace.TraceError($"{Prefix} python path not configured");
                return false;
            }

            if (string.IsNullOrWhiteSpace(workingDir) || !Directory.Exists(workingDir))
            {
                Trace.TraceError($"{Prefix} working directory not found: {workingDir}");
                return false;
            }

            var appPy = Path.Combine(workingDir, "app.py");
            if (!File.Exists(appPy))
            {
                Trace.TraceError($"{Prefix} app.py not found: {workingDir}");
                return false;
            }

            var refAudioPath = Path.IsPathRooted(refAudio) ? refAudio : Path.Combine(workingDir, refAudio);
            if (!File.Exists(refAudioPath))
            {
                Trace.TraceError($"{Prefix} reference audio not found: {refAudioPath}");
                return false;
            }

            var outputPath = Path.Combine(Path.GetTempPath(), $"lisa_chatterbox_{Guid.NewGuid():N}.wav");

            var args = $"app.py --model turbo --audio-prompt \"{refAudioPath}\" --no-play --output \"{outputPath}\"";

            var startInfo = new ProcessStartInfo
            {
                FileName = pythonPath,
                Arguments = args,
                WorkingDirectory = workingDir,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            if (settings?.VerboseLogging == true)
            {
                Trace.WriteLine($"{Prefix} starting python={pythonPath}");
                Trace.WriteLine($"{Prefix} working_dir={workingDir}");
            }

            try
            {
                var process = new Process { StartInfo = startInfo };

                var readyTcs = new TaskCompletionSource<bool>();
                var stdoutBuffer = new StringBuilder();
                var stderrBuffer = new StringBuilder();

                process.OutputDataReceived += (sender, e) =>
                {
                    if (e.Data == null) return;

                    stdoutBuffer.AppendLine(e.Data);

                    // Check for ready signal - "Enter text" appears after model is loaded
                    if (e.Data.Contains("Enter text to synthesize"))
                    {
                        if (settings?.VerboseLogging == true)
                        {
                            Trace.WriteLine($"{Prefix} ready signal received");
                        }
                        readyTcs.TrySetResult(true);
                    }
                };

                process.ErrorDataReceived += (sender, e) =>
                {
                    if (e.Data == null) return;
                    stderrBuffer.AppendLine(e.Data);
                };

                // Handle process exit
                process.Exited += (sender, e) =>
                {
                    Trace.TraceWarning($"{Prefix} process exited unexpectedly");
                    readyTcs.TrySetResult(false);
                };
                process.EnableRaisingEvents = true;

                if (!process.Start())
                {
                    Trace.TraceError($"{Prefix} failed to start process");
                    return false;
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // Wait for model to load
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(ModelLoadTimeout);

                var readyTask = readyTcs.Task;
                var timeoutTask = Task.Delay(ModelLoadTimeout, timeoutCts.Token);

                var completedTask = await Task.WhenAny(readyTask, timeoutTask).ConfigureAwait(false);

                if (completedTask == timeoutTask)
                {
                    if (settings?.VerboseLogging == true)
                    {
                        TraceLogUtil.AppendVerboseDetail(
                            "TTS/chatterbox",
                            $"startup timeout\ncmd={pythonPath} {args}\nstdout:\n{stdoutBuffer}\n\nstderr:\n{stderrBuffer}");
                    }
                    Trace.TraceError($"{Prefix} startup timed out while waiting for ready signal");
                    try
                    {
                        process.Kill(true);
                    }
                    catch { }
                    process.Dispose();
                    return false;
                }

                // Check if process exited (result is false) or ready (result is true)
                var isReady = await readyTask.ConfigureAwait(false);
                if (!isReady)
                {
                    if (settings?.VerboseLogging == true)
                    {
                        TraceLogUtil.AppendVerboseDetail(
                            "TTS/chatterbox",
                            $"startup failed\ncmd={pythonPath} {args}\nstdout:\n{stdoutBuffer}\n\nstderr:\n{stderrBuffer}");
                    }
                    var failureSummary = TraceLogUtil.SummarizeText(stderrBuffer.Length > 0 ? stderrBuffer.ToString() : stdoutBuffer.ToString());
                    if (string.IsNullOrWhiteSpace(failureSummary))
                    {
                        failureSummary = "process exited before reporting readiness";
                    }
                    Trace.TraceError($"{Prefix} startup failed: {failureSummary}");
                    process.Dispose();
                    return false;
                }

                lock (_lock)
                {
                    _process = process;
                    _outputPath = outputPath;
                    _isReady = true;
                    _currentSettings = settings;
                }

                if (settings?.VerboseLogging == true && (stdoutBuffer.Length > 0 || stderrBuffer.Length > 0))
                {
                    TraceLogUtil.AppendVerboseDetail(
                        "TTS/chatterbox",
                        $"startup ready\ncmd={pythonPath} {args}\nstdout:\n{stdoutBuffer}\n\nstderr:\n{stderrBuffer}");
                }

                Trace.WriteLine($"{Prefix} process ready");
                return true;
            }
            catch (Exception ex)
            {
                Trace.TraceError($"{Prefix} failed to start: {ex.Message}");
                return false;
            }
        }

        private static string? ResolvePythonPath(HostSettings? settings)
        {
            // 1. Check explicit setting
            if (!string.IsNullOrWhiteSpace(settings?.ChatterboxPythonPath))
            {
                if (File.Exists(settings.ChatterboxPythonPath))
                {
                    return settings.ChatterboxPythonPath;
                }
            }

            // 2. Check Chatterbox venv relative to working dir
            var workingDir = ResolveWorkingDir(settings);
            if (!string.IsNullOrWhiteSpace(workingDir))
            {
                var venvPython = Path.Combine(workingDir, ".venv", "Scripts", "python.exe");
                if (File.Exists(venvPython))
                {
                    return venvPython;
                }
            }

            // 3. Check Chatterbox venv relative to app
            var appDir = AppContext.BaseDirectory;
            var chatterboxDir = Path.Combine(appDir, "..", "Chatterbox");
            if (Directory.Exists(chatterboxDir))
            {
                var venvPython = Path.Combine(chatterboxDir, ".venv", "Scripts", "python.exe");
                if (File.Exists(venvPython))
                {
                    return venvPython;
                }
            }

            return null;
        }

        private static string? ResolveWorkingDir(HostSettings? settings)
        {
            // 1. Check explicit setting
            if (!string.IsNullOrWhiteSpace(settings?.ChatterboxWorkingDir))
            {
                if (Directory.Exists(settings.ChatterboxWorkingDir))
                {
                    return settings.ChatterboxWorkingDir;
                }
            }

            // 2. Check relative to app
            var appDir = AppContext.BaseDirectory;
            var chatterboxDir = Path.Combine(appDir, "..", "Chatterbox");
            if (Directory.Exists(chatterboxDir))
            {
                return Path.GetFullPath(chatterboxDir);
            }

            // 3. Check common development path
            var devPath = Path.Combine(appDir, "..", "..", "..", "..", "Chatterbox");
            if (Directory.Exists(devPath))
            {
                return Path.GetFullPath(devPath);
            }

            return null;
        }

        public void Shutdown()
        {
            Process? process;

            lock (_lock)
            {
                process = _process;
                _process = null;
                _isReady = false;

                // Clean up output file
                if (!string.IsNullOrEmpty(_outputPath))
                {
                    try
                    {
                        if (File.Exists(_outputPath))
                        {
                            File.Delete(_outputPath);
                        }
                    }
                    catch { }
                    _outputPath = null;
                }
            }

            if (process == null)
            {
                return;
            }

            try
            {
                if (!process.HasExited)
                {
                    // Send blank line to trigger graceful exit
                    try
                    {
                        process.StandardInput.WriteLine();
                        process.StandardInput.Flush();
                    }
                    catch { }

                    // Wait briefly for graceful exit
                    if (!process.WaitForExit(2000))
                    {
                        process.Kill(true);
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"{Prefix} shutdown error: {ex.Message}");
            }
            finally
            {
                process.Dispose();
            }

            Trace.WriteLine($"{Prefix} process shut down");
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            Shutdown();
        }
    }
}
