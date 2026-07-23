using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using System.Text.RegularExpressions;

namespace OsuScoutNew.Services
{
    public static class OsuLocationService
    {
        public static string FindOsuSongsFolder()
        {
            // 1. Try Default Path (C: Drive AppData)
            string defaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "osu!", "Songs");
            if (Directory.Exists(defaultPath)) return defaultPath;

            // 2. Try Windows Registry
            try
            {
                using (var key = Registry.ClassesRoot.OpenSubKey(@"osu\shell\open\command"))
                {
                    if (key != null)
                    {
                        string val = key.GetValue(null) as string;
                        if (!string.IsNullOrEmpty(val))
                        {
                            var match = Regex.Match(val, "\"(.*?)\"");
                            if (match.Success)
                            {
                                string exePath = match.Groups[1].Value;
                                string songsPath = Path.Combine(Path.GetDirectoryName(exePath), "Songs");
                                if (Directory.Exists(songsPath)) return songsPath;
                            }
                        }
                    }
                }
            }
            catch { }

            // 3. Try to find an actively running osu! process
            try
            {
                var osuProcess = Process.GetProcessesByName("osu!").FirstOrDefault();
                if (osuProcess != null && osuProcess.MainModule != null)
                {
                    string exePath = osuProcess.MainModule.FileName;
                    string songsPath = Path.Combine(Path.GetDirectoryName(exePath), "Songs");
                    if (Directory.Exists(songsPath)) return songsPath;
                }
            }
            catch { }

            // 4. THE ULTIMATE FALLBACK: Scan every hard drive connected to the PC
            try
            {
                var fixedDrives = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady);
                foreach (var drive in fixedDrives)
                {
                    string pattern1 = Path.Combine(drive.Name, "osu!", "Songs");
                    string pattern2 = Path.Combine(drive.Name, "Games", "osu!", "Songs");

                    if (Directory.Exists(pattern1)) return pattern1;
                    if (Directory.Exists(pattern2)) return pattern2;
                }
            }
            catch { }

            return null;
        }

        public static string GetOsuExecutablePath(string osuSongsPath)
        {
            if (string.IsNullOrEmpty(osuSongsPath)) return null;

            var osuFolder = Directory.GetParent(osuSongsPath)?.FullName;
            if (osuFolder != null)
            {
                string exePath = Path.Combine(osuFolder, "osu!.exe");
                if (File.Exists(exePath))
                    return exePath;
            }
            return null;
        }
    }
}