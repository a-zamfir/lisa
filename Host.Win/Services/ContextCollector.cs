// File: Host.Win/Services/ContextCollector.cs
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using System.Drawing;

namespace Host.Win.Services
{
    /// <summary>
    /// Collects basic foreground window context. Screen capture stubs left for future work.
    /// </summary>
    public sealed class ContextCollector : IDisposable
    {
        public string? GetActiveWindowTitle()
        {
            var handle = NativeMethods.GetForegroundWindow();
            if (handle == IntPtr.Zero) return null;

            var sb = new StringBuilder(1024);
            if (NativeMethods.GetWindowText(handle, sb, sb.Capacity) > 0)
            {
                return sb.ToString();
            }

            return null;
        }

        public string? GetActiveProcessName()
        {
            var handle = NativeMethods.GetForegroundWindow();
            if (handle == IntPtr.Zero) return null;

            if (NativeMethods.GetWindowThreadProcessId(handle, out var processId) > 0)
            {
                try
                {
                    using var process = Process.GetProcessById((int)processId);
                    return process.ProcessName;
                }
                catch (Exception ex)
                {
                    Trace.TraceError($"Failed to get process name: {ex.Message}");
                }
            }

            return null;
        }

        public Screen? GetActiveScreen()
        {
            var handle = NativeMethods.GetForegroundWindow();
            return handle == IntPtr.Zero ? Screen.PrimaryScreen : Screen.FromHandle(handle);
        }

        public Screen GetPrimaryScreen()
        {
            if (Screen.PrimaryScreen != null)
            {
                return Screen.PrimaryScreen;
            }

            var screens = Screen.AllScreens;
            return screens.Length > 0 ? screens[0] : Screen.FromPoint(System.Drawing.Point.Empty);
        }

        // Future: capture active window bitmap for context enrichment.
        // public Bitmap? CaptureActiveWindow() { return null; }

        public void Dispose()
        {
            // Nothing to dispose yet; placeholder for future handles.
        }

        private static class NativeMethods
        {
            [DllImport("user32.dll")]
            internal static extern IntPtr GetForegroundWindow();

            [DllImport("user32.dll", SetLastError = true)]
            internal static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

            [DllImport("user32.dll", SetLastError = true)]
            internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        }
    }
}
