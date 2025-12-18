// File: Host.Win/Services/OverlayController.cs
using System;
using System.Diagnostics;
using System.Windows.Forms;
using Host.Win.Views;

namespace Host.Win.Services
{
    /// <summary>
    /// Controls showing/hiding the overlay window and positions it bottom-right.
    /// </summary>
    public sealed class OverlayController : IDisposable
    {
        private readonly OverlayWindow _window;
        private readonly ContextCollector _contextCollector;
        private bool _disposed;

        public OverlayController(OverlayWindow window, ContextCollector contextCollector)
        {
            _window = window;
            _contextCollector = contextCollector;
        }

        public void ToggleOverlay()
        {
            if (_window.IsVisible)
            {
                HideOverlay();
            }
            else
            {
                ShowOverlay();
            }
        }

        public void ShowOverlay()
        {
            if (_disposed) return;

            _window.Dispatcher.Invoke(() =>
            {
                var screen = _contextCollector.GetPrimaryScreen();
                _window.ShowAtBottomRight(screen, 16);
                _window.Show();
                _window.Topmost = true; // Keeps overlay above without stealing focus aggressively.
                _window.PlayShowAnimation();
            });
            Trace.WriteLine("Overlay shown.");
        }

        public void HideOverlay()
        {
            if (_disposed) return;

            _window.Dispatcher.Invoke(() =>
            {
                _window.PlayHideAnimation(() => _window.Hide());
            });
            Trace.WriteLine("Overlay hidden.");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _window.Dispatcher.Invoke(() =>
            {
                _window.AllowClose = true;
                _window.Close();
            });
        }
    }
}
