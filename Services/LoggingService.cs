using Microsoft.Extensions.Logging;

namespace CustomCoverArt.Services;

/// <summary>
/// Service for logging operations
/// </summary>
public interface ILoggingService
{
    void LogInformation(string message, params object[] args);
    void LogWarning(string message, params object[] args);

    // Structured-logging style (values follow the message).
    void LogError(string message, params object[] args);

    // Exception overload. No default on the exception parameter so a single-arg
    // call is unambiguous and resolves to the overload above.
    void LogError(string message, Exception? exception, params object[] args);

    void LogDebug(string message, params object[] args);
}

/// <summary>
/// Implementation of logging service using Microsoft.Extensions.Logging
/// </summary>
public class LoggingService : ILoggingService
{
    private readonly ILogger<LoggingService> _logger;

    public LoggingService(ILogger<LoggingService> logger)
    {
        _logger = logger;
    }

    public void LogInformation(string message, params object[] args)
    {
        _logger.LogInformation(message, args);
    }

    public void LogWarning(string message, params object[] args)
    {
        _logger.LogWarning(message, args);
    }

    public void LogError(string message, params object[] args)
    {
        _logger.LogError(message, args);
    }

    public void LogError(string message, Exception? exception, params object[] args)
    {
        if (exception != null)
        {
            _logger.LogError(exception, message, args);
        }
        else
        {
            _logger.LogError(message, args);
        }
    }

    public void LogDebug(string message, params object[] args)
    {
        _logger.LogDebug(message, args);
    }
}
