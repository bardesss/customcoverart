namespace CustomCoverArt.Services;

public interface IRateLimitingService
{
    /// <summary>
    /// Atomically checks the caller's window and, if under <paramref name="maxRequests"/>,
    /// records this request. False means the caller is over the limit, and nothing is recorded.
    /// </summary>
    bool TryAcquire(string clientId, string endpoint, int maxRequests, TimeSpan timeWindow);

    void ClearExpiredEntries();
}

public class RateLimitingService : IRateLimitingService
{
    // Every access happens under _lockObject, so a plain Dictionary is enough. The old
    // ConcurrentDictionary suggested a thread safety that the separate check-then-record
    // calls did not actually have.
    private readonly Dictionary<string, List<DateTime>> _requestHistory = new();
    private readonly object _lockObject = new();
    private readonly Timer _cleanupTimer;

    public RateLimitingService()
    {
        // Clean up expired entries every 5 minutes. The Timer callback needs a
        // (object? state) signature, so wrap the parameterless public method.
        _cleanupTimer = new Timer(_ => ClearExpiredEntries(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    public bool TryAcquire(string clientId, string endpoint, int maxRequests, TimeSpan timeWindow)
    {
        var key = $"{clientId}:{endpoint}";
        var now = DateTime.UtcNow;

        lock (_lockObject)
        {
            if (!_requestHistory.TryGetValue(key, out var requests))
            {
                requests = new List<DateTime>();
                _requestHistory[key] = requests;
            }

            var cutoff = now - timeWindow;
            requests.RemoveAll(r => r < cutoff);

            if (requests.Count >= maxRequests)
            {
                return false;
            }

            requests.Add(now);
            return true;
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
                _requestHistory.Remove(key);
            }
        }
    }
}
