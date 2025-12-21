// File: Host.Win/Services/AudioPlaybackService.cs
using System;
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

            return PlayAsync(_lastAudio!, cancellationToken);
        }

        public Task<bool> PlayAsync(byte[] audio, CancellationToken cancellationToken)
        {
            Stop();
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var stream = new MemoryStream(audio);
            _reader = new WaveFileReader(stream);
            _output = new WaveOutEvent();
            _output.Init(_reader);
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
    }
}
