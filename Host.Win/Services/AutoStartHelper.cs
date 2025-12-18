// File: Host.Win/Services/AutoStartHelper.cs
using System;
using Microsoft.Win32;

namespace Host.Win.Services
{
    /// <summary>
    /// Registry-based auto-start helper (user scope). Call from installer or settings page when allowed.
    /// </summary>
    public static class AutoStartHelper
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static void Enable(string appName, string executablePath)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true) ??
                            Registry.CurrentUser.CreateSubKey(RunKeyPath);
            key?.SetValue(appName, $"\"{executablePath}\"");
        }

        public static void Disable(string appName)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(appName, throwOnMissingValue: false);
        }

        public static bool IsEnabled(string appName, string executablePath)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var value = key?.GetValue(appName) as string;
            return string.Equals(value?.Trim('"'), executablePath, StringComparison.OrdinalIgnoreCase);
        }
    }
}
