using System.Net.Sockets;
using CustomCoverArt.Exceptions;

namespace CustomCoverArt.Services;

/// <summary>
/// Service for handling retry operations with exponential backoff
/// </summary>
public interface IRetryService
{
    Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> operation, int maxRetries = 3, TimeSpan? baseDelay = null);
    Task ExecuteWithRetryAsync(Func<Task> operation, int maxRetries = 3, TimeSpan? baseDelay = null);
}

/// <summary>
/// Implementation of retry service with exponential backoff
/// </summary>
public class RetryService : IRetryService
{
    private readonly ILoggingService _loggingService;

    public RetryService(ILoggingService loggingService)
    {
        _loggingService = loggingService;
    }

    public async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> operation, int maxRetries = 3, TimeSpan? baseDelay = null)
    {
        var delay = baseDelay ?? TimeSpan.FromMilliseconds(100);
        var lastException = (Exception?)null;

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (Exception ex) when (IsRetryableException(ex) && attempt < maxRetries)
            {
                lastException = ex;
                var waitTime = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * Math.Pow(2, attempt));
                
                _loggingService.LogWarning("Operation failed (attempt {Attempt}/{MaxRetries}), retrying in {WaitTime}ms: {Error}", 
                    attempt + 1, maxRetries + 1, waitTime.TotalMilliseconds, ex.Message);
                
                await Task.Delay(waitTime);
            }
            catch (Exception ex)
            {
                _loggingService.LogError("Operation failed after {Attempts} attempts: {Error}", attempt + 1, ex.Message);
                throw;
            }
        }

        // This should never be reached, but just in case
        throw lastException ?? new InvalidOperationException("Operation failed after all retry attempts");
    }

    public async Task ExecuteWithRetryAsync(Func<Task> operation, int maxRetries = 3, TimeSpan? baseDelay = null)
    {
        await ExecuteWithRetryAsync(async () =>
        {
            await operation();
            return true; // Dummy return value
        }, maxRetries, baseDelay);
    }

    private static bool IsRetryableException(Exception ex)
    {
        return ex switch
        {
            IOException => true,
            UnauthorizedAccessException => true,
            TimeoutException => true,
            HttpRequestException => true,
            SocketException => true,
            CustomCoverArtException ccaEx when ccaEx.ErrorCode == "FILE_OPERATION_ERROR" => true,
            _ => false
        };
    }
}

