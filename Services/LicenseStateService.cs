using System;

namespace SteamCmdWebAPI.Services
{
    public class LicenseStateService
    {
        public bool IsLicenseValid { get; set; }
        public string LicenseMessage { get; set; }
        public bool UsingCache { get; set; }
        
        // Thêm trạng thái để kiểm tra có cần khóa tất cả chức năng hay không
        public bool LockAllFunctions => !IsLicenseValid && !UsingCache;
        
        // Ghi log khi trạng thái license thay đổi
        public void LogLicenseState()
        {
            if (LockAllFunctions)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"CẢNH BÁO: Tất cả chức năng đã bị khóa do giấy phép không hợp lệ");
                Console.WriteLine($"Thông báo: {LicenseMessage}");
                Console.ResetColor();
            }
            else if (!IsLicenseValid && UsingCache)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"CẢNH BÁO: Đang sử dụng giấy phép từ cache");
                Console.WriteLine($"Thông báo: {LicenseMessage}");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"Giấy phép hợp lệ");
                Console.ResetColor();
            }
        }
        
        // Cập nhật trạng thái giấy phép
        public void UpdateLicenseState(bool isLicenseValid, string licenseMessage, bool usingCache)
        {
            IsLicenseValid = isLicenseValid;
            LicenseMessage = licenseMessage;
            UsingCache = usingCache;
            LogLicenseState();
        }
    }
} 