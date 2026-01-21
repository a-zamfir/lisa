// File: Host.Win/Services/TcpCallbackServer.cs
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Host.Win.Models;

namespace Host.Win.Services
{
    public sealed class TcpCallbackServer : IDisposable
    {
        private readonly int _port;
        private readonly Action<AgentToolCallback> _onCallback;
        private readonly string _token;
        private readonly int _maxPayloadBytes;
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private Task? _listenerTask;

        public TcpCallbackServer(int port, string token, Action<AgentToolCallback> onCallback, int maxPayloadBytes = 8192)
        {
            _port = port;
            _token = token;
            _onCallback = onCallback;
            _maxPayloadBytes = maxPayloadBytes;
        }

        public void Start()
        {
            if (_listener != null) return;

            try
            {
                _cts = new CancellationTokenSource();
                _listener = new TcpListener(IPAddress.Loopback, _port);

                // Enable SO_REUSEADDR to allow port reuse after unclean shutdown
                _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

                _listener.Start();
                _listenerTask = Task.Run(() => ListenAsync(_cts.Token));
            }
            catch (Exception ex)
            {
                // Cleanup on failed start
                Trace.TraceError($"Failed to start TCP callback server on port {_port}: {ex.Message}");
                CleanupResources();
                throw;
            }
        }

        private async Task ListenAsync(CancellationToken token)
        {
            if (_listener == null) return;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                    _ = Task.Run(() => HandleClientAsync(client, token), token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // ignore accept errors
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken token)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    var lengthBuffer = new byte[4];
                    while (!token.IsCancellationRequested)
                    {
                        var read = await ReadExactAsync(stream, lengthBuffer, token).ConfigureAwait(false);
                        if (read == 0) return;

                        var length = BitConverter.ToInt32(lengthBuffer, 0);
                        if (length <= 0 || length > _maxPayloadBytes) return;

                        var payloadBuffer = new byte[length];
                        var payloadRead = await ReadExactAsync(stream, payloadBuffer, token).ConfigureAwait(false);
                        if (payloadRead == 0) return;

                        var json = Encoding.UTF8.GetString(payloadBuffer);
                        var payload = JsonSerializer.Deserialize<AgentToolCallback>(json);
                        if (payload != null && IsValidToken(payload.Token))
                        {
                            _onCallback(payload);
                        }
                    }
                }
            }
            catch
            {
                // ignore malformed payloads
            }
        }

        private static async Task<int> ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken token)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), token).ConfigureAwait(false);
                if (read == 0)
                {
                    return 0;
                }

                offset += read;
            }

            return offset;
        }

        private bool IsValidToken(string? providedToken)
        {
            if (string.IsNullOrEmpty(providedToken) || string.IsNullOrEmpty(_token))
            {
                return false;
            }

            // Use constant-time comparison to prevent timing attacks
            var providedBytes = Encoding.UTF8.GetBytes(providedToken);
            var expectedBytes = Encoding.UTF8.GetBytes(_token);

            return CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
        }

        public void Stop()
        {
            CleanupResources();
        }

        private void CleanupResources()
        {
            try
            {
                _cts?.Cancel();
            }
            catch { }

            try
            {
                _listener?.Stop();
            }
            catch { }

            try
            {
                _cts?.Dispose();
            }
            catch { }

            _listener = null;
            _cts = null;

            // Wait for listener task to complete (with timeout)
            try
            {
                _listenerTask?.Wait(TimeSpan.FromSeconds(2));
            }
            catch { }

            _listenerTask = null;
        }

        public void Dispose()
        {
            CleanupResources();
            GC.SuppressFinalize(this);
        }

        // Finalizer for emergency cleanup if Dispose is not called
        ~TcpCallbackServer()
        {
            CleanupResources();
        }
    }
}
