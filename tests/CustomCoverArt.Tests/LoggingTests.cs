using System;
using System.Linq;
using CustomCoverArt.Controllers;
using CustomCoverArt.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CustomCoverArt.Tests;

/// <summary>
/// Log lines must carry the real category (CustomCoverArt.Services.CoverArtService, …),
/// not the wrapper's. Jellyfin's log filter and anyone grepping the server log key on it.
/// </summary>
public class LoggingTests
{
    [Theory]
    [InlineData(typeof(CoverArtService))]
    [InlineData(typeof(LibraryDetectionService))]
    [InlineData(typeof(LocalizationService))]
    [InlineData(typeof(MediaItemService))]
    [InlineData(typeof(StartupValidationService))]
    [InlineData(typeof(CustomCoverArtController))]
    public void TakesALoggerCategorisedByItsOwnType(Type type)
    {
        var expected = typeof(ILogger<>).MakeGenericType(type);
        var parameterTypes = type.GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType);

        Assert.Contains(expected, parameterTypes);
    }

    [Fact]
    public void TheLoggingWrapperIsGone()
    {
        var asm = typeof(Plugin).Assembly;
        Assert.Null(asm.GetType("CustomCoverArt.Services.ILoggingService"));
        Assert.Null(asm.GetType("CustomCoverArt.Services.LoggingService"));
    }
}
