// File: Host.Win/Services/TrayIconManager.cs
using System;
using System.Diagnostics;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;


namespace Host.Win.Services
{
    /// <summary>
    /// Manages the system tray icon and menu.
    /// </summary>
    public sealed class TrayIconManager : IDisposable
    {
        private WinForms.NotifyIcon _notifyIcon;
        private bool _disposed;

        public TrayIconManager(Action onOpenAssistant, Action onOpenSettings, Action onQuit)
        {
            _notifyIcon = new NotifyIcon
            {
                Text = "LISA Assistant",
                Icon = SystemIcons.Application,
                Visible = true
            };

            var contextMenu = new ContextMenuStrip();
            contextMenu.Items.Add("Open Assistant", null, (_, _) => onOpenAssistant());
            contextMenu.Items.Add("Settings", null, (_, _) => onOpenSettings());
            contextMenu.Items.Add(new ToolStripSeparator());
            contextMenu.Items.Add("Quit", null, (_, _) => onQuit());

            _notifyIcon.ContextMenuStrip = contextMenu;
            _notifyIcon.DoubleClick += (_, _) => onOpenAssistant();

            Trace.WriteLine("Tray icon created.");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
    }
}
