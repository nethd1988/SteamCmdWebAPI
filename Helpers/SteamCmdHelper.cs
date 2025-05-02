using System;
using System.IO;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace SteamCmdWebAPI.Helpers
{
    public static class SteamCmdHelper
    {
        public static bool VerifySteamCmdExecution(string steamCmdPath)
        {
            if (!File.Exists(steamCmdPath))
                return false;
                
            try
            {
                var testProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = steamCmdPath,
                        Arguments = "+quit",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    }
                };

                testProcess.Start();
                string output = testProcess.StandardOutput.ReadToEnd();
                testProcess.WaitForExit();
                
                // Kiểm tra xem SteamCMD có thực sự chạy không
                return output.Contains("Steam Console Client") && output.Contains("Valve Corporation");
            }
            catch
            {
                return false;
            }
        }

        public static bool EnsureSteamCmdIsNotRunning()
        {
            try
            {
                var processes = Process.GetProcessesByName("steamcmd");
                if (processes.Length > 0)
                {
                    foreach (var process in processes)
                    {
                        try { process.Kill(); } catch { }
                    }
                    return true;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}