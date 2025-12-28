// File: Host.Win/Services/TtsService.cs
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Host.Win.Models;

namespace Host.Win.Services
{
    public sealed class TtsService
    {
        private static readonly TimeSpan PiperTimeout = TimeSpan.FromSeconds(10);
        private const int MaxErrorPreviewChars = 400;

        public async Task<TtsResult> GenerateAsync(string text, HostSettings? settings, CancellationToken cancellationToken)
        {
            text = SanitizeText(text);
            if (string.IsNullOrWhiteSpace(text))
            {
                return TtsResult.Failed("No text to speak.");
            }

            var command = ResolvePiperCommand(settings);
            var modelPath = ResolveVoiceModelPath(settings);
            var configPath = ResolveVoiceConfigPath(settings, modelPath);
            var verbose = settings?.VerboseLogging == true;

            if (string.IsNullOrWhiteSpace(command.FileName))
            {
                var details = verbose
                    ? $"piperMode={settings?.PiperMode ?? "exe"} piperExePath={settings?.PiperExePath ?? ""} piperPythonPath={settings?.PiperPythonPath ?? ""}"
                    : null;
                return await FallbackToSapiAsync(text, settings, cancellationToken, "Piper executable not found.", details).ConfigureAwait(false);
            }

            if (command.RequiresExistingFile && !File.Exists(command.FileName))
            {
                var details = verbose ? $"expectedPiperPath={command.FileName}" : null;
                return await FallbackToSapiAsync(text, settings, cancellationToken, "Piper executable not found.", details).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
            {
                var details = verbose ? $"piperVoiceModelPath={settings?.PiperVoiceModelPath ?? ""} resolvedModelPath={modelPath}" : null;
                return await FallbackToSapiAsync(text, settings, cancellationToken, "Piper voice model not found.", details).ConfigureAwait(false);
            }

            var outputPath = Path.Combine(Path.GetTempPath(), $"lisa_tts_{Guid.NewGuid():N}.wav");
            try
            {
                var coreArgs = $"--model \"{modelPath}\" --output_file \"{outputPath}\"";
                if (!string.IsNullOrWhiteSpace(configPath) && File.Exists(configPath))
                {
                    coreArgs += $" --config \"{configPath}\"";
                }

                if (settings?.PiperSpeakerId is int speakerId && speakerId >= 0)
                {
                    coreArgs += $" --speaker {speakerId}";
                }

                async Task<(bool Success, byte[]? AudioBytes, string Reason, string? VerboseDetails)> TryRunPiperAsync(
                    PiperCommand piperCommand,
                    string piperArgs,
                    string? attemptLabel)
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = piperCommand.FileName,
                        Arguments = piperArgs,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    if (piperCommand.RequiresExistingFile)
                    {
                        var wd = Path.GetDirectoryName(piperCommand.FileName);
                        if (!string.IsNullOrWhiteSpace(wd))
                        {
                            startInfo.WorkingDirectory = wd;
                        }
                    }

                    if (verbose)
                    {
                        var label = string.IsNullOrWhiteSpace(attemptLabel) ? "Piper" : attemptLabel;
                        Trace.WriteLine($"[TTS] {label} cmd: {startInfo.FileName} {startInfo.Arguments}");
                        if (!string.IsNullOrWhiteSpace(startInfo.WorkingDirectory))
                        {
                            Trace.WriteLine($"[TTS] {label} cwd: {startInfo.WorkingDirectory}");
                        }
                        Trace.WriteLine($"[TTS] Piper model: {modelPath}");
                        if (!string.IsNullOrWhiteSpace(configPath))
                        {
                            Trace.WriteLine($"[TTS] Piper config: {configPath}");
                        }
                        Trace.WriteLine($"[TTS] Piper output: {outputPath}");
                    }

                    using var process = new Process { StartInfo = startInfo };
                    if (!process.Start())
                    {
                        var details = verbose ? $"cmd={startInfo.FileName} {startInfo.Arguments}" : null;
                        return (false, null, "Failed to start Piper.", details);
                    }

                    var stdoutTask = process.StandardOutput.ReadToEndAsync();
                    var stderrTask = process.StandardError.ReadToEndAsync();

                    await process.StandardInput.WriteAsync(text + Environment.NewLine).ConfigureAwait(false);
                    await process.StandardInput.FlushAsync().ConfigureAwait(false);
                    process.StandardInput.Close();

                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutCts.CancelAfter(PiperTimeout);
                    try
                    {
                        await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        if (!process.HasExited)
                        {
                            try
                            {
                                process.Kill(true);
                            }
                            catch
                            {
                                // ignore kill errors
                            }
                        }

                        if (cancellationToken.IsCancellationRequested)
                        {
                            return (false, null, "TTS cancelled.", null);
                        }

                        return (false, null, "TTS timed out.", null);
                    }

                    var stderr = (await stderrTask.ConfigureAwait(false)).Trim();
                    var stdout = (await stdoutTask.ConfigureAwait(false)).Trim();

                    if (process.ExitCode != 0)
                    {
                        var reason = $"Piper exited with code {process.ExitCode}.";
                        if (!string.IsNullOrWhiteSpace(stderr))
                        {
                            reason += $" stderr: {TruncateForLog(stderr, MaxErrorPreviewChars)}";
                        }
                        else if (!string.IsNullOrWhiteSpace(stdout))
                        {
                            reason += $" stdout: {TruncateForLog(stdout, MaxErrorPreviewChars)}";
                        }

                        string? verboseDetails = null;
                        if (verbose)
                        {
                            verboseDetails =
                                $"cmd={startInfo.FileName} {startInfo.Arguments}\n" +
                                $"cwd={(string.IsNullOrWhiteSpace(startInfo.WorkingDirectory) ? "(default)" : startInfo.WorkingDirectory)}\n" +
                                $"exit_code={process.ExitCode}\n" +
                                $"stdout={(string.IsNullOrWhiteSpace(stdout) ? "(empty)" : stdout)}\n" +
                                $"stderr={(string.IsNullOrWhiteSpace(stderr) ? "(empty)" : stderr)}";
                        }

                        return (false, null, reason, verboseDetails);
                    }

                    if (verbose)
                    {
                        if (!string.IsNullOrWhiteSpace(stdout))
                        {
                            Trace.WriteLine($"[TTS] Piper stdout: {stdout}");
                        }
                        if (!string.IsNullOrWhiteSpace(stderr))
                        {
                            Trace.TraceWarning($"[TTS] Piper stderr: {stderr}");
                        }
                    }

                    if (!File.Exists(outputPath))
                    {
                        var reason = $"Piper output missing: {outputPath}";
                        var details = verbose ? $"cmd={startInfo.FileName} {startInfo.Arguments}" : null;
                        return (false, null, reason, details);
                    }

                    var bytes = await File.ReadAllBytesAsync(outputPath, cancellationToken).ConfigureAwait(false);
                    return (true, bytes, string.Empty, null);
                }

                static string BuildArgs(PiperCommand piperCommand, string argsCore)
                {
                    if (string.IsNullOrWhiteSpace(piperCommand.ArgumentsPrefix))
                    {
                        return argsCore.Trim();
                    }

                    return $"{piperCommand.ArgumentsPrefix} {argsCore}".Trim();
                }

                var primary = await TryRunPiperAsync(command, BuildArgs(command, coreArgs), attemptLabel: "Piper(primary)").ConfigureAwait(false);
                if (primary.Success && primary.AudioBytes != null)
                {
                    return TtsResult.FromAudio(primary.AudioBytes);
                }

                // Some Windows venv installs ship a broken `piper.exe` console shim. If the shim fails, retry via the venv python.
                if (string.Equals(settings?.PiperMode, "exe", StringComparison.OrdinalIgnoreCase)
                    && command.RequiresExistingFile
                    && IsPiperCommand(command.FileName))
                {
                    try
                    {
                        if (verbose)
                        {
                            Trace.TraceWarning($"[TTS] Piper(primary) failed; retrying with python fallback. reason={primary.Reason}");
                        }

                        var scriptsDir = Path.GetDirectoryName(command.FileName);
                        if (!string.IsNullOrWhiteSpace(scriptsDir))
                        {
                            var venvPython = Path.Combine(scriptsDir, "python.exe");
                            if (File.Exists(venvPython))
                            {
                                if (verbose)
                                {
                                    Trace.WriteLine($"[TTS] Retrying Piper via venv python: {venvPython} -m piper");
                                }

                                TryDelete(outputPath);
                                var pythonCommand = new PiperCommand(venvPython, "-m piper", requiresExistingFile: true);
                                var pythonAttempt = await TryRunPiperAsync(
                                        pythonCommand,
                                        BuildArgs(pythonCommand, coreArgs),
                                        attemptLabel: "Piper(python-fallback)")
                                    .ConfigureAwait(false);

                                if (pythonAttempt.Success && pythonAttempt.AudioBytes != null)
                                {
                                    if (verbose)
                                    {
                                        Trace.WriteLine("[TTS] Piper(python-fallback) succeeded.");
                                    }
                                    return TtsResult.FromAudio(pythonAttempt.AudioBytes);
                                }

                                var combinedReason = $"{primary.Reason} (python fallback: {pythonAttempt.Reason})";
                                var combinedDetails = verbose
                                    ? $"primary:\n{primary.VerboseDetails ?? "(no details)"}\n\npython:\n{pythonAttempt.VerboseDetails ?? "(no details)"}"
                                    : null;

                                return await FallbackToSapiAsync(text, settings, cancellationToken, combinedReason, combinedDetails).ConfigureAwait(false);
                            }
                        }
                    }
                    catch
                    {
                        // ignore and keep the primary result
                    }
                }

                if (string.Equals(primary.Reason, "TTS cancelled.", StringComparison.Ordinal))
                {
                    return TtsResult.Failed(primary.Reason);
                }

                if (string.Equals(primary.Reason, "TTS timed out.", StringComparison.Ordinal))
                {
                    return TtsResult.Failed(primary.Reason);
                }

                return await FallbackToSapiAsync(text, settings, cancellationToken, primary.Reason, primary.VerboseDetails).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var details = verbose ? $"cmd={command.FileName} model={modelPath} mode={settings?.PiperMode ?? "exe"}" : null;
                return await FallbackToSapiAsync(text, settings, cancellationToken, $"Piper failed: {ex.Message}", details).ConfigureAwait(false);
            }
            finally
            {
                TryDelete(outputPath);
            }
        }

        public async Task<bool> SpeakWithSapiAsync(string text, HostSettings? settings, CancellationToken cancellationToken)
        {
            text = SanitizeText(text);
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }
            return await TrySpeakWithSapiAsync(text, settings, cancellationToken).ConfigureAwait(false);
        }

        private static PiperCommand ResolvePiperCommand(HostSettings? settings)
        {
            var mode = settings?.PiperMode?.Trim().ToLowerInvariant();
            if (string.Equals(mode, "python", StringComparison.OrdinalIgnoreCase))
            {
                var pythonPath = string.IsNullOrWhiteSpace(settings?.PiperPythonPath) ? "python" : settings.PiperPythonPath;

                // SECURITY: Block network paths for user-configured executables
                if (!string.IsNullOrWhiteSpace(settings?.PiperPythonPath) && IsNetworkPath(settings.PiperPythonPath))
                {
                    Trace.TraceWarning($"[TTS] Blocked network path for PiperPythonPath: {settings.PiperPythonPath}");
                    return new PiperCommand(string.Empty, string.Empty, requiresExistingFile: false);
                }

                if (IsPiperCommand(pythonPath))
                {
                    return new PiperCommand(pythonPath, string.Empty, requiresExistingFile: false);
                }
                return new PiperCommand(pythonPath, "-m piper", requiresExistingFile: false);
            }

            if (!string.IsNullOrWhiteSpace(settings?.PiperExePath))
            {
                // SECURITY: Block network paths for user-configured executables
                if (IsNetworkPath(settings.PiperExePath))
                {
                    Trace.TraceWarning($"[TTS] Blocked network path for PiperExePath: {settings.PiperExePath}");
                    return new PiperCommand(string.Empty, string.Empty, requiresExistingFile: false);
                }

                return new PiperCommand(settings.PiperExePath, string.Empty, requiresExistingFile: true);
            }

            var bundled = Path.Combine(AppContext.BaseDirectory, "piper", "piper.exe");
            return new PiperCommand(bundled, string.Empty, requiresExistingFile: true);
        }

        private static string ResolveVoiceModelPath(HostSettings? settings)
        {
            if (!string.IsNullOrWhiteSpace(settings?.PiperVoiceModelPath))
            {
                // SECURITY: Block network paths for model files
                if (IsNetworkPath(settings.PiperVoiceModelPath))
                {
                    Trace.TraceWarning($"[TTS] Blocked network path for PiperVoiceModelPath: {settings.PiperVoiceModelPath}");
                    return string.Empty;
                }

                return settings.PiperVoiceModelPath;
            }

            return string.Empty;
        }

        private static string ResolveVoiceConfigPath(HostSettings? settings, string modelPath)
        {
            if (!string.IsNullOrWhiteSpace(settings?.PiperVoiceConfigPath))
            {
                // SECURITY: Block network paths for config files
                if (IsNetworkPath(settings.PiperVoiceConfigPath))
                {
                    Trace.TraceWarning($"[TTS] Blocked network path for PiperVoiceConfigPath: {settings.PiperVoiceConfigPath}");
                    return string.Empty;
                }

                return settings.PiperVoiceConfigPath;
            }

            if (!string.IsNullOrWhiteSpace(modelPath))
            {
                var defaultConfig = Path.ChangeExtension(modelPath, ".json");
                return defaultConfig ?? string.Empty;
            }

            return string.Empty;
        }

        private static async Task<TtsResult> FallbackToSapiAsync(
            string text,
            HostSettings? settings,
            CancellationToken cancellationToken,
            string reason,
            string? verboseDetails = null)
        {
            Trace.TraceWarning($"TTS fallback to SAPI: {reason}");
            if (settings?.VerboseLogging == true && !string.IsNullOrWhiteSpace(verboseDetails))
            {
                Trace.WriteLine($"[TTS] Piper details:\n{verboseDetails}");
            }
            var success = await TrySpeakWithSapiAsync(text, settings, cancellationToken).ConfigureAwait(false);
            if (!success)
            {
                return TtsResult.Failed(reason);
            }
            return TtsResult.UsedSapi();
        }

        private static string TruncateForLog(string value, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            value = value.Trim();
            if (value.Length <= maxChars)
            {
                return value;
            }

            return value.Substring(0, maxChars) + "…";
        }

        private static async Task<bool> TrySpeakWithSapiAsync(string text, HostSettings? settings, CancellationToken cancellationToken)
        {
            var synthType = Type.GetType("System.Speech.Synthesis.SpeechSynthesizer, System.Speech");
            if (synthType != null)
            {
                return await Task.Run(() =>
                {
                    object? synth = null;
                    try
                    {
                        synth = Activator.CreateInstance(synthType);
                        if (synth == null)
                        {
                            return false;
                        }

                        var rate = ConvertToSapiRate(settings?.VoiceRate ?? 1.0);
                        var volume = ConvertToSapiVolume(settings?.VoiceVolume ?? 1.0);

                        synthType.GetProperty("Rate")?.SetValue(synth, rate);
                        synthType.GetProperty("Volume")?.SetValue(synth, volume);
                        synthType.GetMethod("SetOutputToDefaultAudioDevice")?.Invoke(synth, null);

                        if (cancellationToken.IsCancellationRequested)
                        {
                            return false;
                        }

                        synthType.GetMethod("Speak")?.Invoke(synth, new object[] { text });
                        return true;
                    }
                    catch (Exception ex)
                    {
                        Trace.TraceWarning($"SAPI TTS failed (System.Speech): {ex.Message}");
                        return false;
                    }
                    finally
                    {
                        if (synth != null)
                        {
                            synthType.GetMethod("Dispose")?.Invoke(synth, null);
                        }
                    }
                }, cancellationToken).ConfigureAwait(false);
            }

            return await Task.Run(() =>
            {
                Type? comType = null;
                object? voice = null;
                try
                {
                    comType = Type.GetTypeFromProgID("SAPI.SpVoice");
                    if (comType == null)
                    {
                        Trace.TraceWarning("SAPI not available on this system.");
                        return false;
                    }

                    voice = Activator.CreateInstance(comType);
                    if (voice == null)
                    {
                        return false;
                    }

                    var rate = ConvertToSapiRate(settings?.VoiceRate ?? 1.0);
                    var volume = ConvertToSapiVolume(settings?.VoiceVolume ?? 1.0);

                    comType.InvokeMember("Rate", BindingFlags.SetProperty, null, voice, new object[] { rate });
                    comType.InvokeMember("Volume", BindingFlags.SetProperty, null, voice, new object[] { volume });

                    if (cancellationToken.IsCancellationRequested)
                    {
                        return false;
                    }

                    comType.InvokeMember("Speak", BindingFlags.InvokeMethod, null, voice, new object[] { text, 0 });
                    return true;
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning($"SAPI TTS failed (COM): {ex.Message}");
                    return false;
                }
                finally
                {
                    if (voice != null && Marshal.IsComObject(voice))
                    {
                        try
                        {
                            Marshal.FinalReleaseComObject(voice);
                        }
                        catch
                        {
                            // ignore release errors
                        }
                    }
                }
            }, cancellationToken).ConfigureAwait(false);
        }

        private static int ConvertToSapiRate(double rate)
        {
            if (rate <= 0.0)
            {
                return 0;
            }

            var normalized = (rate - 1.0) * 5.0;
            return (int)Math.Clamp(Math.Round(normalized), -10, 10);
        }

        private static int ConvertToSapiVolume(double volume)
        {
            if (volume <= 0.0)
            {
                return 0;
            }

            var scaled = (int)Math.Round(volume * 100);
            return Math.Clamp(scaled, 0, 100);
        }

        private static void TryDelete(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // ignore cleanup failures
            }
        }

        private static bool IsPiperCommand(string value)
        {
            var trimmed = value.Trim();
            return trimmed.EndsWith("piper", StringComparison.OrdinalIgnoreCase)
                || trimmed.EndsWith("piper.exe", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNetworkPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var trimmed = path.Trim();
            return trimmed.StartsWith("\\\\", StringComparison.Ordinal)
                || trimmed.StartsWith("//", StringComparison.Ordinal);
        }

        private static string SanitizeText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var sb = new System.Text.StringBuilder(text.Length);
            foreach (var ch in text)
            {
                if (char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch))
                {
                    sb.Append(ch);
                    continue;
                }

                if (".,!?;:'\"()[]{}".IndexOf(ch) >= 0)
                {
                    sb.Append(ch);
                }
            }

            return sb.ToString().Trim();
        }
    }

    internal readonly struct PiperCommand
    {
        public PiperCommand(string fileName, string argumentsPrefix, bool requiresExistingFile)
        {
            FileName = fileName;
            ArgumentsPrefix = argumentsPrefix;
            RequiresExistingFile = requiresExistingFile;
        }

        public string FileName { get; }
        public string ArgumentsPrefix { get; }
        public bool RequiresExistingFile { get; }
    }

    public sealed class TtsResult
    {
        private TtsResult() { }

        public bool Success { get; private set; }
        public bool UsedSapiFallback { get; private set; }
        public byte[]? AudioBytes { get; private set; }
        public string? Error { get; private set; }

        public static TtsResult FromAudio(byte[] audio)
        {
            return new TtsResult
            {
                Success = true,
                AudioBytes = audio
            };
        }

        public static TtsResult UsedSapi()
        {
            return new TtsResult
            {
                Success = true,
                UsedSapiFallback = true
            };
        }

        public static TtsResult Failed(string error)
        {
            return new TtsResult
            {
                Success = false,
                Error = error
            };
        }
    }
}
