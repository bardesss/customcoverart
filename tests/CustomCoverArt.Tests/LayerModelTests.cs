using CustomCoverArt.Common;
using CustomCoverArt.Models;
using NSubstitute;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace CustomCoverArt.Tests;

/// <summary>
/// Guards on client-supplied layer data: image paths are honoured only inside the
/// plugin's own data directory, and the layer list is bounded before it reaches the
/// compositor.
/// </summary>
public class LayerModelTests
{
    private static CoverDocument BlackDoc()
    {
        var doc = new CoverDocument { Canvas = new CanvasSettings { Width = 120, Height = 120, Format = "png" } };
        doc.Background.Source = "none";
        doc.Background.DimColor = "#000000";
        doc.Background.Dim = 0f;
        return doc;
    }

    private static string WriteRedPng(string dir)
    {
        System.IO.Directory.CreateDirectory(dir);
        var p = System.IO.Path.Combine(dir, $"logo_{System.Guid.NewGuid():N}.png");
        using var img = new Image<Rgba32>(40, 40, Color.Red);
        img.SaveAsPng(p);
        return p;
    }

    [Fact]
    public async System.Threading.Tasks.Task OutsideSandboxImagePath_IsIgnored_NoThrow()
    {
        var svc = AnimationTestHost.NewCoverArtService();
        var doc = BlackDoc();
        doc.Layers.Add(new CoverLayer { Type = "image", ImagePath = "/etc/passwd", X = 0.5f, Y = 0.5f, Width = 0.5f, Height = 0.5f });

        var path = await svc.GenerateFromDocumentAsync(doc); // must succeed; unsafe path ignored
        Assert.True(System.IO.File.Exists(path));
        Assert.Equal(string.Empty, doc.Layers[0].ImagePath);
        try { System.IO.File.Delete(path); } catch { }
    }

    /// <summary>
    /// The real leak this guards: a readable image that exists but lives OUTSIDE the
    /// plugin dir must not end up composited into a cover the user can then download.
    /// </summary>
    [Fact]
    public async System.Threading.Tasks.Task OutsideSandboxImage_IsNotDrawn()
    {
        var (svc, dataDir) = AnimationTestHost.New();
        var outside = WriteRedPng(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cca_outside_" + System.Guid.NewGuid().ToString("N")));
        try
        {
            var doc = BlackDoc();
            doc.Layers.Add(new CoverLayer { Type = "image", ImagePath = outside, X = 0.5f, Y = 0.5f, Width = 0.8f, Height = 0.8f });

            var path = await svc.GenerateFromDocumentAsync(doc);
            using var result = Image.Load<Rgba32>(path);
            Assert.True(result[60, 60].R < 20, "Out-of-sandbox logo must not be composited.");
            try { System.IO.File.Delete(path); } catch { }
        }
        finally
        {
            try { System.IO.Directory.Delete(System.IO.Path.GetDirectoryName(outside)!, true); } catch { }
            try { System.IO.Directory.Delete(dataDir, true); } catch { }
        }
    }

    /// <summary>Positive control: the same PNG inside the sandbox IS drawn.</summary>
    [Fact]
    public async System.Threading.Tasks.Task InsideSandboxImage_IsDrawn()
    {
        var (svc, dataDir) = AnimationTestHost.New();
        var paths = Substitute.For<MediaBrowser.Common.Configuration.IApplicationPaths>();
        paths.DataPath.Returns(dataDir);
        try
        {
            var inside = WriteRedPng(PluginPaths.Uploads(paths));
            var doc = BlackDoc();
            doc.Layers.Add(new CoverLayer { Type = "image", ImagePath = inside, X = 0.5f, Y = 0.5f, Width = 0.8f, Height = 0.8f });

            var path = await svc.GenerateFromDocumentAsync(doc);
            using var result = Image.Load<Rgba32>(path);
            Assert.True(result[60, 60].R > 200, "In-sandbox logo should be composited.");
            try { System.IO.File.Delete(path); } catch { }
        }
        finally
        {
            try { System.IO.Directory.Delete(dataDir, true); } catch { }
        }
    }

    /// <summary>
    /// A document with hundreds of layers is a cheap way to burn render time; the
    /// service truncates the list before compositing.
    /// </summary>
    [Fact]
    public async System.Threading.Tasks.Task ExcessLayers_AreDropped()
    {
        var svc = AnimationTestHost.NewCoverArtService();
        var doc = BlackDoc();
        for (var i = 0; i < 250; i++)
        {
            doc.Layers.Add(new CoverLayer { Type = "text", Content = "x", Size = 0.05f });
        }

        var path = await svc.GenerateFromDocumentAsync(doc);
        Assert.Equal(40, doc.Layers.Count);
        try { System.IO.File.Delete(path); } catch { }
    }
}
