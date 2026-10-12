using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Xml.Serialization;
using CustomCoverArt.Configuration;
using CustomCoverArt.Controllers;
using CustomCoverArt.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Xunit;

namespace CustomCoverArt.Tests;

public class UploadLimitAndDeadCodeTests
{
    private static ImageProcessingService ServiceWithLimit(int mb)
    {
        var loc = Substitute.For<ILocalizationService>();
        loc.GetString(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => (string)ci[0]);
        return new ImageProcessingService(loc, () => mb);
    }

    private static IFormFile ZeroFile(int bytes, string name) =>
        new FormFile(new MemoryStream(new byte[bytes]), 0, bytes, "file", name);

    /// <summary>MaxUploadSizeMb was never read; 5 MB was hard-coded.</summary>
    [Fact]
    public async Task UploadLimit_ComesFromConfiguration()
    {
        var oneAndAHalfMb = 1536 * 1024;

        var tight = await ServiceWithLimit(1).ValidateFileAsync(ZeroFile(oneAndAHalfMb, "bg.png"));
        Assert.Equal("errors.file_too_large", tight.ErrorMessage);

        var roomy = await ServiceWithLimit(2).ValidateFileAsync(ZeroFile(oneAndAHalfMb, "bg.png"));
        Assert.NotEqual("errors.file_too_large", roomy.ErrorMessage); // gets as far as the content check
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    [InlineData(5, 5)]
    [InlineData(500, 50)]
    public void UploadLimit_IsClampedTo1Through50(int configured, int expected)
    {
        Assert.Equal(expected, ImageProcessingService.EffectiveMaxUploadMb(configured));
    }

    [Fact]
    public void UnusedConfigProperties_AreGone_AndOldConfigFilesStillLoad()
    {
        Assert.Null(typeof(PluginConfiguration).GetProperty("DefaultTitle"));
        Assert.Null(typeof(PluginConfiguration).GetProperty("Language"));

        // A config file written by 4.0.1.0 still carries both elements.
        const string xml = "<?xml version=\"1.0\"?><PluginConfiguration xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">"
            + "<DefaultTitle>Movies</DefaultTitle><MaxUploadSizeMb>12</MaxUploadSizeMb><Language>nl</Language><Templates /></PluginConfiguration>";
        var cfg = (PluginConfiguration)new XmlSerializer(typeof(PluginConfiguration)).Deserialize(new StringReader(xml))!;

        Assert.Equal(12, cfg.MaxUploadSizeMb);
    }

    [Fact]
    public void AutoFormatOptimizer_IsGone()
    {
        Assert.Null(typeof(IImageProcessingService).GetMethod("DetermineOptimalFormatAsync"));
    }

    /// <summary>The client still sends the type as a path segment, so the route keeps it; the handler ignores it.</summary>
    [Fact]
    public void RestoreOriginal_KeepsTheRoute_ButTakesOnlyTheId()
    {
        var method = typeof(CustomCoverArtController).GetMethod(nameof(CustomCoverArtController.RestoreOriginal))!;

        Assert.Equal(new[] { "id" }, method.GetParameters().Select(p => p.Name));
        Assert.Equal("targets/{type}/{id}/restore", method.GetCustomAttribute<HttpPostAttribute>()!.Template);
    }
}
