using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace UdpGhost.Services
{
    public static class BlurHelper
    {
        [DllImport("user32.dll")]
        private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        [StructLayout(LayoutKind.Sequential)]
        public struct AccentPolicy
        {
            public AccentState AccentState;
            public int AccentFlags;
            public int GradientColor;
            public int AnimationId;
        }

        public enum AccentState
        {
            Disabled = 0,
            EnableBlurBehind = 3,
            EnableAcrylicBlurBehind = 4
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WindowCompositionAttributeData
        {
            public int Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        public static void EnableBlur(Window window)
        {
            var accent = new AccentPolicy
            {
                AccentState = AccentState.EnableAcrylicBlurBehind,
                GradientColor = unchecked((int)0x990D1117)  // 半透明深色
            };

            var size = Marshal.SizeOf(accent);
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, ptr, false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute = 19,
                    SizeOfData = size,
                    Data = ptr
                };
                SetWindowCompositionAttribute(new WindowInteropHelper(window).Handle, ref data);
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }
    }
}
