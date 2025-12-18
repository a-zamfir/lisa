// File: Host.Win/Services/HotkeyManager.cs
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Host.Win.Services
{
    /// <summary>
    /// Registers and listens for a global hotkey (Ctrl+Space).
    /// Uses a hidden message-only window so the WPF overlay does not need focus.
    /// </summary>
    public sealed class HotkeyManager : IDisposable
    {
        private const int HOTKEY_ID = 0x0001;
        private const int WM_HOTKEY = 0x0312;
        private const uint MOD_CONTROL = 0x0002;
        private const uint VK_SPACE = 0x20;

        private readonly HwndSource _source;
        private readonly Action _callback;
        private bool _disposed;

        public bool IsRegistered { get; private set; }

        public HotkeyManager(Action callback)
        {
            _callback = callback;

            var parameters = new HwndSourceParameters("HotkeyHost")
            {
                Width = 0,
                Height = 0,
                PositionX = 0,
                PositionY = 0,
                WindowStyle = unchecked((int)0x80000000), // WS_POPUP
                ExtendedWindowStyle = 0x00000080 // WS_EX_TOOLWINDOW
            };

            _source = new HwndSource(parameters);
            _source.AddHook(WndProc);

            IsRegistered = NativeMethods.RegisterHotKey(_source.Handle, HOTKEY_ID, MOD_CONTROL, VK_SPACE);
            if (!IsRegistered)
            {
                Trace.TraceError("RegisterHotKey failed with error: " + Marshal.GetLastWin32Error());
            }
            else
            {
                Trace.WriteLine("Global hotkey registered (Ctrl+Space).");
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
            {
                handled = true;
                _callback();
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (IsRegistered)
            {
                NativeMethods.UnregisterHotKey(_source.Handle, HOTKEY_ID);
                IsRegistered = false;
            }
            _source.RemoveHook(WndProc);
            _source.Dispose();
        }

        private static class NativeMethods
        {
            [DllImport("user32.dll", SetLastError = true)]
            internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

            [DllImport("user32.dll", SetLastError = true)]
            internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        }
    }
}
