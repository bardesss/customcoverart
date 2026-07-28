using System;
using System.IO;
using System.Threading.Tasks;
using CustomCoverArt.Common;
using CustomCoverArt.Models;
using CustomCoverArt.Services;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace CustomCoverArt.Tests;

public class PathSandboxTests
{
    private static IApplicationPaths PathsWith(string dataPath)
    {
        var p = Substitute.For<IApplicationPaths>();
        p.DataPath.Returns(dataPath);
        return p;
    }

    [Fact]
    public void AcceptsPathInsideDataDir()
    {
        var paths = PathsWith(Path.Combine(Path.GetTempPath(), "jfdata"));
        var inside = Path.Combine(PluginPaths.Uploads(paths), "poster.png");
        Assert.True(PluginPaths.IsInsideBase(paths, inside));
    }

    [Fact]
    public void RejectsArbitrarySystemPath()
    {
        var paths = PathsWith(Path.Combine(Path.GetTempPath(), "jfdata"));
        Assert.False(PluginPaths.IsInsideBase(paths, @"C:\Windows\system32\config\SAM"));
        Assert.False(PluginPaths.IsInsideBase(paths, "/etc/passwd"));
    }

    [Fact]
    public void RejectsTraversalEscape()
    {
        var paths = PathsWith(Path.Combine(Path.GetTempPath(), "jfdata"));
        var escape = Path.Combine(PluginPaths.Base(paths), "..", "..", "secret.png");
        Assert.False(PluginPaths.IsInsideBase(paths, escape));
    }

    [Fact]
    public void RejectsEmpty()
    {
        var paths = PathsWith(Path.Combine(Path.GetTempPath(), "jfdata"));
        Assert.False(PluginPaths.IsInsideBase(paths, ""));
    }

    [Fact]
    public void BackupsPathIsInsideDataDir()
    {
        var paths = PathsWith(Path.Combine(Path.GetTempPath(), "jfdata"));
        var backup = Path.Combine(PluginPaths.Backups(paths), "abc", "original.png");
        Assert.True(PluginPaths.IsInsideBase(paths, backup));
    }
}

public class UploadValidationTests
{
    private static ImageProcessingService NewService()
    {
        var loc = Substitute.For<ILocalizationService>();
        loc.GetString(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => (string)ci[0]);
        return new ImageProcessingService(loc);
    }

    private static IFormFile FileFrom(byte[] bytes, string name) =>
        new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", name);

    [Fact]
    public async Task AcceptsRealPng()
    {
        using var img = new Image<Rgba32>(16, 16);
        using var ms = new MemoryStream();
        img.SaveAsPng(ms);

        var result = await NewService().ValidateFileAsync(FileFrom(ms.ToArray(), "cover.png"));
        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task RejectsExecutableDisguisedAsPng()
    {
        // MZ header (Windows executable) with a .png name.
        var exe = new byte[512];
        exe[0] = 0x4D; exe[1] = 0x5A;
        var result = await NewService().ValidateFileAsync(FileFrom(exe, "cover.png"));
        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task RejectsHtmlPolyglotDisguisedAsPng()
    {
        // The realistic attack: an HTML/JS payload with an image extension.
        var html = System.Text.Encoding.UTF8.GetBytes(
            "<!DOCTYPE html><html><body><script>alert('xss')</script></body></html>");
        var result = await NewService().ValidateFileAsync(FileFrom(html, "cover.png"));
        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData("normal.png", true)]
    [InlineData("weird<name>.png", false)]
    [InlineData("double.ext.png", false)]
    public void FileNameValidation(string name, bool expected)
    {
        Assert.Equal(expected, ImageProcessingService.IsValidImageFormat(name));
    }
}

public class CoverArtGenerationTests
{
    [Fact]
    public async Task GeneratesValidPngWithGradientAndText()
    {
        var img = Substitute.For<IImageProcessingService>();
        var paths = Substitute.For<IApplicationPaths>();
        paths.DataPath.Returns(Path.Combine(Path.GetTempPath(), "cca_test_" + Guid.NewGuid().ToString("N")));

        var service = new CoverArtService(img, paths, Substitute.For<ILoggingService>());

        var settings = new CoverArtSettings
        {
            Title = "Movies",
            OutputFormat = "png",
            ExportWidth = 400,
            ExportHeight = 300,
            DimensionPreset = "cover",
            TextShadow = true,
            TextOutline = true,
            BackgroundGradient = new GradientSettings
            {
                IsEnabled = true,
                Type = GradientType.Linear,
                StartColor = "#112233",
                EndColor = "#aabbcc"
            }
        };

        var outputPath = await service.GenerateCoverArtAsync(settings);

        Assert.True(File.Exists(outputPath), "output file should exist");
        using var produced = Image.Load(outputPath);
        Assert.Equal(400, produced.Width);
        Assert.Equal(300, produced.Height);
    }

    [Fact]
    public async Task BadHexColorDoesNotThrow()
    {
        // Malformed colours from the API must not abort the render.
        var img = Substitute.For<IImageProcessingService>();
        var paths = Substitute.For<IApplicationPaths>();
        paths.DataPath.Returns(Path.Combine(Path.GetTempPath(), "cca_test_" + Guid.NewGuid().ToString("N")));

        var service = new CoverArtService(img, paths, Substitute.For<ILoggingService>());
        var settings = new CoverArtSettings
        {
            Title = "Hi",
            OutputFormat = "png",
            ExportWidth = 200,
            ExportHeight = 200,
            DimColor = "not-a-color",
            TextColor = "#zzzzzz"
        };

        var outputPath = await service.GenerateCoverArtAsync(settings);
        Assert.True(File.Exists(outputPath));
    }
}

public class PluginMetadataTests
{
    [Fact]
    public void ConfigPageAndResourcesAreEmbedded()
    {
        var asm = typeof(CustomCoverArt.Plugin).Assembly;
        var resources = asm.GetManifestResourceNames();

        Assert.Contains("CustomCoverArt.Configuration.configPage.html", resources);
        Assert.Contains("CustomCoverArt.Resources.en.json", resources);
        Assert.Contains("CustomCoverArt.Resources.fonts.NotoSans-Regular.ttf", resources);
        Assert.Contains("CustomCoverArt.Resources.fonts.NotoSans-Bold.ttf", resources);
    }

    [Fact]
    public void PluginImplementsRequiredJellyfinContracts()
    {
        var t = typeof(CustomCoverArt.Plugin);

        Assert.Contains(t.GetInterfaces(), i => i.Name == "IHasWebPages");
        Assert.Contains(t.GetInterfaces(), i => i.Name == "IPlugin");

        var derivesFromBasePlugin = false;
        for (var b = t.BaseType; b != null; b = b.BaseType)
        {
            if (b.Name.StartsWith("BasePlugin")) { derivesFromBasePlugin = true; break; }
        }

        Assert.True(derivesFromBasePlugin, "Plugin must derive from BasePlugin so Jellyfin loads it");
    }

    [Fact]
    public void ServiceRegistratorIsPresent()
    {
        var t = typeof(CustomCoverArt.Plugin).Assembly.GetType("CustomCoverArt.PluginServiceRegistrator");
        Assert.NotNull(t);
        Assert.Contains(t!.GetInterfaces(), i => i.Name == "IPluginServiceRegistrator");
    }
}
