using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using OsuScoutNew.Core;

namespace OsuScoutNew.Services
{
    public static class SystemInteropService
    {
        private const int SW_RESTORE = 9;

        // --- P/INVOKES ---
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        // --- ENCAPSULATED LOGIC ---

        // Brings a window to the front. A game in fullscreen minimises itself when it loses
        // focus (lazer: MinimiseOnFocusLossInFullscreen), so it is restored first, but only
        // then: restoring a window that isn't minimised can knock it out of fullscreen.
        public static bool FocusWindow(IntPtr handle)
        {
            if (handle == IntPtr.Zero || !IsWindow(handle)) return false;
            if (IsIconic(handle)) ShowWindow(handle, SW_RESTORE);
            return SetForegroundWindow(handle);
        }

        public static bool FocusOsuProcess(OsuClient client, IEnumerable<string> processNames)
        {
            try
            {
                var osuProcess = GameClients.FindRunningGame(client, processNames);
                if (osuProcess != null)
                {
                    IntPtr handle = osuProcess.MainWindowHandle;
                    if (handle != IntPtr.Zero)
                    {
                        FocusWindow(handle);
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