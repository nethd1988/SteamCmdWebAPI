using SteamCmdWebAPI.Helpers;
using System;
using System.Diagnostics;
using System.Threading;
using System.IO;

namespace SteamCmdWebAPI.Extensions
{
    public static class ProcessExtension
    {
        public static void SafeStart(this Process process)
        {
            try
            {
                // Kiểm tra file tồn tại
                if (!File.Exists(process.StartInfo.FileName))
                {
                    throw new FileNotFoundException($"Không tìm thấy file thực thi: {process.StartInfo.FileName}");
                }
                
                // Kiểm tra file có thể thực thi
                var fileInfo = new FileInfo(process.StartInfo.FileName);
                if (fileInfo.Length < 1000) // Kiểm tra kích thước tối thiểu
                {
                    // Cho phép batch file có kích thước nhỏ
                    if (!process.StartInfo.FileName.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) && 
                        !process.StartInfo.FileName.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException($"File thực thi không hợp lệ (kích thước quá nhỏ): {process.StartInfo.FileName}");
                    }
                }
                
                // Giới hạn độ dài đường dẫn và tham số
                if (process.StartInfo.Arguments.Length > 8000)
                {
                    throw new ArgumentException("Tham số command line quá dài (> 8000 ký tự)");
                }
                
                // Đảm bảo thư mục làm việc tồn tại
                if (!Directory.Exists(process.StartInfo.WorkingDirectory))
                {
                    Directory.CreateDirectory(process.StartInfo.WorkingDirectory);
                }

                // Đối với batch file, đảm bảo có quyền thực thi
                if (process.StartInfo.FileName.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) || 
                    process.StartInfo.FileName.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        // Thử đặt quyền đọc/ghi/thực thi
                        File.SetAttributes(process.StartInfo.FileName, 
                            FileAttributes.Normal);
                    }
                    catch { /* Bỏ qua nếu không thể đặt thuộc tính */ }
                }
                
                // Khởi động với retry
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    try
                    {
                        process.Start();
                        break; // Thoát vòng lặp nếu thành công
                    }
                    catch (Exception ex) when (attempt < 3)
                    {
                        // Đợi một chút trước khi thử lại
                        Thread.Sleep(1000 * attempt);
                        continue;
                    }
                }
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("filename or extension is too long"))
                {
                    throw new Exception("Đường dẫn hoặc tham số quá dài. Hãy rút ngắn đường dẫn cài đặt hoặc giảm số lượng app cần cập nhật cùng lúc.", ex);
                }
                throw;
            }
        }

        public static void Terminator(this Process process, int timeWaitToExit = 10000)
        {
            if (process == null || process.HasExited) return;

            try
            {
                // Thử kết thúc tiến trình nhẹ nhàng trước
                process.CloseMainWindow();

                // Đợi tiến trình kết thúc với thời gian chờ ngắn
                if (!process.WaitForExit(2000))
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
                            CmdHelper.RunCommand($"taskkill /PID {process.Id} /F /T", 15000);

                            // Đợi thêm để đảm bảo tiến trình đã kết thúc
                            if (!process.HasExited)
                            {
                                Thread.Sleep(1000);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Process termination failed: {ex.Message}");
                // Thử cách cuối cùng nếu tất cả phương pháp khác thất bại
                try
                {
                    System.Diagnostics.Process.Start("taskkill", $"/F /PID {process.Id} /T").WaitForExit();
                }
                catch { }
            }
        }
    }
}