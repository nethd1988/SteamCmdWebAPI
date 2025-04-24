using SteamCmdWebAPI.Helpers;
using System;
using System.Diagnostics;

namespace SteamCmdWebAPI.Extensions
{
    public static class ProcessExtension
    {
        public static void Terminator(this Process process, int timeWaitToExit = 10000)
        {
            if (process == null || process.HasExited) return;

            try
            {
                // Gọi Kill để kết thúc tiến trình
                process.Kill();

                // Đợi tiến trình kết thúc với thời gian chờ được chỉ định
                if (!process.WaitForExit(timeWaitToExit))
                {
                    // Nếu tiến trình vẫn chưa thoát sau thời gian chờ
                    var processId = process?.Id;
                    if (processId != null)
                    {
                        // Chạy lệnh taskkill để ép buộc kết thúc tiến trình
                        CmdHelper.RunCommand($"taskkill /PID {process.Id} /F");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Process termination failed: {ex.Message}");
            }
        }
    }
}