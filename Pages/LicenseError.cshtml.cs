using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SteamCmdWebAPI.Pages
{
    public class LicenseErrorModel : PageModel
    {
        public string ErrorMessage { get; set; } = "Giấy phép không hợp lệ hoặc đã hết hạn.";
        public string ContactEmail { get; set; } = "support@example.com";

        public void OnGet(string message = null)
        {
            if (!string.IsNullOrEmpty(message))
            {
                ErrorMessage = message;
            }
        }
    }
}