using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SteamCmdWebAPI.Services;
using System;

namespace SteamCmdWebAPI.Pages
{
    [AllowAnonymous]
    public class LicenseErrorModel : PageModel
    {
        private readonly LicenseStateService _licenseStateService;
        
        public string LicenseMessage { get; private set; }
        
        public LicenseErrorModel(LicenseStateService licenseStateService)
        {
            _licenseStateService = licenseStateService;
        }
        
        public void OnGet()
        {
            LicenseMessage = _licenseStateService.LicenseMessage ?? "Không có thông tin về lỗi giấy phép";
        }
    }
}