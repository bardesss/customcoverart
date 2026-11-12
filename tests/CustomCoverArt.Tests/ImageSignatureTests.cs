using System;
using System.Linq;
using CustomCoverArt.Services.Rendering;
using SkiaSharp;
using Xunit;

namespace CustomCoverArt.Tests;

public class ImageSignatureTests
{
    public static TheoryData<string, byte[], ImageKind?> Heads => new()
    {
        { "png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D }, ImageKind.Png },
        { "jpeg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46, 0, 1 }, ImageKind.Jpeg },
        { "gif", new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x10, 0, 0x10, 0, 0x80, 0 }, ImageKind.Gif },
        { "bmp", new byte[] { 0x42, 0x4D, 0x36, 0, 0, 0, 0, 0, 0, 0, 0x36, 0 }, ImageKind.Bmp },
        { "webp", "RIFF\x24\0\0\0WEBP"u8.ToArray(), ImageKind.Webp },
        { "tiff", new byte[] { 0x49, 0x49, 0x2A, 0x00, 8, 0, 0, 0, 0, 0, 0, 0 }, null },
        { "ico", new byte[] { 0x00, 0x00, 0x01, 0x00, 1, 0, 16, 16, 0, 0, 1, 0 }, null },
        { "wave", "RIFF\x24\0\0\0WAVE"u8.ToArray(), null },
        { "random3", new byte[] { 0x13, 0x37, 0x42 }, null },
    };

    [Theory]
    [MemberData(nameof(Heads))]
    public void Detect(string name, byte[] head, ImageKind? expected)
    {
        Assert.True(expected is null || head.Length == 12, name);
        Assert.Equal(expected, ImageSignature.Detect(head));
    }

    [Fact]
    public void Detect_AcceptsGif87a_AndIgnoresTrailingBytes()
    {
        Assert.Equal(ImageKind.Gif, ImageSignature.Detect("GIF87a"u8));
        Assert.Equal(ImageKind.Png, ImageSignature.Detect(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5, 6, 7, 8, 9 }));
    }

    /// <summary>Each signature needs all of its bytes: one byte short is unknown, never a guess.</summary>
    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A })]
    [InlineData(new byte[] { 0xFF, 0xD8 })]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39 })]
    [InlineData(new byte[] { 0x42 })]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42 })]
    [InlineData(new byte[0])]
    public void Detect_TruncatedSignature_IsNull(byte[] head)
    {
        Assert.Null(ImageSignature.Detect(head));
    }

    [Theory]
    [InlineData("GIF88a")]     // not a GIF version
    [InlineData("RIFF\0\0\0\0WEBX")] // RIFF with the wrong inner fourcc
    [InlineData("RIFX\0\0\0\0WEBP")] // big-endian RIFF variant
    [InlineData("\u0089PNG\r\n\u001a\0")] // last PNG signature byte wrong
    public void Detect_NearMisses_AreNull(string latin1)
    {
        var bytes = latin1.Select(c => (byte)c).ToArray();
        Assert.Null(ImageSignature.Detect(bytes));
    }

    /// <summary>The codec's own verdict must agree with the sniffed kind; every other Skia format is a mismatch.</summary>
    [Fact]
    public void Matches_PairsEachKindWithExactlyOneSkiaFormat()
    {
        var pairs = new (ImageKind Kind, SKEncodedImageFormat Format)[]
        {
            (ImageKind.Png, SKEncodedImageFormat.Png),
            (ImageKind.Jpeg, SKEncodedImageFormat.Jpeg),
            (ImageKind.Gif, SKEncodedImageFormat.Gif),
            (ImageKind.Bmp, SKEncodedImageFormat.Bmp),
            (ImageKind.Webp, SKEncodedImageFormat.Webp),
        };

        foreach (var kind in Enum.GetValues<ImageKind>())
        {
            foreach (var format in Enum.GetValues<SKEncodedImageFormat>())
            {
                Assert.Equal(pairs.Contains((kind, format)), ImageSignature.Matches(kind, format));
            }
        }
    }
}
