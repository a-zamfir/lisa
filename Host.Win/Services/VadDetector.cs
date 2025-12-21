// File: Host.Win/Services/VadDetector.cs
using System;
using NAudio.Wave;

namespace Host.Win.Services
{
    public enum VadDecision
    {
        None,
        Speech,
        SilenceTimeout
    }

    public sealed class VadDetector
    {
        public const int FrameDurationMs = 20;
        public const int SilenceTimeoutMs = 800;
        public const int MaxRecordingMs = 20000;
        public const float SpeechRmsThreshold = 0.02f;
        public const float SilenceRmsThreshold = 0.012f;

        private int _silenceFrames;
        private bool _speechDetected;

        public bool SpeechDetected => _speechDetected;

        public void Reset()
        {
            _silenceFrames = 0;
            _speechDetected = false;
        }

        public VadDecision ProcessFrame(float rms)
        {
            if (rms >= SpeechRmsThreshold)
            {
                _speechDetected = true;
                _silenceFrames = 0;
                return VadDecision.Speech;
            }

            if (_speechDetected && rms < SilenceRmsThreshold)
            {
                _silenceFrames++;
                if (_silenceFrames >= SilenceFramesToStop)
                {
                    return VadDecision.SilenceTimeout;
                }
            }

            return VadDecision.None;
        }

        public static int GetFrameByteCount(WaveFormat format)
        {
            var samplesPerFrame = format.SampleRate * FrameDurationMs / 1000;
            return samplesPerFrame * format.BlockAlign;
        }

        public static float CalculateRms(ReadOnlySpan<byte> buffer, WaveFormat format)
        {
            if (buffer.Length == 0) return 0f;

            var channels = Math.Max(1, format.Channels);
            var sampleCount = buffer.Length / format.BlockAlign;
            if (sampleCount == 0) return 0f;

            double sumSquares = 0;
            var frames = 0;

            if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
            {
                var floatCount = buffer.Length / 4;
                for (var i = 0; i < floatCount; i += channels)
                {
                    double frame = 0;
                    for (var ch = 0; ch < channels && i + ch < floatCount; ch++)
                    {
                        var sample = BitConverter.ToSingle(buffer.Slice((i + ch) * 4, 4));
                        frame += Math.Abs(sample);
                    }
                    frame /= channels;
                    sumSquares += frame * frame;
                    frames++;
                }
            }
            else if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16)
            {
                for (var i = 0; i < buffer.Length; i += format.BlockAlign)
                {
                    double frame = 0;
                    for (var ch = 0; ch < channels; ch++)
                    {
                        var offset = i + (ch * 2);
                        if (offset + 1 >= buffer.Length) break;
                        short sample = BitConverter.ToInt16(buffer.Slice(offset, 2));
                        frame += Math.Abs(sample / 32768f);
                    }
                    frame /= channels;
                    sumSquares += frame * frame;
                    frames++;
                }
            }
            else if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 32)
            {
                for (var i = 0; i < buffer.Length; i += format.BlockAlign)
                {
                    double frame = 0;
                    for (var ch = 0; ch < channels; ch++)
                    {
                        var offset = i + (ch * 4);
                        if (offset + 3 >= buffer.Length) break;
                        int sample = BitConverter.ToInt32(buffer.Slice(offset, 4));
                        frame += Math.Abs(sample / (float)int.MaxValue);
                    }
                    frame /= channels;
                    sumSquares += frame * frame;
                    frames++;
                }
            }
            else
            {
                for (var i = 0; i < buffer.Length; i += format.BlockAlign)
                {
                    double frame = 0;
                    for (var ch = 0; ch < channels; ch++)
                    {
                        var offset = i + (ch * 2);
                        if (offset + 1 >= buffer.Length) break;
                        short sample = BitConverter.ToInt16(buffer.Slice(offset, 2));
                        frame += Math.Abs(sample / 32768f);
                    }
                    frame /= channels;
                    sumSquares += frame * frame;
                    frames++;
                }
            }

            if (frames == 0) return 0f;
            return (float)Math.Sqrt(sumSquares / frames);
        }

        private static int SilenceFramesToStop
        {
            get
            {
                var frames = SilenceTimeoutMs / FrameDurationMs;
                return Math.Max(1, frames);
            }
        }
    }
}
