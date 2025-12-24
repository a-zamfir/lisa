// File: Host.Win/Services/WindowBackdropService.cs
using System;
using System.Runtime.InteropServices;

namespace Host.Win.Services
{
    internal static class WindowBackdropService
    {
        private const int WcaAccentPolicy = 19;
        private const int AccentDisabled = 0;
        private const int AccentEnableAcrylicBlurBehind = 4;

        [StructLayout(LayoutKind.Sequential)]
        private struct AccentPolicy
        {
            public int AccentState;
            public int AccentFlags;
            public int GradientColor;
            public int AnimationId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowCompositionAttributeData
        {
            public int Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        [DllImport("user32.dll")]
        private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        public static void ApplyGlass(IntPtr hwnd, bool enabled)
        {
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            var accent = new AccentPolicy
            {
                AccentState = enabled ? AccentEnableAcrylicBlurBehind : AccentDisabled,
                AccentFlags = 2,
                GradientColor = enabled ? unchecked((int)0xCC1E242C) : 0,
                AnimationId = 0
            };

            var size = Marshal.SizeOf<AccentPolicy>();
            var data = new WindowCompositionAttributeData
            {
                Attribute = WcaAccentPolicy,
                SizeOfData = size
            };

            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, ptr, false);
                data.Data = ptr;
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
    }
}
