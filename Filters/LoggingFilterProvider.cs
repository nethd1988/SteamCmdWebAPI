using Microsoft.Extensions.Logging;
using System;

namespace SteamCmdWebAPI.Filters
{
    public class LoggingFilterProvider : ILoggerProvider
    {
        private readonly ILoggerProvider _innerProvider;
        
        public LoggingFilterProvider(ILoggerProvider innerProvider)
        {
            _innerProvider = innerProvider;
        }
        
        public ILogger CreateLogger(string categoryName)
        {
            var innerLogger = _innerProvider.CreateLogger(categoryName);
            return new LoggingFilter(innerLogger, categoryName);
        }
        
        public void Dispose()
        {
            _innerProvider.Dispose();
        }
    }
} 