// File: Host.Win/Services/TcpCallbackServer.cs
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
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
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Loopback, _port);
            _listener.Start();
            _listenerTask = Task.Run(() => ListenAsync(_cts.Token));
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
                        if (payload != null && string.Equals(payload.Token, _token, StringComparison.Ordinal))
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

        public void Stop()
        {
            _cts?.Cancel();
            _listener?.Stop();
            _listener = null;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
