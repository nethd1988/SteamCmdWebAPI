using Microsoft.Extensions.Logging;
using System;

namespace SteamCmdWebAPI.Filters
{
    /// <summary>
    /// Filter để lọc các log không cần thiết
    /// </summary>
    public class LoggingFilter : ILogger, IDisposable
    {
        private readonly ILogger _innerLogger;
        private readonly string _categoryName;
        
        // Danh sách các từ khóa thường xuất hiện trong log không cần thiết
        private static readonly string[] _ignoredPhrases = new[] {
            "ExecuteReader completed",
            "Executed DbCommand",
            "User profile is available",
            "Application warmup",
            "Content root path",
            "Hosting environment",
            "Request starting",
            "Request finished",
            "Route matched with",
            "Successfully validated the token",
            "AuthenticationScheme: ",
            "Connection id ",
            "Executing endpoint",
            "Executed endpoint",
            "Running prestart",
            "Starting web server instance"
        };
        
        public LoggingFilter(ILogger innerLogger, string categoryName)
        {
            _innerLogger = innerLogger;
            _categoryName = categoryName;
        }
        
        public IDisposable BeginScope<TState>(TState state)
        {
            return _innerLogger.BeginScope(state);
        }
        
        public bool IsEnabled(LogLevel logLevel)
        {
            // Nếu log level là debug hoặc trace, chúng ta không ghi log trong môi trường production
#if RELEASE
            if (logLevel == LogLevel.Debug || logLevel == LogLevel.Trace)
                return false;
#endif
            
            // Xử lý các loại log khác bởi logger bên trong
            return _innerLogger.IsEnabled(logLevel);
        }
        
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            // Chỉ log các thông báo lỗi và cảnh báo, hoặc khi có exception
            if (logLevel < LogLevel.Warning && exception == null)
            {
                string message = formatter(state, exception);
                
                // Kiểm tra xem message có chứa bất kỳ từ khóa nào trong danh sách bỏ qua không
                foreach (var phrase in _ignoredPhrases)
                {
                    if (message.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                    {
                        return; // Bỏ qua log này
                    }
                }
                
                // Bỏ qua tất cả các log Debug và Trace
                if (logLevel == LogLevel.Debug || logLevel == LogLevel.Trace)
                {
                    return;
                }
                
                // Bỏ qua các log từ một số namespace cụ thể
                if (_categoryName.StartsWith("Microsoft.") || 
                    _categoryName.StartsWith("System.") ||
                    _categoryName.StartsWith("Microsoft.AspNetCore.StaticFiles") ||
                    _categoryName.Contains(".Diagnostics."))
                {
                    return;
                }
            }
            
            // Chuyển tiếp log cho logger bên trong
            _innerLogger.Log(logLevel, eventId, state, exception, formatter);
        }
        
        public void Dispose()
        {
            if (_innerLogger is IDisposable disposableLogger)
            {
                disposableLogger.Dispose();
            }
        }
    }
} 