using System;
using System.IO;
using CustomCoverArt.Models;
using CustomCoverArt.Services.Rendering;
using Xunit;

namespace CustomCoverArt.Tests;

[Collection("FontStoreState")]
public class CustomFontCacheTests
{
    private static string CopyBundledFont()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cca_font_{Guid.NewGuid():N}.ttf");
        using var src = typeof(Plugin).Assembly.GetManifestResourceStream("CustomCoverArt.Resources.fonts.NotoSans-Regular.ttf")!;
        using var dst = File.Create(path);
        src.CopyTo(dst);
        return path;
    }

    /// <summary>
    /// A custom font is parsed once per file version, not once per layer per render. A changed
    /// file (new mtime) is re-read, so re-uploading over the same path is picked up.
    /// </summary>
    [Fact]
    public void CustomFont_IsParsedOncePerFileVersion()
    {
        var path = CopyBundledFont();
        try
        {
            var layer = new CoverLayer { FontPath = path };
            Assert.False(FontStore.IsCustomFontCached(path));

            var first = FontStore.Resolve(layer);
            Assert.True(FontStore.IsCustomFontCached(path));
            Assert.Same(first, FontStore.Resolve(layer));

            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));
            Assert.False(FontStore.IsCustomFontCached(path));

            var second = FontStore.Resolve(layer);
            Assert.True(FontStore.IsCustomFontCached(path));
            Assert.NotSame(first, second);
            Assert.NotSame(FontStore.Bundled(FontWeight.Normal), second);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void BrokenCustomFont_FallsBackToBundled()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cca_font_{Guid.NewGuid():N}.ttf");
        File.WriteAllText(path, "not a font");
        try
        {
            var face = FontStore.Resolve(new CoverLayer { FontPath = path });
            Assert.Equal("Noto Sans", face.FamilyName);
            Assert.False(FontStore.IsCustomFontCached(path));
        }
        finally { File.Delete(path); }
    }
}
