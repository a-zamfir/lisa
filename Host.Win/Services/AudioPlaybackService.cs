// File: Host.Win/Services/AudioPlaybackService.cs
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace Host.Win.Services
{
    public sealed class AudioPlaybackService : IDisposable
    {
        private byte[]? _lastAudio;
        private string? _lastText;
        private WaveOutEvent? _output;
        private WaveStream? _reader;
        private float _lastPlaybackRate = 1.0f;
        private float _lastVolume = 1.0f;

        public bool HasAudio => _lastAudio != null && _lastAudio.Length > 0;
        public string? LastText => _lastText;

        public void SetLastAudio(byte[] audio)
        {
            _lastAudio = audio;
        }

        public void SetLastText(string? text)
        {
            _lastText = text;
        }

        public Task<bool> PlayLastAsync(CancellationToken cancellationToken)
        {
            if (!HasAudio)
            {
                return Task.FromResult(false);
            }

            return PlayAsync(_lastAudio!, _lastPlaybackRate, _lastVolume, cancellationToken);
        }

        public Task<bool> PlayLastAsync(double playbackRate, CancellationToken cancellationToken)
        {
            if (!HasAudio)
            {
                return Task.FromResult(false);
            }

            return PlayAsync(_lastAudio!, playbackRate, _lastVolume, cancellationToken);
        }

        public Task<bool> PlayLastAsync(double playbackRate, double volume, CancellationToken cancellationToken)
        {
            if (!HasAudio)
            {
                return Task.FromResult(false);
            }

            return PlayAsync(_lastAudio!, playbackRate, volume, cancellationToken);
        }

        public Task<bool> PlayAsync(byte[] audio, CancellationToken cancellationToken)
        {
            return PlayAsync(audio, playbackRate: 1.0f, volume: 1.0f, cancellationToken);
        }

        public Task<bool> PlayAsync(byte[] audio, double playbackRate, CancellationToken cancellationToken)
        {
            return PlayAsync(audio, playbackRate, volume: 1.0, cancellationToken);
        }

        public Task<bool> PlayAsync(byte[] audio, double playbackRate, double volume, CancellationToken cancellationToken)
        {
            Stop();
            var safeRate = (float)Math.Clamp(playbackRate, 0.5, 2.0);
            var safeVolume = (float)Math.Clamp(volume, 0.0, 1.0);
            _lastPlaybackRate = safeRate;
            _lastVolume = safeVolume;

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var stream = new MemoryStream(audio);
            _reader = new WaveFileReader(stream);
            _output = new WaveOutEvent();

            WaveStream source = _reader;
            if (Math.Abs(safeRate - 1.0f) > 0.001f)
            {
                source = new PlaybackRateWaveStream(_reader, safeRate);
            }

            try
            {
                _output.Init(source);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Audio playback speed {safeRate:0.00}x not supported; falling back to 1.00x ({ex.Message})");
                _lastPlaybackRate = 1.0f;
                _output.Init(_reader);
            }

            try
            {
                _output.Volume = safeVolume;
            }
            catch
            {
                // ignore volume failures (some drivers/devices can be picky)
            }

            _output.PlaybackStopped += (_, _) =>
            {
                Stop();
                tcs.TrySetResult(true);
            };

            if (cancellationToken.CanBeCanceled)
            {
                cancellationToken.Register(() => _output?.Stop());
            }

            _output.Play();
            return tcs.Task;
        }

        public void Stop()
        {
            try
            {
                _output?.Stop();
            }
            catch
            {
                // ignore stop errors
            }
            _output?.Dispose();
            _reader?.Dispose();
            _output = null;
            _reader = null;
        }

        public void Dispose()
        {
            Stop();
        }

        private sealed class PlaybackRateWaveStream : WaveStream
        {
            private readonly WaveStream _source;
            private readonly WaveFormat _format;

            public PlaybackRateWaveStream(WaveStream source, float playbackRate)
            {
                _source = source;
                var original = source.WaveFormat;
                var newRate = (int)Math.Clamp(Math.Round(original.SampleRate * playbackRate), 8000, 192000);
                _format = CreateFormatWithSampleRate(original, newRate);
            }

            public override WaveFormat WaveFormat => _format;

            public override long Length => _source.Length;

            public override long Position
            {
                get => _source.Position;
                set => _source.Position = value;
            }

            public override int Read(byte[] buffer, int offset, int count) => _source.Read(buffer, offset, count);

            private static WaveFormat CreateFormatWithSampleRate(WaveFormat original, int sampleRate)
            {
                if (original.Encoding == WaveFormatEncoding.IeeeFloat)
                {
                    return WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, original.Channels);
                }

                // Default to PCM; WaveOut will accept this as long as the underlying samples are PCM-compatible.
                return new WaveFormat(sampleRate, original.BitsPerSample, original.Channels);
            }
        }
    }
}
