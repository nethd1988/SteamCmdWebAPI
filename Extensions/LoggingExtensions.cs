using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using SteamCmdWebAPI.Filters;

namespace SteamCmdWebAPI.Extensions
{
    /// <summary>
    /// Các extension method để tối ưu logging
    /// </summary>
    public static class LoggingExtensions
    {
        /// <summary>
        /// Ghi log cảnh báo chỉ khi ứng dụng không chạy ở chế độ Release
        /// </summary>
        [Conditional("DEBUG")]
        public static void LogDebugOnly(this ILogger logger, string message, params object[] args)
        {
            logger.LogDebug(message, args);
        }

        /// <summary>
        /// Ghi log thông tin chỉ khi thực sự cần thiết (tránh ghi log quá nhiều)
        /// </summary>
        public static void LogImportantInfo(this ILogger logger, string message, params object[] args)
        {
            logger.LogInformation(message, args);
        }
        
        /// <summary>
        /// Ghi log với mức độ khẩn cấp, chỉ sử dụng cho các sự kiện quan trọng
        /// </summary>
        public static void LogCriticalEvent(this ILogger logger, string message, params object[] args)
        {
            logger.LogCritical(message, args);
        }
        
        /// <summary>
        /// Ghi log bắt đầu/kết thúc phương thức, chỉ sử dụng trong chế độ Debug
        /// </summary>
        [Conditional("DEBUG")]
        public static void LogMethodExecution(this ILogger logger, string methodName, bool isStart = true)
        {
            if (isStart)
                logger.LogDebug($"BEGIN Method: {methodName}");
            else
                logger.LogDebug($"END Method: {methodName}");
        }
        
        /// <summary>
        /// Ghi log hiệu suất để giúp phát hiện các vấn đề về performance
        /// </summary>
        public static IDisposable LogPerformance(this ILogger logger, string operationName, 
            [CallerMemberName] string callerName = "")
        {
            return new PerformanceLogger(logger, operationName, callerName);
        }
        
        /// <summary>
        /// Thêm LoggingFilter để lọc các log không cần thiết
        /// </summary>
        public static ILoggingBuilder UseLogFilter(this ILoggingBuilder builder)
        {
            builder.Services.AddSingleton<ILoggerProvider, LoggingFilterProvider>(
                serviceProvider => 
                {
                    // Lấy provider đầu tiên từ dịch vụ
                    var providers = serviceProvider.GetServices<ILoggerProvider>();
                    foreach (var provider in providers)
                    {
                        if (provider is not LoggingFilterProvider)
                        {
                            return new LoggingFilterProvider(provider);
                        }
                    }
                    
                    return null;
                });
            
            return builder;
        }
    }
    
    /// <summary>
    /// Helper class để đo thời gian thực thi
    /// </summary>
    internal class PerformanceLogger : IDisposable
    {
        private readonly ILogger _logger;
        private readonly string _operationName;
        private readonly string _callerName;
        private readonly Stopwatch _stopwatch;
        
        public PerformanceLogger(ILogger logger, string operationName, string callerName)
        {
            _logger = logger;
            _operationName = operationName;
            _callerName = callerName;
            _stopwatch = Stopwatch.StartNew();
        }
        
        public void Dispose()
        {
            _stopwatch.Stop();
            var elapsedMs = _stopwatch.ElapsedMilliseconds;
            
            // Chỉ log các thao tác mất nhiều thời gian (> 1 giây)
            if (elapsedMs > 1000)
            {
                _logger.LogWarning($"PERFORMANCE: {_callerName}.{_operationName} took {elapsedMs} ms to execute");
            }
        }
    }
} 