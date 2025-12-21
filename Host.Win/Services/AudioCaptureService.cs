// File: Host.Win/Services/AudioCaptureService.cs
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Host.Win.Services
{
    public sealed class AudioCaptureResult
    {
        public AudioCaptureResult(byte[] wavBytes, bool hadSpeech, TimeSpan duration)
        {
            WavBytes = wavBytes;
            HadSpeech = hadSpeech;
            Duration = duration;
        }

        public byte[] WavBytes { get; }
        public bool HadSpeech { get; }
        public TimeSpan Duration { get; }
    }

    public sealed class AudioCaptureService
    {
        public async Task<AudioCaptureResult> CaptureAsync(VadDetector vad, CancellationToken cancellationToken)
        {
            var device = GetDefaultCaptureDevice();
            if (device == null)
            {
                throw new InvalidOperationException("No microphone device found.");
            }

            using var capture = new WasapiCapture(device);
            TrySetPreferredFormat(capture);
            var format = capture.WaveFormat;
            var frameBytes = VadDetector.GetFrameByteCount(format);
            var vadBuffer = new byte[frameBytes * 4];
            var vadBufferCount = 0;
            var stopFlag = 0;
            vad.Reset();

            using var captureStream = new MemoryStream();
            using var writer = new WaveFileWriter(captureStream, format);
            var stopwatch = Stopwatch.StartNew();
            var tcs = new TaskCompletionSource<AudioCaptureResult>(TaskCreationOptions.RunContinuationsAsynchronously);

            void StopCapture()
            {
                if (Interlocked.Exchange(ref stopFlag, 1) == 1)
                {
                    return;
                }
                try
                {
                    capture.StopRecording();
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning($"StopRecording failed: {ex.Message}");
                }
            }

            capture.DataAvailable += (_, args) =>
            {
                if (args.BytesRecorded <= 0) return;
                writer.Write(args.Buffer, 0, args.BytesRecorded);

                if (vadBufferCount + args.BytesRecorded > vadBuffer.Length)
                {
                    Array.Resize(ref vadBuffer, vadBufferCount + args.BytesRecorded);
                }
                Buffer.BlockCopy(args.Buffer, 0, vadBuffer, vadBufferCount, args.BytesRecorded);
                vadBufferCount += args.BytesRecorded;

                while (vadBufferCount >= frameBytes)
                {
                    var frame = new ReadOnlySpan<byte>(vadBuffer, 0, frameBytes);
                    var rms = VadDetector.CalculateRms(frame, format);
                    var decision = vad.ProcessFrame(rms);
                    if (decision == VadDecision.SilenceTimeout)
                    {
                        StopCapture();
                        break;
                    }
                    var remaining = vadBufferCount - frameBytes;
                    if (remaining > 0)
                    {
                        Buffer.BlockCopy(vadBuffer, frameBytes, vadBuffer, 0, remaining);
                    }
                    vadBufferCount = remaining;
                }

                if (stopwatch.ElapsedMilliseconds >= VadDetector.MaxRecordingMs)
                {
                    StopCapture();
                }
            };

            capture.RecordingStopped += (_, args) =>
            {
                writer.Flush();
                var rawBytes = captureStream.ToArray();
                var wavBytes = EnsurePcm16k(rawBytes, format);
                var result = new AudioCaptureResult(wavBytes, vad.SpeechDetected, stopwatch.Elapsed);
                tcs.TrySetResult(result);
                if (args.Exception != null)
                {
                    Trace.TraceWarning($"Capture stopped with error: {args.Exception.Message}");
                }
            };

            using var reg = cancellationToken.Register(StopCapture);
            capture.StartRecording();

            return await tcs.Task.ConfigureAwait(false);
        }

        private static MMDevice? GetDefaultCaptureDevice()
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            }
            catch (Exception ex)
            {
                Trace.TraceError($"Failed to get microphone: {ex.Message}");
                return null;
            }
        }

        private static void TrySetPreferredFormat(WasapiCapture capture)
        {
            try
            {
                capture.WaveFormat = new WaveFormat(16000, 16, 1);
            }
            catch
            {
                // Fall back to device default format.
            }
        }

        private static byte[] EnsurePcm16k(byte[] inputWav, WaveFormat inputFormat)
        {
            if (inputFormat.SampleRate == 16000
                && inputFormat.Channels == 1
                && inputFormat.Encoding == WaveFormatEncoding.Pcm
                && inputFormat.BitsPerSample == 16)
            {
                return inputWav;
            }

            using var inputStream = new MemoryStream(inputWav);
            using var reader = new WaveFileReader(inputStream);
            ISampleProvider sampleProvider = reader.ToSampleProvider();

            if (sampleProvider.WaveFormat.Channels > 1)
            {
                sampleProvider = new StereoToMonoSampleProvider(sampleProvider);
            }

            var resampled = new WdlResamplingSampleProvider(sampleProvider, 16000);
            var pcm16 = new SampleToWaveProvider16(resampled);
            using var outputStream = new MemoryStream();
            WaveFileWriter.WriteWavFileToStream(outputStream, pcm16);
            return outputStream.ToArray();
        }
    }
}
