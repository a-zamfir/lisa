// File: Host.Win/Services/AgentCallbackServer.cs
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Host.Win.Models;

namespace Host.Win.Services
{
    public sealed class AgentCallbackServer : IDisposable
    {
        private readonly int _port;
        private readonly Action<AgentToolCallback> _onCallback;
        private UdpClient? _udpClient;
        private CancellationTokenSource? _cts;
        private Task? _listenerTask;

        public AgentCallbackServer(int port, Action<AgentToolCallback> onCallback)
        {
            _port = port;
            _onCallback = onCallback;
        }

        public void Start()
        {
            if (_udpClient != null) return;
            // UDP callbacks are a temporary bridge until streaming HTTP (SSE/WebSocket) is available.
            _cts = new CancellationTokenSource();
            _udpClient = new UdpClient(new IPEndPoint(IPAddress.Loopback, _port));
            _listenerTask = Task.Run(() => ListenAsync(_cts.Token));
        }

        private async Task ListenAsync(CancellationToken token)
        {
            if (_udpClient == null) return;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var result = await _udpClient.ReceiveAsync().ConfigureAwait(false);
                    // No sender validation yet; add token + size guard when replacing UDP transport.
                    var json = Encoding.UTF8.GetString(result.Buffer);
                    var payload = JsonSerializer.Deserialize<AgentToolCallback>(json);
                    if (payload != null)
                    {
                        _onCallback(payload);
                    }
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch
                {
                    // ignore malformed payloads
                }
            }
        }

        public void Stop()
        {
            _cts?.Cancel();
            _udpClient?.Close();
            _udpClient?.Dispose();
            _udpClient = null;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
