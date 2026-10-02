using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using OsuScoutNew.Core;

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

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT point);

        private struct POINT { public int X, Y; }

        // The mouse position in screen pixels.
        public static (int X, int Y) CursorPosition()
        {
            GetCursorPos(out POINT point);
            return (point.X, point.Y);
        }

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

        // A click on a window that hasn't got focus asks it (WM_MOUSEACTIVATE) whether to take
        // focus; MA_NOACTIVATE leaves focus where it was and still delivers the click.
        public const int WM_MOUSEACTIVATE = 0x0021;
        public const int MA_NOACTIVATE = 3;

        // Puts an always-on-top window above every other always-on-top window, without
        // taking focus. A fullscreen game's window is often always-on-top too, and whichever
        // of the two was clicked last would otherwise cover the other.
        public static void BringAboveOtherTopmostWindows(IntPtr handle)
        {
            if (handle != IntPtr.Zero)
                SetWindowPos(handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        // --- ENCAPSULATED LOGIC ---

        // Brings a window to the front. A game in fullscreen minimises itself when it loses
        // focus (lazer: "Minimise osu! when switching to another app"), so it is restored
        // first, but only then: restoring a window that isn't minimised can knock it out of
        // fullscreen.
        public static bool FocusWindow(IntPtr handle)
        {
            if (handle == IntPtr.Zero || !IsWindow(handle)) return false;
            if (IsIconic(handle)) ShowWindow(handle, SW_RESTORE);
            return SetForegroundWindow(handle);
        }

        // Switches to the running game of this client. Both clients run as osu!.exe, so
        // stable's button never lands on lazer and the other way round.
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
