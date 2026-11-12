using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace CustomCoverArt.Services.Rendering;

/// <summary>
/// Whether the host's SkiaSharp can be used (spec S4.4). The plugin ships no Skia binaries: it uses
/// the copy Jellyfin loads. When that copy is missing or broken (Jellyfin then runs with its
/// NullImageEncoder), every rendering or decoding action refuses with <see cref="UnavailableMessage"/>
/// and the rest of the plugin keeps working.
/// </summary>
/// <remarks>
/// JIT isolation: no member signature or field of this type names a Skia type, and the only code that
/// does is <see cref="ProbeNative"/>, which is never inlined. A missing SkiaSharp.dll therefore surfaces
/// when <c>ProbeNative</c> is compiled, inside <see cref="Evaluate"/>'s try, as a catchable exception,
/// not when this type or a caller is loaded. Callers follow the same rule: they check
/// <see cref="IsAvailable"/> and only then call a separate method that touches Skia.
/// </remarks>
public static class SkiaRuntime
{
    /// <summary>What every guarded action answers while the engine is unavailable.</summary>
    internal const string UnavailableMessage = "The server's image engine (SkiaSharp) is not available on this Jellyfin installation.";

    private static readonly AsyncLocal<Func<bool>?> Override = new();

    private static ILogger _log = NullLogger.Instance;

    private static readonly Lazy<(bool Available, string? Version)> State = CreateState(ProbeNative, () => Volatile.Read(ref _log));

    /// <summary>True once the one-time probe of the host's SkiaSharp has succeeded. Lazy and thread-safe.</summary>
    public static bool IsAvailable => Override.Value is { } forced ? forced() : State.Value.Available;

    /// <summary>The native libSkiaSharp version (for example "119.0"), or null when unavailable.</summary>
    public static string? NativeVersion => IsAvailable ? State.Value.Version : null;

    /// <summary>
    /// Forces <see cref="IsAvailable"/> for tests. Scoped to the calling async flow, so a test that
    /// switches the engine off does not switch it off for tests running in parallel.
    /// </summary>
    internal static Func<bool>? OverrideForTests
    {
        get => Override.Value;
        set => Override.Value = value;
    }

    /// <summary>
    /// Where the probe's one Warning goes: the first real logger offered (from the cover service,
    /// which DI builds before any guarded controller action runs). Later calls are ignored.
    /// </summary>
    internal static void UseLogger(ILogger logger)
        => Interlocked.CompareExchange(ref _log, logger, NullLogger.Instance);

    /// <summary>Runs <paramref name="probe"/> once and caches the outcome, failures included.</summary>
    internal static Lazy<(bool Available, string? Version)> CreateState(Func<string?> probe, Func<ILogger> log)
        => new(() => Evaluate(probe, log()), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Runs the probe. Every way the host's SkiaSharp can be missing or unusable becomes
    /// <c>(false, null)</c> and one Warning; nothing is thrown.
    /// </summary>
    internal static (bool Available, string? Version) Evaluate(Func<string?> probe, ILogger log)
    {
        try
        {
            return (true, probe());
        }
        catch (Exception ex) when (ex is FileNotFoundException
            or FileLoadException
            or DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException
            or TypeInitializationException
            or InvalidOperationException)
        {
            log.LogWarning(
                "Image engine unavailable on {OS} {Arch}: {Type}: {Message}. Cover rendering is disabled; the rest of the server is not affected.",
                RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture,
                ex.GetType().Name,
                ex.Message);
            return (false, null);
        }
    }

    /// <summary>
    /// The same check Jellyfin's own startup makes: the native library matches the managed one, and a
    /// native call works. The only member of this type that names Skia types (see the remarks above).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string? ProbeNative()
    {
        SkiaSharpVersion.CheckNativeLibraryCompatible(true);
        _ = SKPMColor.PreMultiply(SKColors.Black);
        return SkiaSharpVersion.Native.ToString();
    }
}
