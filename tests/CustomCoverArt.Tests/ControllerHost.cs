using System;
using System.IO;
using CustomCoverArt.Controllers;
using CustomCoverArt.Services;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CustomCoverArt.Tests;

internal static class ControllerHost
{
    public static CustomCoverArtController New(
        ICoverArtService? coverArt = null,
        ILibraryDetectionService? library = null,
        IRateLimitingService? rateLimiter = null,
        IStartupValidationService? startup = null,
        IMediaItemService? media = null)
    {
        var loc = Substitute.For<ILocalizationService>();
        loc.GetString(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => (string)ci[0]);
        var paths = Substitute.For<IApplicationPaths>();
        paths.DataPath.Returns(Path.Combine(Path.GetTempPath(), "cca_ctl_" + Guid.NewGuid().ToString("N")));

        var controller = new CustomCoverArtController(
            library ?? Substitute.For<ILibraryDetectionService>(),
            coverArt ?? Substitute.For<ICoverArtService>(),
            new ImageProcessingService(loc),
            media ?? Substitute.For<IMediaItemService>(),
            Substitute.For<IUserContextService>(),
            NullLogger<CustomCoverArtController>.Instance,
            rateLimiter ?? new RateLimitingService(),
            startup ?? Substitute.For<IStartupValidationService>(),
            loc,
            paths);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }
}
