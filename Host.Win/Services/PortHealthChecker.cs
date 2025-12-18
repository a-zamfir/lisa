// File: Host.Win/Services/PortHealthChecker.cs
using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Host.Win.Services
{
    public sealed class PortHealthChecker : IDisposable
    {
        private readonly int _port;
        private readonly string _host;
        private readonly Action<bool> _onStatusChanged;
        private readonly DispatcherTimer _timer;
        private readonly Func<Task<bool>>? _customCheck;
        private bool _disposed;
        private bool _isChecking;

        public PortHealthChecker(string host, int port, Action<bool> onStatusChanged, Func<Task<bool>>? customCheck = null)
        {
            _host = host;
            _port = port;
            _onStatusChanged = onStatusChanged;
            _customCheck = customCheck;
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _timer.Tick += OnTick;
        }

        public void Start() => _timer.Start();

        private async void OnTick(object? sender, EventArgs e)
        {
            if (_isChecking) return;
            _isChecking = true;
            var ready = await CheckAsync().ConfigureAwait(true);
            _onStatusChanged(ready);
            _isChecking = false;
        }

        private Task<bool> CheckAsync() => _customCheck != null ? _customCheck() : CheckPortAsync();

        private async Task<bool> CheckPortAsync()
        {
            using var client = new TcpClient();
            try
            {
                var connectTask = client.ConnectAsync(_host, _port);
                var timeoutTask = Task.Delay(250);
                var completed = await Task.WhenAny(connectTask, timeoutTask).ConfigureAwait(false);
                if (completed == timeoutTask)
                {
                    return false;
                }

                await connectTask.ConfigureAwait(false);
                return client.Connected;
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Stop();
            _timer.Tick -= OnTick;
        }
    }
}
