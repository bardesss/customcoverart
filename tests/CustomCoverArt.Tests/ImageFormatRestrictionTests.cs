using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CustomCoverArt.Models;
using CustomCoverArt.Services;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Tga;
using SixLabors.ImageSharp.Formats.Tiff;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Metadata.Profiles.Icc;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace CustomCoverArt.Tests;

/// <summary>
/// The plugin decodes only PNG, JPEG, GIF, BMP and WebP. Whatever the file name, bytes in any
/// other format (TIFF above all, whose ImageSharp 3.1.12 codec has open advisories) must never
/// reach a decoder, and embedded ICC profiles must never be kept for later parsing.
/// </summary>
public class ImageFormatRestrictionTests
{
    private static byte[] Encode(Image image, IImageEncoder encoder)
    {
        using var ms = new MemoryStream();
        image.Save(ms, encoder);
        return ms.ToArray();
    }

    private static byte[] TiffBytes()
    {
        using var img = new Image<Rgba32>(16, 16, Color.Red);
        return Encode(img, new TiffEncoder());
    }

    private static byte[] TgaBytes()
    {
        using var img = new Image<Rgba32>(16, 16, Color.Red);
        return Encode(img, new TgaEncoder());
    }

    private static ILocalizationService Loc()
    {
        var loc = Substitute.For<ILocalizationService>();
        loc.GetString(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => (string)ci[0]);
        return loc;
    }

    [Fact]
    public void DecodeConfiguration_KnowsOnlyTheFiveSupportedFormats()
    {
        var names = SafeImageLoader.DecodeConfiguration.ImageFormats.Select(f => f.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "BMP", "GIF", "JPEG", "PNG", "Webp" }.OrderBy(n => n), names, StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("tiff", ".png")]
    [InlineData("tiff", ".jpg")]
    [InlineData("tiff", ".tif")]
    [InlineData("tga", ".png")]
    public void UnsupportedFormat_UnderAnyName_IsNeverDecoded(string format, string ext)
    {
        var bytes = format == "tiff" ? TiffBytes() : TgaBytes();
        // Sanity: ImageSharp's full default configuration does decode these bytes.
        using (Image.Load(bytes)) { }

        var path = TestImages.TempPath(ext);
        File.WriteAllBytes(path, bytes);
        try
        {
            Assert.Null(SafeImageLoader.TryIdentify(path));
            Assert.Null(SafeImageLoader.TryIdentify(bytes));
            Assert.False(SafeImageLoader.CanDecodeFirstFrame(bytes));
            Assert.Null(SafeImageLoader.TryLoad(path, maxFrames: 1));
            Assert.Null(SafeImageLoader.TryLoadRgba32(path, maxFrames: 1));
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
            Assert.NotNull(SafeImageLoader.TryIdentify(path));
            Assert.True(SafeImageLoader.CanDecodeFirstFrame(bytes));
            using var native = SafeImageLoader.TryLoad(path, maxFrames: 1);
            Assert.NotNull(native);
            Assert.Equal(24, native!.Width);
            using var rgba = SafeImageLoader.TryLoadRgba32(path, maxFrames: 1);
            Assert.NotNull(rgba);
            Assert.True(rgba![12, 12].G > 100, $"{format} decoded to the wrong colour");

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
            using var loaded = SafeImageLoader.TryLoadRgba32(path, maxFrames: 60);
            Assert.NotNull(loaded);
            Assert.Equal(2, loaded!.Frames.Count);
            Assert.Equal(7, loaded.Frames[0].Metadata.GetGifMetadata().FrameDelay);
            Assert.Equal(13, loaded.Frames[1].Metadata.GetGifMetadata().FrameDelay);
            Assert.Equal(3, loaded.Metadata.GetGifMetadata().RepeatCount);
            Assert.Equal(0, loaded[8, 8].A);
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
            using var loaded = SafeImageLoader.TryLoadRgba32(path, maxFrames: 1);
            Assert.NotNull(loaded);
            Assert.Equal(0, loaded![4, 4].A);
            Assert.Equal(128, loaded[0, 0].A);
        }
        finally { File.Delete(path); }
    }

    /// <summary>A minimal well-formed ICC header (no tags) that ImageSharp accepts as valid.</summary>
    private static byte[] MinimalIccProfile()
    {
        var p = new byte[132];
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(0), (uint)p.Length);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(8), 0x04000000);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(12), 0x6D6E7472); // mntr
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(16), 0x52474220); // 'RGB '
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(20), 0x58595A20); // 'XYZ '
        "acsp"u8.CopyTo(p.AsSpan(36));
        return p;
    }

    /// <summary>The GHSA-gwg2-r3hj-4w44 profile: an mpet CLUT declaring 15x15 channels, truncated.</summary>
    private static byte[] MalformedClutProfile()
    {
        const int tagOffset = 144;
        const int elementOffset = 168;
        var p = new byte[200];
        void U32(int o, uint v) => BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(o), v);
        void U16(int o, ushort v) => BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(o), v);
        U32(0, (uint)p.Length);
        U32(8, 0x04000000);
        U32(12, 0x6D6E7472);
        U32(16, 0x52474220);
        U32(20, 0x58595A20);
        "acsp"u8.CopyTo(p.AsSpan(36));
        U32(128, 1);
        U32(132, 0x41324230); // A2B0
        U32(136, tagOffset);
        U32(140, 56);
        U32(tagOffset, 0x6D706574); // mpet
        U32(tagOffset + 12, 1);
        U32(tagOffset + 16, 24);
        U32(tagOffset + 20, 32);
        U32(elementOffset, 0x636C7574); // clut
        U16(elementOffset + 4, 15);
        U16(elementOffset + 6, 15);
        for (int i = 0; i < 15; i++) { p[elementOffset + 8 + i] = 3; }
        return p;
    }

    public static TheoryData<string, string> IccCarriers => new()
    {
        { "jpeg", "valid" },
        { "png", "valid" },
        { "webp", "valid" },
        { "png", "malformed" },
    };

    [Theory]
    [MemberData(nameof(IccCarriers))]
    public void EmbeddedIccProfile_LoadsButIsNeverKept(string format, string profile)
    {
        var icc = profile == "valid" ? MinimalIccProfile() : MalformedClutProfile();
        byte[] bytes;
        using (var img = new Image<Rgba32>(16, 16, Color.Green))
        {
            img.Metadata.IccProfile = new IccProfile(icc);
            bytes = Encode(img, format switch
            {
                "jpeg" => new JpegEncoder(),
                "webp" => new WebpEncoder(),
                _ => new PngEncoder(),
            });
        }

        // Sanity: the fixture really carries the profile (ImageSharp's default decode keeps it).
        using (var full = Image.Load(bytes))
        {
            Assert.NotNull(full.Metadata.IccProfile);
        }

        var path = TestImages.TempPath("." + (format == "jpeg" ? "jpg" : format));
        File.WriteAllBytes(path, bytes);
        try
        {
            using var native = SafeImageLoader.TryLoad(path, maxFrames: 1);
            Assert.NotNull(native);
            Assert.Null(native!.Metadata.IccProfile);
            Assert.All(native.Frames, f => Assert.Null(f.Metadata.IccProfile));

            using var rgba = SafeImageLoader.TryLoadRgba32(path, maxFrames: 1);
            Assert.NotNull(rgba);
            Assert.Null(rgba!.Metadata.IccProfile);
            Assert.True(rgba[8, 8].G > 100);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LoadedImages_CarryTheRestrictedConfiguration()
    {
        var path = TestImages.TempPath(".png");
        using (var img = new Image<Rgba32>(8, 8, Color.Green)) { img.SaveAsPng(path); }
        try
        {
            using var loaded = SafeImageLoader.TryLoad(path, maxFrames: 1);
            Assert.NotNull(loaded);
            Assert.DoesNotContain(loaded!.Configuration.ImageFormats, f => f is TiffFormat);
        }
        finally { File.Delete(path); }
    }
}
