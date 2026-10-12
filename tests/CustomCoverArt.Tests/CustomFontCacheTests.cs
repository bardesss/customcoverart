using System;
using System.IO;
using CustomCoverArt.Models;
using CustomCoverArt.Services;
using Xunit;

namespace CustomCoverArt.Tests;

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
            Assert.False(DocumentRenderer.IsCustomFontCached(path));

            DocumentRenderer.CreateFont(layer, 24f);
            Assert.True(DocumentRenderer.IsCustomFontCached(path));

            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));
            Assert.False(DocumentRenderer.IsCustomFontCached(path));

            var font = DocumentRenderer.CreateFont(layer, 24f);
            Assert.True(DocumentRenderer.IsCustomFontCached(path));
            Assert.Equal(24f, font.Size);
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
            var font = DocumentRenderer.CreateFont(new CoverLayer { FontPath = path }, 20f);
            Assert.Equal("Noto Sans", font.Family.Name);
            Assert.False(DocumentRenderer.IsCustomFontCached(path));
        }
        finally { File.Delete(path); }
    }
}
