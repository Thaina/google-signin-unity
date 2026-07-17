#if UNITY_EDITOR_WIN ||UNITY_STANDALONE_WIN

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Debug = UnityEngine.Debug;

namespace Google.Impl {
    public static partial class DesktopExt {
        public static partial void TryBringGameToFront() {
            try {
                var targetHWnd = Process.GetCurrentProcess().MainWindowHandle;

                ShowWindow(targetHWnd, 9);

                keybd_event(0x12, 0, 0, 0);
                keybd_event(0x12, 0, 0x0002, 0);

                SetForegroundWindow(targetHWnd);

                Debug.Log("[Auth] Windows BringGameToFront successful.");
            } catch (Exception e) {
                Debug.LogWarning($"[Auth] Windows BringGameToFront failed: {e.Message}");
            }

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            static extern bool SetForegroundWindow(IntPtr hWnd);

            [DllImport("user32.dll")]
            static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

            [DllImport("user32.dll")]
            static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);
        }
    }
}

#endif