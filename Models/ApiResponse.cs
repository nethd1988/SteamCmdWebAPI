namespace SteamCmdWebAPI.Models
{
    public class ApiResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public object Data { get; set; }

        public ApiResponse()
        {
            Success = false;
            Message = string.Empty;
            Data = null;
        }
    }
} 