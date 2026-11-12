using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CustomCoverArt.Services;
using CustomCoverArt.Services.Rendering;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SkiaSharp;
using Xunit;

namespace CustomCoverArt.Tests;

/// <summary>
/// The plugin decodes only PNG, JPEG, GIF, BMP and WebP. Whatever the file name, bytes in any
/// other format (TIFF and TGA, and ICO, WBMP, HEIF, AVIF, RAW, which Skia could open) must never
/// reach a decoder, and embedded ICC profiles must never be applied or kept.
/// </summary>
public class ImageFormatRestrictionTests
{
    private static byte[] Encode(Image image, IImageEncoder encoder)
    {
        using var ms = new MemoryStream();
        image.Save(ms, encoder);
        return ms.ToArray();
    }

    /// <summary>16×16 red TIFF (little-endian, "II*\0"), exported once from ImageSharp's TiffEncoder.</summary>
    private static byte[] TiffBytes() => File.ReadAllBytes(TestImages.Fixture("tiff16.tif"));

    /// <summary>16×16 red TGA, exported once from ImageSharp's TgaEncoder.</summary>
    private static byte[] TgaBytes() => File.ReadAllBytes(TestImages.Fixture("tga16.tga"));

    private static ILocalizationService Loc()
    {
        var loc = Substitute.For<ILocalizationService>();
        loc.GetString(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => (string)ci[0]);
        return loc;
    }

    [Fact]
    public void AllowList_IsExactlyTheFiveSupportedFormats()
    {
        Assert.Equal(
            new[] { ImageKind.Png, ImageKind.Jpeg, ImageKind.Gif, ImageKind.Bmp, ImageKind.Webp }.OrderBy(k => k),
            ImageSignature.Allowed.OrderBy(k => k));
        Assert.Equal(Enum.GetValues<ImageKind>().OrderBy(k => k), ImageSignature.Allowed.OrderBy(k => k));
    }

    [Theory]
    [InlineData("tiff", ".png")]
    [InlineData("tiff", ".jpg")]
    [InlineData("tiff", ".tif")]
    [InlineData("tga", ".png")]
    public void UnsupportedFormat_UnderAnyName_IsNeverDecoded(string format, string ext)
    {
        var bytes = format == "tiff" ? TiffBytes() : TgaBytes();
        if (format == "tiff")
        {
            Assert.Equal(new byte[] { 0x49, 0x49, 0x2A, 0x00 }, bytes.Take(4)); // sanity: really a TIFF
        }

        Assert.Null(ImageSignature.Detect(bytes));

        var path = TestImages.TempPath(ext);
        File.WriteAllBytes(path, bytes);
        try
        {
            Assert.Null(SafeImageLoader.TryProbe(path));
            Assert.Null(SafeImageLoader.TryProbe(bytes));
            Assert.False(SafeImageLoader.CanDecodeFirstFrame(bytes));
            Assert.Null(SafeImageLoader.TryDecode(path, maxFrames: 1));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Upload_TiffNamedPng_IsRejected()
    {
        var bytes = TiffBytes();
        var file = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "poster.png");

        var result = await new ImageProcessingService(Loc()).ValidateFileAsync(file);

        Assert.False(result.IsValid);
        Assert.Equal("errors.invalid_file_format", result.ErrorMessage);
    }

    public static TheoryData<string, string> SupportedFormats => new()
    {
        { "png", ".png" },
        { "jpeg", ".jpg" },
        { "gif", ".gif" },
        { "bmp", ".bmp" },
        { "webp", ".webp" },
    };

    private static byte[] SupportedBytes(string format)
    {
        using var img = new Image<Rgba32>(24, 24, Color.Green);
        IImageEncoder encoder = format switch
        {
            "png" => new PngEncoder(),
            "jpeg" => new JpegEncoder(),
            "gif" => new GifEncoder(),
            "bmp" => new BmpEncoder(),
            "webp" => new WebpEncoder(),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
        return Encode(img, encoder);
    }

    [Theory]
    [MemberData(nameof(SupportedFormats))]
    public async Task SupportedFormats_StillLoadAndUpload(string format, string ext)
    {
        var bytes = SupportedBytes(format);
        var path = TestImages.TempPath(ext);
        File.WriteAllBytes(path, bytes);
        try
        {
            var probe = SafeImageLoader.TryProbe(path);
            Assert.NotNull(probe);
            Assert.Equal(format, probe!.Kind.ToString(), StringComparer.OrdinalIgnoreCase);
            Assert.True(SafeImageLoader.CanDecodeFirstFrame(bytes));
            using var decoded = SafeImageLoader.TryDecode(path, maxFrames: 1);
            Assert.NotNull(decoded);
            Assert.Equal(24, decoded!.Width);
            Assert.True(TestPixels.Px(decoded.RootFrame, 12, 12).G > 100, $"{format} decoded to the wrong colour");

            var file = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "cover" + ext);
            var result = await new ImageProcessingService(Loc()).ValidateFileAsync(file);
            Assert.True(result.IsValid, result.ErrorMessage);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AnimatedGif_KeepsFrameDelaysRepeatCountAndTransparency()
    {
        var path = TestImages.TempPath(".gif");
        using (var gif = new Image<Rgba32>(16, 16, Color.Transparent))
        {
            gif.Metadata.GetGifMetadata().RepeatCount = 3;
            gif.Frames.RootFrame.Metadata.GetGifMetadata().FrameDelay = 7;
            using var second = new Image<Rgba32>(16, 16, Color.Blue);
            var added = gif.Frames.AddFrame(second.Frames.RootFrame);
            added.Metadata.GetGifMetadata().FrameDelay = 13;
            gif.SaveAsGif(path);
        }

        try
        {
            using var loaded = SafeImageLoader.TryDecode(path, maxFrames: 60);
            Assert.NotNull(loaded);
            Assert.Equal(ImageKind.Gif, loaded!.Kind);
            Assert.Equal(2, loaded.Frames.Count);
            Assert.Equal(new[] { 70, 130 }, loaded.FrameDurationsMs);
            Assert.Equal(3, loaded.RepetitionCount);
            Assert.Equal(0, TestPixels.Px(loaded.RootFrame, 8, 8).A);
            Assert.Equal(new Rgba(0, 0, 255, 255), TestPixels.Px(loaded.Frames[1], 8, 8));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void PngTransparency_Survives()
    {
        var path = TestImages.TempPath(".png");
        using (var img = new Image<Rgba32>(8, 8, new Rgba32(255, 0, 0, 0))) { img[0, 0] = new Rgba32(0, 0, 255, 128); img.SaveAsPng(path); }
        try
        {
            using var loaded = SafeImageLoader.TryDecode(path, maxFrames: 1);
            Assert.NotNull(loaded);
            Assert.Equal(0, TestPixels.Px(loaded!.RootFrame, 4, 4).A);
            var half = TestPixels.Px(loaded.RootFrame, 0, 0);
            Assert.Equal(128, half.A);
            Assert.True(TestPixels.Near(half, new Rgba(0, 0, 255, 128), 1), $"got {half}");
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// 16×16 green images carrying an ICC profile, exported once from ImageSharp: a minimal
    /// well-formed header (JPEG APP2, PNG iCCP, WebP ICCP) and the GHSA-gwg2-r3hj-4w44 profile
    /// (an mpet CLUT declaring 15×15 channels, truncated) in a PNG.
    /// </summary>
    public static TheoryData<string, string> IccCarriers => new()
    {
        { "icc-valid.jpg", "ICC_PROFILE" },
        { "icc-valid.png", "iCCP" },
        { "icc-valid.webp", "ICCP" },
        { "icc-malformed-clut.png", "iCCP" },
    };

    [Theory]
    [MemberData(nameof(IccCarriers))]
    public void EmbeddedIccProfile_LoadsButIsNeverKept(string fixture, string marker)
    {
        var path = TestImages.Fixture(fixture);
        var bytes = File.ReadAllBytes(path);

        // Sanity: the fixture really carries the profile.
        Assert.True(
            bytes.AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes(marker)) >= 0,
            $"{fixture} lost its {marker} chunk");

        var decoded = SafeImageLoader.TryDecode(path, maxFrames: 1);
        if (decoded is null)
        {
            // The malformed profile may be refused outright; it must never throw.
            Assert.StartsWith("icc-malformed", fixture, StringComparison.Ordinal);
            return;
        }

        using (decoded)
        {
            Assert.Null(decoded.RootFrame.ColorSpace);
            Assert.True(TestPixels.Px(decoded.RootFrame, 8, 8).G > 100);
        }
    }

    /// <summary>
    /// Skia ships an ICO codec. An ICO wrapping a perfectly good PNG must still be refused,
    /// because the allow-list runs on the bytes before any codec does.
    /// </summary>
    [Fact]
    public async Task Decoder_RejectsAnIcoEvenThoughSkiaCanReadIt()
    {
        var png = File.ReadAllBytes(TestImages.Fixture("icc-valid.png"));
        var ico = new byte[22 + png.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(2), 1);   // type: icon
        BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(4), 1);   // one image
        ico[6] = 16;                                                  // width
        ico[7] = 16;                                                  // height
        BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(10), 1);  // planes
        BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(12), 32); // bits per pixel
        BinaryPrimitives.WriteUInt32LittleEndian(ico.AsSpan(14), (uint)png.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(ico.AsSpan(18), 22);
        png.CopyTo(ico, 22);

        // Sanity: Skia itself opens this file as an ICO.
        using (var data = SKData.CreateCopy(ico))
        using (var codec = SKCodec.Create(data))
        {
            Assert.NotNull(codec);
            Assert.Equal(SKEncodedImageFormat.Ico, codec!.EncodedFormat);
        }

        var path = TestImages.TempPath(".png");
        File.WriteAllBytes(path, ico);
        try
        {
            Assert.Null(ImageSignature.Detect(ico));
            Assert.Null(SafeImageLoader.TryProbe(path));
            Assert.Null(SafeImageLoader.TryProbe(ico));
            Assert.Null(SafeImageLoader.TryDecode(path, maxFrames: 1));
            Assert.False(SafeImageLoader.CanDecodeFirstFrame(ico));

            var file = new FormFile(new MemoryStream(ico), 0, ico.Length, "file", "logo.png");
            var result = await new ImageProcessingService(Loc()).ValidateFileAsync(file);
            Assert.False(result.IsValid);
            Assert.Equal("errors.invalid_file_format", result.ErrorMessage);
        }
        finally { File.Delete(path); }
    }
}
