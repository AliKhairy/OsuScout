using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace OsuScoutNew.Services
{
    public static class SystemInteropService
    {
        // --- HOTKEY CONSTANTS ---
        public const int HOTKEY_ID = 9000;
        public const uint MOD_ALT = 0x0001;
        public const uint VK_S = 0x53;
        public const int WM_HOTKEY = 0x0312;

        private const int SW_RESTORE = 9;

        // --- P/INVOKES ---
        [DllImport("user32.dll")]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ShowWindow(IntPtr hWnd, int nCmdShow);

        // --- ENCAPSULATED LOGIC ---
        public static bool FocusOsuProcess()
        {
            try
            {
                var osuProcess = Process.GetProcessesByName("osu!").FirstOrDefault();
                if (osuProcess != null)
                {
                    IntPtr handle = osuProcess.MainWindowHandle;
                    if (handle != IntPtr.Zero)
                    {
                        ShowWindow(handle, SW_RESTORE);
                        SetForegroundWindow(handle);
                        return true;
                    }
                }
            }
            catch
            {
                // Silently fail if access is denied by the OS
            }

            return false;
        }
    }
}