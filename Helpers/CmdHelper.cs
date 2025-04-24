using System;
using System.Diagnostics;
using System.IO;

namespace SteamCmdWebAPI.Helpers
{
    public class CmdHelper
    {
        /// <summary>
        /// Thực thi một lệnh command thông qua cmd.exe.
        /// </summary>
        /// <param name="command">Lệnh cần thực thi.</param>
        /// <param name="timeWaitToKill">
        /// Thời gian chờ (ms) trước khi buộc kết thúc tiến trình. 
        /// Nếu bằng 0 thì chờ vô thời hạn.
        /// </param>
        public static void RunCommand(string command, int timeWaitToKill = 0)
        {
            var processInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {command}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var process = new Process { StartInfo = processInfo })
            {
                process.OutputDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        Console.WriteLine(e.Data);
                };

                process.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        Console.Error.WriteLine(e.Data);
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                if (timeWaitToKill > 0)
                {
                    if (!process.WaitForExit(timeWaitToKill))
                    {
                        try { process.Kill(); } catch { }
                    }
                }
                else
                {
                    process.WaitForExit();
                }
            }
        }
    }
}