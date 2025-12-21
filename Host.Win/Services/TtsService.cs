// File: Host.Win/Services/TtsService.cs
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Host.Win.Models;

namespace Host.Win.Services
{
    public sealed class TtsService
    {
        private static readonly TimeSpan PiperTimeout = TimeSpan.FromSeconds(10);

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

            if (string.IsNullOrWhiteSpace(command.FileName))
            {
                return await FallbackToSapiAsync(text, settings, cancellationToken, "Piper executable not found.").ConfigureAwait(false);
            }

            if (command.RequiresExistingFile && !File.Exists(command.FileName))
            {
                return await FallbackToSapiAsync(text, settings, cancellationToken, "Piper executable not found.").ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
            {
                return await FallbackToSapiAsync(text, settings, cancellationToken, "Piper voice model not found.").ConfigureAwait(false);
            }

            var outputPath = Path.Combine(Path.GetTempPath(), $"lisa_tts_{Guid.NewGuid():N}.wav");
            try
            {
                var args = $"{command.ArgumentsPrefix} --model \"{modelPath}\" --output_file \"{outputPath}\"";
                if (!string.IsNullOrWhiteSpace(configPath) && File.Exists(configPath))
                {
                    args += $" --config \"{configPath}\"";
                }

                var lengthScale = GetPiperLengthScale(settings);
                if (lengthScale.HasValue)
                {
                    args += $" --length_scale {lengthScale.Value:0.00}";
                }

                if (settings?.PiperSpeakerId is int speakerId && speakerId >= 0)
                {
                    args += $" --speaker {speakerId}";
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = command.FileName,
                    Arguments = args,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = startInfo };
                if (!process.Start())
                {
                    return await FallbackToSapiAsync(text, settings, cancellationToken, "Failed to start Piper.").ConfigureAwait(false);
                }

                await process.StandardInput.WriteAsync(text).ConfigureAwait(false);
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
                        return TtsResult.Failed("TTS cancelled.");
                    }

                    return TtsResult.Failed("TTS timed out.");
                }

                if (process.ExitCode != 0)
                {
                    var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                    var message = string.IsNullOrWhiteSpace(error) ? "Piper exited with errors." : error.Trim();
                    return await FallbackToSapiAsync(text, settings, cancellationToken, message).ConfigureAwait(false);
                }

                if (!File.Exists(outputPath))
                {
                    return await FallbackToSapiAsync(text, settings, cancellationToken, "Piper output missing.").ConfigureAwait(false);
                }

                var bytes = await File.ReadAllBytesAsync(outputPath, cancellationToken).ConfigureAwait(false);
                return TtsResult.FromAudio(bytes);
            }
            catch (Exception ex)
            {
                return await FallbackToSapiAsync(text, settings, cancellationToken, $"Piper failed: {ex.Message}").ConfigureAwait(false);
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
                if (IsPiperCommand(pythonPath))
                {
                    return new PiperCommand(pythonPath, string.Empty, requiresExistingFile: false);
                }
                return new PiperCommand(pythonPath, "-m piper", requiresExistingFile: false);
            }

            if (!string.IsNullOrWhiteSpace(settings?.PiperExePath))
            {
                return new PiperCommand(settings.PiperExePath, string.Empty, requiresExistingFile: true);
            }

            var bundled = Path.Combine(AppContext.BaseDirectory, "piper", "piper.exe");
            return new PiperCommand(bundled, string.Empty, requiresExistingFile: true);
        }

        private static string ResolveVoiceModelPath(HostSettings? settings)
        {
            if (!string.IsNullOrWhiteSpace(settings?.PiperVoiceModelPath))
            {
                return settings.PiperVoiceModelPath;
            }

            return string.Empty;
        }

        private static string ResolveVoiceConfigPath(HostSettings? settings, string modelPath)
        {
            if (!string.IsNullOrWhiteSpace(settings?.PiperVoiceConfigPath))
            {
                return settings.PiperVoiceConfigPath;
            }

            if (!string.IsNullOrWhiteSpace(modelPath))
            {
                var defaultConfig = Path.ChangeExtension(modelPath, ".json");
                return defaultConfig ?? string.Empty;
            }

            return string.Empty;
        }

        private static double? GetPiperLengthScale(HostSettings? settings)
        {
            var rate = settings?.VoiceRate ?? 1.0;
            if (Math.Abs(rate - 1.0) < 0.01)
            {
                return null;
            }

            if (rate <= 0.05)
            {
                return null;
            }

            var lengthScale = 1.0 / rate;
            return Math.Clamp(lengthScale, 0.5, 2.0);
        }

        private static async Task<TtsResult> FallbackToSapiAsync(
            string text,
            HostSettings? settings,
            CancellationToken cancellationToken,
            string reason)
        {
            Trace.TraceWarning($"TTS fallback to SAPI: {reason}");
            var success = await TrySpeakWithSapiAsync(text, settings, cancellationToken).ConfigureAwait(false);
            if (!success)
            {
                return TtsResult.Failed(reason);
            }
            return TtsResult.UsedSapi();
        }

        private static async Task<bool> TrySpeakWithSapiAsync(string text, HostSettings? settings, CancellationToken cancellationToken)
        {
            var synthType = Type.GetType("System.Speech.Synthesis.SpeechSynthesizer, System.Speech");
            if (synthType == null)
            {
                Trace.TraceWarning("SAPI not available on this system.");
                return false;
            }

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
                    Trace.TraceWarning($"SAPI TTS failed: {ex.Message}");
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
