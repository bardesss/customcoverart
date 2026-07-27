using System.Collections.Concurrent;

namespace CustomCoverArt.Services;

/// <summary>
/// Service for rate limiting API requests
/// </summary>
public interface IRateLimitingService
{
    bool IsAllowed(string clientId, string endpoint, int maxRequests = 10, TimeSpan? timeWindow = null);
    void RecordRequest(string clientId, string endpoint);
    void ClearExpiredEntries();
}

/// <summary>
/// Implementation of rate limiting service using sliding window
/// </summary>
public class RateLimitingService : IRateLimitingService
{
    private readonly ConcurrentDictionary<string, List<DateTime>> _requestHistory = new();
    private readonly object _lockObject = new object();
    private readonly Timer _cleanupTimer;

    public RateLimitingService()
    {
        // Clean up expired entries every 5 minutes. The Timer callback needs a
        // (object? state) signature, so wrap the parameterless public method.
        _cleanupTimer = new Timer(_ => ClearExpiredEntries(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    public bool IsAllowed(string clientId, string endpoint, int maxRequests = 10, TimeSpan? timeWindow = null)
    {
        var window = timeWindow ?? TimeSpan.FromMinutes(1);
        var key = $"{clientId}:{endpoint}";
        
        lock (_lockObject)
        {
            if (!_requestHistory.TryGetValue(key, out var requests))
            {
                return true; // First request is always allowed
            }

            // Remove requests outside the time window
            var cutoff = DateTime.UtcNow - window;
            requests.RemoveAll(r => r < cutoff);

            return requests.Count < maxRequests;
        }
    }

    public void RecordRequest(string clientId, string endpoint)
    {
        var key = $"{clientId}:{endpoint}";
        
        lock (_lockObject)
        {
            if (!_requestHistory.TryGetValue(key, out var requests))
            {
                requests = new List<DateTime>();
                _requestHistory[key] = requests;
            }

            requests.Add(DateTime.UtcNow);
        }
    }

    public void ClearExpiredEntries()
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromHours(1); // Keep 1 hour of history
        
        lock (_lockObject)
        {
            var keysToRemove = new List<string>();
            
            foreach (var kvp in _requestHistory)
            {
                kvp.Value.RemoveAll(r => r < cutoff);
                if (kvp.Value.Count == 0)
                {
                    keysToRemove.Add(kvp.Key);
                }
            }

            foreach (var key in keysToRemove)
            {
                _requestHistory.TryRemove(key, out _);
            }
        }
    }
}

