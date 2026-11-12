using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using CustomCoverArt.Models;
using CustomCoverArt.Services.Rendering;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using Xunit;

namespace CustomCoverArt.Tests;

// FontStore keeps process-wide caches and a warn-once set, so the classes that reset or read them run one at a time.
[Collection("FontStoreState")]
public class FontStoreTests
{
    private static SKTypeface Custom(string path)
    {
        Assert.True(FontStore.TryGetCustom(path, out var face), "expected " + path + " to load");
        return face;
    }

    private static bool HasOutline(SKTypeface face, char c)
    {
        using var font = new SKFont(face, 40);
        using var path = font.GetGlyphPath(face.GetGlyph(c));
        return path is not null && path.PointCount > 0;
    }

    [Theory]
    [InlineData(FontWeight.Light, 300)]
    [InlineData(FontWeight.Normal, 400)]
    [InlineData(FontWeight.Medium, 500)]
    [InlineData(FontWeight.SemiBold, 600)]
    [InlineData(FontWeight.Bold, 700)]
    [InlineData(FontWeight.ExtraBold, 800)]
    public void Bundled_EveryWeightResolvesToItsOwnFace(FontWeight weight, int expectedCssWeight)
    {
        var face = FontStore.Bundled(weight);

        Assert.Equal("Noto Sans", face.FamilyName);
        Assert.Equal(expectedCssWeight, face.FontStyle.Weight);
        Assert.Same(face, FontStore.Bundled(weight));
        Assert.Same(face, FontStore.Resolve(new CoverLayer { Weight = weight }));
    }

    [Fact]
    public void Bundled_WeightsDifferBetweenLightRegularAndExtraBold()
    {
        var weights = new[] { FontWeight.Light, FontWeight.Normal, FontWeight.ExtraBold }
            .Select(w => FontStore.Bundled(w).FontStyle.Weight).ToArray();

        Assert.Equal(3, weights.Distinct().Count());
    }

    [Fact]
    public void Bundled_UnmappedWeightFallsBackToRegular()
    {
        Assert.Same(FontStore.Bundled(FontWeight.Normal), FontStore.Bundled((FontWeight)950));
    }

    [Fact]
    public void Bundled_ConcurrentFirstUseYieldsOneInstance()
    {
        var faces = Enumerable.Range(0, 64).AsParallel().Select(_ => FontStore.Bundled(FontWeight.Medium)).ToArray();

        Assert.All(faces, f => Assert.Same(faces[0], f));
    }

    [Fact]
    public void Ttc_UsesTheFirstFace()
    {
        var ttf = Custom(FontFixtures.Path("sub.ttf"));
        var ttc = Custom(FontFixtures.Path("sub.ttc"));

        Assert.NotSame(FontStore.Bundled(FontWeight.Normal), ttc);
        Assert.Equal(ttf.GlyphCount, ttc.GlyphCount);
        Assert.True(HasOutline(ttc, 'H'));
    }

    [Fact]
    public void Otf_CffOutlines_LoadAsACustomFace()
    {
        var otfBytes = FontFixtures.Bytes("sub.otf");
        Assert.Equal("OTTO", System.Text.Encoding.ASCII.GetString(otfBytes, 0, 4));
        FontFixtures.RecordOffset(otfBytes, "CFF ");

        var otf = Custom(FontFixtures.Path("sub.otf"));

        Assert.NotSame(FontStore.Bundled(FontWeight.Normal), otf);
        Assert.Equal(Custom(FontFixtures.Path("sub.ttf")).GlyphCount, otf.GlyphCount);
        Assert.True(HasOutline(otf, 'H'));
    }

    [Theory]
    [InlineData("sub.ttf")]
    [InlineData("sub.otf")]
    [InlineData("sub.ttc")]
    public void SfntSanity_AcceptsTheRealFixtures(string name)
    {
        Assert.True(SfntSanity.LooksValid(FontFixtures.Bytes(name)));
    }

    [Fact]
    public void SfntSanity_RejectsATableOutsideTheFile()
    {
        var bytes = FontFixtures.Bytes("sub.ttf");
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12 + 12, 4), (uint)bytes.Length + 1);

        Assert.False(SfntSanity.LooksValid(bytes));
    }

    [Fact]
    public void SfntSanity_TableEndingExactlyAtTheEndOfTheFile_IsStillValid()
    {
        var bytes = FontFixtures.Bytes("sub.ttf");
        var at = 12 + 8;
        var offset = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at, 4));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(at + 4, 4), (uint)bytes.Length - offset);

        Assert.True(SfntSanity.LooksValid(bytes));
    }

    [Fact]
    public void SfntSanity_OffsetPlusLengthThatWrapsAroundUint_IsRejected()
    {
        var bytes = FontFixtures.Bytes("sub.ttf");
        // 0xFFFFFFF0 + 0x20 wraps to 0x10 in 32-bit arithmetic, which would look like it fits.
        FontFixtures.SetOffsetAndLength(bytes, "cmap", 0xFFFFFFF0u, 0x20u);

        Assert.False(SfntSanity.LooksValid(bytes));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(11)]
    public void SfntSanity_RejectsAHeaderShorterThan12Bytes(int length)
    {
        Assert.False(SfntSanity.LooksValid(FontFixtures.Bytes("sub.ttf").AsSpan(0, length)));
    }

    [Fact]
    public void SfntSanity_RejectsATableDirectoryCutOffByTheEndOfTheFile()
    {
        Assert.False(SfntSanity.LooksValid(FontFixtures.Bytes("sub.ttf").AsSpan(0, 12 + 16)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    [InlineData(65535)]
    public void SfntSanity_RejectsATableCountOutsideOneToSixtyFour(int count)
    {
        var bytes = FontFixtures.Bytes("sub.ttf");
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4, 2), (ushort)count);

        Assert.False(SfntSanity.LooksValid(bytes));
    }

    [Fact]
    public void SfntSanity_RejectsACollectionWhoseFirstFaceOffsetIsOutOfRange()
    {
        var bytes = FontFixtures.Bytes("sub.ttc");
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12, 4), (uint)bytes.Length);

        Assert.False(SfntSanity.LooksValid(bytes));
    }

    [Fact]
    public void SfntSanity_RejectsACollectionWithAHugeFirstFaceOffset()
    {
        var bytes = FontFixtures.Bytes("sub.ttc");
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12, 4), uint.MaxValue);

        Assert.False(SfntSanity.LooksValid(bytes));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1000u)]
    [InlineData(uint.MaxValue)]
    public void SfntSanity_RejectsACollectionWithAnImpossibleFaceCount(uint faces)
    {
        var bytes = FontFixtures.Bytes("sub.ttc");
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), faces);

        Assert.False(SfntSanity.LooksValid(bytes));
    }

    [Theory]
    [InlineData("cmap")]
    [InlineData("head")]
    [InlineData("hhea")]
    [InlineData("hmtx")]
    [InlineData("maxp")]
    public void SfntSanity_RejectsAFontMissingARequiredTable(string table)
    {
        var bytes = FontFixtures.Bytes("sub.ttf");
        FontFixtures.SetTag(bytes, table, "zzzz");

        Assert.False(SfntSanity.LooksValid(bytes));
    }

    [Theory]
    [InlineData("glyf")]
    [InlineData("loca")]
    public void SfntSanity_RejectsATrueTypeFontWithoutBothOutlineTables(string table)
    {
        var bytes = FontFixtures.Bytes("sub.ttf");
        FontFixtures.SetTag(bytes, table, "zzzz");

        Assert.False(SfntSanity.LooksValid(bytes));
    }

    [Fact]
    public void SfntSanity_RejectsACffFontWithoutItsCffTable()
    {
        var bytes = FontFixtures.Bytes("sub.otf");
        FontFixtures.SetTag(bytes, "CFF ", "zzzz");

        Assert.False(SfntSanity.LooksValid(bytes));
    }

    [Theory]
    [InlineData("0x00010000", new byte[] { 0, 1, 0, 0 }, true)]
    [InlineData("OTTO", new byte[] { 0x4F, 0x54, 0x54, 0x4F }, true)]
    [InlineData("true", new byte[] { 0x74, 0x72, 0x75, 0x65 }, true)]
    [InlineData("ttcf", new byte[] { 0x74, 0x74, 0x63, 0x66 }, true)]
    [InlineData("wOFF", new byte[] { 0x77, 0x4F, 0x46, 0x46 }, false)]
    [InlineData("wOF2", new byte[] { 0x77, 0x4F, 0x46, 0x32 }, false)]
    [InlineData("three bytes", new byte[] { 0, 1, 0 }, false)]
    [InlineData("empty", new byte[0], false)]
    public void IsSupportedSignature_KnowsTheFourSfntMagics(string label, byte[] head, bool expected)
    {
        Assert.True(expected == FontStore.IsSupportedSignature(head), label);
    }

    [Fact]
    public void SupportedExtensions_AreTtfOtfTtcOnly_AnyCase()
    {
        Assert.Equal(new[] { ".otf", ".ttc", ".ttf" }, FontStore.SupportedExtensions.OrderBy(e => e, StringComparer.Ordinal));
        Assert.Contains(".TTF", FontStore.SupportedExtensions);
    }

    [Fact]
    public void CustomFont_GarbageAfterValidMagic_FallsBackToBundled()
    {
        using var dir = new TempFolder();
        var bytes = new byte[204];
        new Random(7).NextBytes(bytes);
        new byte[] { 0x74, 0x72, 0x75, 0x65 }.CopyTo(bytes, 0); // "true"
        var path = dir.File("garbage.ttf");
        File.WriteAllBytes(path, bytes);

        Assert.Same(FontStore.Bundled(FontWeight.Bold), FontStore.Resolve(new CoverLayer { FontPath = path, Weight = FontWeight.Bold }));
        Assert.False(FontStore.IsCustomFontCached(path));
    }

    [Fact]
    public void CustomFont_Woff2RenamedToTtf_FailsTheSignatureAndFallsBack()
    {
        using var dir = new TempFolder();
        var path = dir.Copy("sub.woff2", "disguised.ttf");

        Assert.False(FontStore.TryGetCustom(path, out _));
        Assert.Same(FontStore.Bundled(FontWeight.Normal), FontStore.Resolve(new CoverLayer { FontPath = path }));
    }

    [Fact]
    public void CustomFont_WoffRenamedToOtf_FailsTheSignatureAndFallsBack()
    {
        using var dir = new TempFolder();
        var path = dir.Copy("sub.woff", "disguised.otf");

        Assert.False(FontStore.TryGetCustom(path, out _));
    }

    [Fact]
    public void CustomFont_ValidSignatureButDamagedDirectory_FallsBackWithoutReachingSkia()
    {
        using var dir = new TempFolder();
        var bytes = FontFixtures.Bytes("sub.ttf");
        FontFixtures.SetOffsetAndLength(bytes, "glyf", 0xFFFFFFF0u, 0x20u);
        var path = dir.File("damaged.ttf");
        File.WriteAllBytes(path, bytes);

        Assert.False(FontStore.TryGetCustom(path, out _));
    }

    [Fact]
    public void CustomFont_Truncated_FallsBack()
    {
        using var dir = new TempFolder();
        var path = dir.File("cut.ttf");
        File.WriteAllBytes(path, FontFixtures.Bytes("sub.ttf").AsSpan(0, 1000).ToArray());

        Assert.False(FontStore.TryGetCustom(path, out _));
    }

    [Fact]
    public void CustomFont_MissingFile_FallsBackWithOneWarning()
    {
        var log = new CapturingLogger<FontStoreTests>();
        using var capture = FontStore.CaptureLogForTests(log);
        FontStore.ResetWarningsForTests();
        var path = Path.Combine(Path.GetTempPath(), "cca_missing_" + Guid.NewGuid().ToString("N") + ".ttf");

        for (var i = 0; i < 3; i++)
        {
            Assert.Same(FontStore.Bundled(FontWeight.Light), FontStore.Resolve(new CoverLayer { FontPath = path, Weight = FontWeight.Light }));
        }

        var warning = Assert.Single(log.Entries, e => e.Message.Contains(Path.GetFileName(path)));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("(unreadable)", warning.Message);
    }

    [Fact]
    public void Resolve_NoFontPath_UsesTheBundledFaceWithoutLogging()
    {
        var log = new CapturingLogger<FontStoreTests>();
        using var capture = FontStore.CaptureLogForTests(log);

        Assert.Same(FontStore.Bundled(FontWeight.Medium), FontStore.Resolve(new CoverLayer { FontPath = null!, Weight = FontWeight.Medium }));
        Assert.Same(FontStore.Bundled(FontWeight.Medium), FontStore.Resolve(new CoverLayer { FontPath = string.Empty, Weight = FontWeight.Medium }));
        Assert.Empty(log.Entries);
    }

    [Fact]
    public void CustomFont_IsCachedPerFileVersion_AndReloadsWhenTheWriteTimeChanges()
    {
        using var dir = new TempFolder();
        var path = dir.Copy("sub.ttf", "replace.ttf");
        var layer = new CoverLayer { FontPath = path };

        var first = FontStore.Resolve(layer);
        Assert.True(FontStore.IsCustomFontCached(path));
        Assert.Same(first, FontStore.Resolve(layer));

        // Re-upload over the same path: different bytes, newer write time.
        File.Copy(FontFixtures.Path("sub.otf"), path, overwrite: true);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));
        Assert.False(FontStore.IsCustomFontCached(path));

        var second = FontStore.Resolve(layer);
        Assert.NotSame(first, second);
        Assert.True(FontStore.IsCustomFontCached(path));

        // The replaced face is still referenced by renders already in flight: it must stay usable.
        Assert.NotEqual(IntPtr.Zero, first.Handle);
        Assert.True(first.GlyphCount > 0);
        Assert.True(HasOutline(first, 'H'));
    }

    [Fact]
    public void CustomFont_BrokenThenFixedInPlace_IsPickedUpAfterTheWriteTimeChanges()
    {
        using var dir = new TempFolder();
        var path = dir.File("late.ttf");
        File.WriteAllText(path, "not a font");

        Assert.False(FontStore.TryGetCustom(path, out _));
        Assert.False(FontStore.TryGetCustom(path, out _)); // remembered as broken, same version

        File.Copy(FontFixtures.Path("sub.ttf"), path, overwrite: true);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));

        Assert.True(FontStore.TryGetCustom(path, out var face));
        Assert.True(HasOutline(face, 'H'));
    }

    [Fact]
    public void CustomFont_ConcurrentFirstUseYieldsOneSharedInstance()
    {
        using var dir = new TempFolder();
        var path = dir.Copy("sub.ttf", "parallel.ttf");

        var faces = Enumerable.Range(0, 64).AsParallel()
            .Select(_ => FontStore.Resolve(new CoverLayer { FontPath = path }))
            .ToArray();

        Assert.All(faces, f => Assert.Same(faces[0], f));
        Assert.NotSame(FontStore.Bundled(FontWeight.Normal), faces[0]);
    }
}
