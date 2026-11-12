using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CustomCoverArt.Services.Rendering;

/// <summary>
/// Where the static renderers (<see cref="DocumentRenderer"/>, <see cref="FontStore"/>) send their
/// warnings: the logger of whoever started the current render, carried on the calling async flow.
/// Scoped per flow rather than process-wide, so two renders at once never log into each other.
/// </summary>
internal static class RenderLog
{
    private static readonly AsyncLocal<ILogger?> Current = new();

    /// <summary>The current flow's logger, or a no-op logger outside any render scope.</summary>
    public static ILogger Logger => Current.Value ?? NullLogger.Instance;

    /// <summary>
    /// True inside a render scope. A warn-once warning raised outside one would go to the no-op logger,
    /// so callers that warn once check this before spending the warning.
    /// </summary>
    public static bool IsActive => Current.Value is not null;

    /// <summary>Routes renderer warnings on the calling flow to <paramref name="logger"/> until disposed, then restores the previous one.</summary>
    public static IDisposable Use(ILogger logger)
    {
        var previous = Current.Value;
        Current.Value = logger;
        return new Restore(previous);
    }

    private sealed class Restore : IDisposable
    {
        private readonly ILogger? _previous;
        private bool _disposed;

        public Restore(ILogger? previous) => _previous = previous;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Current.Value = _previous;
        }
    }
}
