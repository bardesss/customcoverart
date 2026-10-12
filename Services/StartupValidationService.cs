using CustomCoverArt.Common;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace CustomCoverArt.Services;

public interface IStartupValidationService
{
    /// <summary>
    /// Whether the plugin can write to its data directory. A success is cached for the life
    /// of the process. A failure is re-checked on the next call, so fixing permissions
    /// recovers without a restart.
    /// </summary>
    Task<bool> IsPluginReadyAsync();
}

public class StartupValidationService : IStartupValidationService
{
    private readonly ILogger<StartupValidationService> _logger;
    private readonly IApplicationPaths _applicationPaths;
    private volatile bool _ready;

    public StartupValidationService(ILogger<StartupValidationService> logger, IApplicationPaths applicationPaths)
    {
        _logger = logger;
        _applicationPaths = applicationPaths;
    }

    public async Task<bool> IsPluginReadyAsync()
    {
        if (_ready)
        {
            return true;
        }

        // One probe of the one directory everything else lives under. Details go to the
        // server log only, never into the HTTP response.
        var dir = PluginPaths.Base(_applicationPaths);
        var probe = Path.Combine(dir, $".write-check-{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(probe, "ok").ConfigureAwait(false);
            File.Delete(probe);
            _ready = true;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Plugin data directory is not writable: {Directory}", dir);
            try
            {
                if (File.Exists(probe))
                {
                    File.Delete(probe);
                }
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // Best effort; the probe name is unique, so a leftover never collides.
            }

            return false;
        }
    }
}
