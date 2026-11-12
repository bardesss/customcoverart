using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CustomCoverArt.Models;
using CustomCoverArt.Services;
using CustomCoverArt.Services.Rendering;
using SkiaSharp;

namespace CustomCoverArt.Tests;

/// <summary>Tolerance classes of the SkiaSharp migration spec (S7.2).</summary>
internal enum CorpusClass
{
    /// <summary>Max channel difference 0.</summary>
    Exact,

    /// <summary>Mean at most 1.5 and 99th percentile at most 8.</summary>
    Close,

    /// <summary>Mean at most 4 and 99th percentile at most 48.</summary>
    Loose,

    /// <summary>Loose pixel bound plus ink geometry (centroid, bounding box, mass) against the case background.</summary>
    Text,

    /// <summary>GIF: frame count, delays and loop exact; every frame at the Loose bound.</summary>
    Animated,
}

/// <summary>
/// One corpus document. <paramref name="InkBackground"/> is the flat colour behind the text
/// of a <see cref="CorpusClass.Text"/> case; the ink geometry check measures against it.
/// </summary>
internal sealed record CorpusCase(
    string Name,
    CorpusClass Class,
    Func<CorpusInputs, CoverDocument> Build,
    SKColor? InkBackground = null);

/// <summary>Absolute paths of the generated input files the corpus documents refer to.</summary>
internal sealed record CorpusInputs(string PhotoPng, string PhotoJpg, string LogoPng, string AnimatedGif, string FontTtf);

internal sealed record CorpusResult(bool Pass, double Mean, double P99, int MaxDiff, string Note);

/// <summary>
/// The render corpus: about 35 documents rendered end to end through
/// <see cref="CoverArtService.GenerateFromDocumentAsync"/>, plus an engine-neutral comparer
/// that reads both outputs back with <see cref="SKCodec"/>. The same corpus is rendered
/// before and after every migration task and compared against the stored baseline.
/// Collage is not covered: it needs Jellyfin library data.
/// </summary>
internal static class RenderCorpus
{
    public const int Width = 300;
    public const int Height = 450;
    public const int GifWidth = 200;
    public const int GifHeight = 300;

    // Text cases share one flat background so the ink (everything that differs from it) is measurable.
    private const string TextBg = "#3a5a7a";
    private static readonly SKColor TextBgColor = SKColor.Parse(TextBg);

    // Per-channel difference above which a pixel counts as ink in the Text geometry check.
    internal const int InkThreshold = 24;

    // ------------------------------------------------------------------ cases

    public static IReadOnlyList<CorpusCase> Cases { get; } = BuildCases();

    private static List<CorpusCase> BuildCases()
    {
        var list = new List<CorpusCase>();

        void Add(string name, CorpusClass c, Func<CorpusInputs, CoverDocument> build, SKColor? ink = null)
            => list.Add(new CorpusCase(name, c, build, ink));

        // Solid colour, with each effect alone.
        Add("01-solid", CorpusClass.Exact, _ => Solid());
        Add("02-solid-vignette", CorpusClass.Exact, _ => Solid(d => d.Effects.Vignette = new VignetteSettings { Enabled = true, Amount = 0.6f, Softness = 0.5f }));
        Add("03-solid-grain", CorpusClass.Exact, _ => Solid(d => d.Effects.Grain = new GrainSettings { Enabled = true, Amount = 0.12f, Seed = 7 }));
        Add("04-solid-border-square", CorpusClass.Close, _ => Solid(d => d.Effects.Border = new BorderSettings { Enabled = true, Color = "#ffffff", Thickness = 10 }));
        Add("05-solid-border-radius", CorpusClass.Loose, _ => Solid(d => d.Effects.Border = new BorderSettings { Enabled = true, Color = "#ffffff", Thickness = 10, Radius = 24 }));
        Add("06-solid-border-double", CorpusClass.Loose, _ => Solid(d => d.Effects.Border = new BorderSettings { Enabled = true, Color = "#f5d76e", Thickness = 6, Gap = 8, Double = true, Radius = 12 }));
        Add("07-solid-softlight", CorpusClass.Exact, _ => Solid(d => d.Effects.SoftLight = new SoftLightSettings { Enabled = true, Color = "#ffe9a8", Opacity = 0.3f }));

        // Gradients: 3 stops, four angles, then radial off-centre.
        Add("08-gradient-linear-0", CorpusClass.Close, _ => Gradient(0f));
        Add("09-gradient-linear-45", CorpusClass.Close, _ => Gradient(45f));
        Add("10-gradient-linear-90", CorpusClass.Close, _ => Gradient(90f));
        Add("11-gradient-linear-200", CorpusClass.Close, _ => Gradient(200f));
        Add("12-gradient-radial-offcentre", CorpusClass.Close, _ =>
        {
            var doc = Gradient(0f);
            var g = doc.Background.Gradient!;
            g.Type = GradientType.Radial;
            g.CenterX = 0.3f;
            g.CenterY = 0.65f;
            g.Radius = 0.7f;
            return doc;
        });

        // Photo backgrounds.
        Add("13-overlay-ramp", CorpusClass.Close, i =>
        {
            var doc = Photo(i.PhotoPng, "cover");
            doc.Background.Overlay = new GradientSettings
            {
                IsEnabled = true,
                Angle = 90f,
                Stops = new List<GradientStop>
                {
                    new() { Color = "#000000", Position = 0f, Alpha = 0f },
                    new() { Color = "#000000", Position = 1f, Alpha = 0.9f },
                },
            };
            return doc;
        });
        Add("14-photo-cover", CorpusClass.Close, i => Photo(i.PhotoPng, "cover"));
        Add("15-photo-contain", CorpusClass.Close, i => Photo(i.PhotoPng, "contain"));
        Add("16-photo-stretch", CorpusClass.Close, i => Photo(i.PhotoPng, "stretch"));
        Add("17-photo-jpeg-cover", CorpusClass.Close, i => Photo(i.PhotoJpg, "cover"));
        Add("18-photo-pan-zoom", CorpusClass.Close, i =>
        {
            var doc = Photo(i.PhotoPng, "cover");
            doc.Background.Transform = new BackgroundTransform { Scale = 1.6f, OffsetX = 0.4f, OffsetY = -0.3f };
            return doc;
        });
        Add("19-photo-blur6-dim", CorpusClass.Loose, i =>
        {
            var doc = Photo(i.PhotoPng, "cover");
            doc.Background.Blur = 6f;
            doc.Background.Dim = 0.4f;
            return doc;
        });

        // Image layers.
        Add("20-logo-opacity", CorpusClass.Loose, i => WithLayers(Solid(), ImageLayer(i.LogoPng, 0.5f, 0f)));
        Add("21-logo-rotated", CorpusClass.Loose, i => WithLayers(Solid(), ImageLayer(i.LogoPng, 1f, 30f)));

        // Text. All on one flat background; the geometry check measures ink against it.
        Add("22-text-left", CorpusClass.Text, _ => Text(l => { l.X = 0.08f; l.Align = TextAlign.Left; }), TextBgColor);
        Add("23-text-centre", CorpusClass.Text, _ => Text(l => { l.Align = TextAlign.Center; }), TextBgColor);
        Add("24-text-right", CorpusClass.Text, _ => Text(l => { l.X = 0.92f; l.Align = TextAlign.Right; }), TextBgColor);
        Add("25-text-rotated-15", CorpusClass.Text, _ => Text(l => l.Rotation = 15f), TextBgColor);
        Add("26-text-shadow-hard", CorpusClass.Text, _ => Text(l => l.Shadow = new TextShadowSettings { Enabled = true, Color = "#000000", Blur = 0, OffsetX = 4, OffsetY = 4 }), TextBgColor);
        Add("27-text-shadow-blur12-rotated", CorpusClass.Text, _ => Text(l =>
        {
            l.Rotation = -12f;
            l.Shadow = new TextShadowSettings { Enabled = true, Color = "#000000", Blur = 12, OffsetX = 6, OffsetY = 6 };
        }), TextBgColor);
        Add("28-text-outline4", CorpusClass.Text, _ => Text(l => l.Outline = new TextOutlineSettings { Enabled = true, Color = "#101820", Width = 4 }), TextBgColor);
        Add("29-text-outline4-half", CorpusClass.Text, _ => Text(l =>
        {
            l.Opacity = 0.5f;
            l.Outline = new TextOutlineSettings { Enabled = true, Color = "#101820", Width = 4 };
        }), TextBgColor);
        Add("30-text-weight-light", CorpusClass.Text, _ => Text(l => l.Weight = FontWeight.Light), TextBgColor);
        Add("31-text-weight-extrabold", CorpusClass.Text, _ => Text(l => l.Weight = FontWeight.ExtraBold), TextBgColor);
        Add("32-text-custom-ttf", CorpusClass.Text, i => Text(l => l.FontPath = i.FontTtf), TextBgColor);

        // Animated GIF: Ken Burns, passthrough of an animated source, repeated still.
        Add("33-anim-kenburns", CorpusClass.Animated, i =>
        {
            var doc = Photo(i.PhotoPng, "cover");
            Gif(doc);
            doc.Background.Animation = new AnimationSettings { Enabled = true, KenBurns = true, ZoomAmount = 0.3f, Direction = "in", FrameCount = 6, DelayMs = 120, Loop = true };
            return doc;
        });
        Add("34-anim-passthrough", CorpusClass.Animated, i =>
        {
            var doc = Photo(i.AnimatedGif, "cover");
            Gif(doc);
            doc.Background.Animation = new AnimationSettings { Enabled = true, KenBurns = false, FrameCount = 20, DelayMs = 80, Loop = true };
            return doc;
        });
        Add("35-anim-static-repeat", CorpusClass.Animated, i =>
        {
            var doc = Photo(i.PhotoPng, "cover");
            Gif(doc);
            doc.Layers.Add(TextLayer(l => { l.Content = "Still"; l.Size = 0.1f; }));
            doc.Background.Animation = new AnimationSettings { Enabled = true, KenBurns = false, FrameCount = 4, DelayMs = 100, Loop = false };
            return doc;
        });

        // Dim over a solid colour (spec S7.1, Exact): a flat-coloured image dimmed with a distinct colour.
        Add("36-solid-image-dim", CorpusClass.Exact, i =>
        {
            var doc = Photo(Path.Combine(Path.GetDirectoryName(i.PhotoPng)!, "solid.png"), "stretch");
            doc.Background.Dim = 0.35f;
            doc.Background.DimColor = "#102a60";
            return doc;
        });

        return list;
    }

    private static CoverDocument NewDoc() => new()
    {
        Canvas = new CanvasSettings { Width = Width, Height = Height, Format = "png" },
        Background = new BackgroundLayer { Dim = 0f },
    };

    private static void Gif(CoverDocument doc)
    {
        doc.Canvas.Width = GifWidth;
        doc.Canvas.Height = GifHeight;
        doc.Canvas.Format = "gif";
    }

    private static CoverDocument Solid(Action<CoverDocument>? tweak = null)
    {
        var doc = NewDoc();
        doc.Background.Source = BackgroundSources.Solid;
        doc.Background.DimColor = "#c0392b";
        tweak?.Invoke(doc);
        return doc;
    }

    private static CoverDocument Gradient(float angle)
    {
        var doc = NewDoc();
        doc.Background.Source = BackgroundSources.Gradient;
        doc.Background.Gradient = new GradientSettings
        {
            IsEnabled = true,
            Type = GradientType.Linear,
            Angle = angle,
            Stops = new List<GradientStop>
            {
                new() { Color = "#1b2a49", Position = 0f },
                new() { Color = "#e84a5f", Position = 0.55f },
                new() { Color = "#ffd166", Position = 1f },
            },
        };
        return doc;
    }

    private static CoverDocument Photo(string path, string fit)
    {
        var doc = NewDoc();
        doc.Background.Source = BackgroundSources.Upload;
        doc.Background.ImagePath = path;
        doc.Background.Fit = fit;
        doc.Background.DimColor = "#000000";
        return doc;
    }

    private static CoverDocument WithLayers(CoverDocument doc, params CoverLayer[] layers)
    {
        doc.Layers.AddRange(layers);
        return doc;
    }

    private static CoverLayer ImageLayer(string path, float opacity, float rotation) => new()
    {
        Id = "logo",
        Type = "image",
        ImagePath = path,
        X = 0.5f,
        Y = 0.5f,
        Width = 0.6f,
        Height = 0.4f, // 0.6 x 300 = 0.4 x 450 = 180 px: square, like the logo
        Opacity = opacity,
        Rotation = rotation,
    };

    private static CoverLayer TextLayer(Action<CoverLayer> tweak)
    {
        var layer = new CoverLayer
        {
            Id = "t1",
            Type = "text",
            Content = "Midnight Harbor",
            X = 0.5f,
            Y = 0.5f,
            Size = 0.075f,
            Color = "#ffffff",
            Weight = FontWeight.Bold,
            Align = TextAlign.Center,
        };
        tweak(layer);
        return layer;
    }

    private static CoverDocument Text(Action<CoverLayer> tweak)
    {
        var doc = NewDoc();
        doc.Background.Source = BackgroundSources.Solid;
        doc.Background.DimColor = TextBg;
        doc.Layers.Add(TextLayer(tweak));
        return doc;
    }

    // ----------------------------------------------------------------- inputs

    /// <summary>
    /// Writes the generated inputs into <paramref name="dir"/>. The stills are procedural
    /// (SkiaSharp), the animated GIF comes from <see cref="TestImages.SaveMultiFrameGif"/>, and
    /// the font is copied from <paramref name="fontSourceDir"/> (default <c>dir/fonts</c>).
    /// </summary>
    public static CorpusInputs WriteInputs(string dir, string? fontSourceDir = null)
    {
        Directory.CreateDirectory(dir);
        var fontSource = Path.Combine(fontSourceDir ?? Path.Combine(dir, "fonts"), "sub.ttf");
        if (!File.Exists(fontSource))
        {
            throw new FileNotFoundException("The corpus needs sub.ttf; copy it into " + Path.GetDirectoryName(fontSource), fontSource);
        }

        var photoPng = Path.Combine(dir, "photo.png");
        var photoJpg = Path.Combine(dir, "photo.jpg");
        var logoPng = Path.Combine(dir, "logo.png");
        var animatedGif = Path.Combine(dir, "animated.gif");
        var fontTtf = Path.Combine(dir, "sub.ttf");

        using (var photo = MakePhoto(640, 800))
        {
            File.WriteAllBytes(photoPng, Encode(photo, SKEncodedImageFormat.Png, 100));
            File.WriteAllBytes(photoJpg, Encode(photo, SKEncodedImageFormat.Jpeg, 90));
        }

        using (var solid = RenderSurface.New(Width, Height, new SKColor(0x3d, 0x8b, 0x6e)))
        {
            File.WriteAllBytes(Path.Combine(dir, "solid.png"), Encode(solid, SKEncodedImageFormat.Png, 100));
        }

        using (var logo = MakeLogo(240))
        {
            File.WriteAllBytes(logoPng, Encode(logo, SKEncodedImageFormat.Png, 100));
        }

        TestImages.SaveMultiFrameGif(animatedGif, 120, 180, 3);
        File.Copy(fontSource, fontTtf, overwrite: true);

        return new CorpusInputs(photoPng, photoJpg, logoPng, animatedGif, fontTtf);
    }

    /// <summary>The input paths for an inputs directory already populated by <see cref="WriteInputs"/>.</summary>
    public static CorpusInputs InputsIn(string dir) => new(
        Path.Combine(dir, "photo.png"),
        Path.Combine(dir, "photo.jpg"),
        Path.Combine(dir, "logo.png"),
        Path.Combine(dir, "animated.gif"),
        Path.Combine(dir, "sub.ttf"));

    private static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format, int quality)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, quality);
        return data.ToArray();
    }

    // A photo-like picture: a sky/ground gradient, soft discs, fine diagonal stripes and per-pixel
    // noise (a deterministic hash), so resampling and blur kernels have detail to disagree about.
    private static SKBitmap MakePhoto(int w, int h)
    {
        var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            using (var sky = new SKPaint())
            {
                sky.Shader = SKShader.CreateLinearGradient(
                    new SKPoint(0, 0), new SKPoint(0, h),
                    new[] { new SKColor(0x1d, 0x3b, 0x6b), new SKColor(0xf0, 0x9a, 0x5b), new SKColor(0x2b, 0x4a, 0x2e) },
                    new[] { 0f, 0.6f, 1f },
                    SKShaderTileMode.Clamp);
                canvas.DrawRect(0, 0, w, h, sky);
            }

            var rng = new Random(1234);
            for (int i = 0; i < 14; i++)
            {
                using var disc = new SKPaint { IsAntialias = true, Color = new SKColor((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256), 170) };
                canvas.DrawCircle(rng.Next(w), rng.Next(h), 30 + rng.Next(90), disc);
            }

            using var stripe = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 70), StrokeWidth = 3, Style = SKPaintStyle.Stroke };
            for (int x = -h; x < w; x += 22)
            {
                canvas.DrawLine(x, h, x + h, 0, stripe);
            }

            using var edge = new SKPaint { Color = new SKColor(10, 10, 10), IsAntialias = false };
            canvas.DrawRect(w * 0.2f, h * 0.55f, w * 0.35f, h * 0.12f, edge);
        }

        // Deterministic noise, written straight into the pixels.
        var span = bitmap.GetPixelSpan();
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                uint n = (uint)(x * 73856093) ^ (uint)(y * 19349663);
                n ^= n >> 13;
                n *= 0x5bd1e995;
                int noise = (int)((n >> 24) & 0x0F) - 8;
                int o = (y * w + x) * 4;
                for (int c = 0; c < 3; c++)
                {
                    span[o + c] = (byte)Math.Clamp(span[o + c] + noise, 0, 255);
                }
            }
        }

        return bitmap;
    }

    // A logo with real alpha: transparent corners, an opaque ring, and a half transparent triangle.
    private static SKBitmap MakeLogo(int size)
    {
        var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(SKColors.Transparent);
        using var canvas = new SKCanvas(bitmap);
        float c = size / 2f;
        using (var disc = new SKPaint { IsAntialias = true, Color = new SKColor(0x22, 0x66, 0xcc) })
        {
            canvas.DrawCircle(c, c, size * 0.45f, disc);
        }

        using (var ring = new SKPaint { IsAntialias = true, Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = size * 0.06f })
        {
            canvas.DrawCircle(c, c, size * 0.36f, ring);
        }

        using var path = new SKPath();
        path.MoveTo(c, size * 0.25f);
        path.LineTo(size * 0.78f, size * 0.72f);
        path.LineTo(size * 0.22f, size * 0.72f);
        path.Close();
        using var tri = new SKPaint { IsAntialias = true, Color = new SKColor(0xff, 0xcc, 0x33, 140) };
        canvas.DrawPath(path, tri);
        return bitmap;
    }

    // ------------------------------------------------------------- rendering

    /// <summary>
    /// Renders every case (or those accepted by <paramref name="filter"/>) into <paramref name="outDir"/> as
    /// <c>NN-name.png|gif</c>. The service sandbox lives under <c>outDir/data</c>; inputs sit in
    /// <c>outDir/data/customcoverart/inputs</c>. When <paramref name="reuseInputsFrom"/> is given its
    /// inputs are copied instead of regenerated, so a run renders from byte-identical inputs.
    /// Returns the output path per case name.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string>> RenderAsync(
        string outDir, string fontSourceDir, string? reuseInputsFrom = null, Func<CorpusCase, bool>? filter = null)
    {
        Directory.CreateDirectory(outDir);
        var dataDir = Path.Combine(outDir, "data");
        var inputsDir = Path.Combine(dataDir, "customcoverart", "inputs");

        CorpusInputs inputs;
        if (reuseInputsFrom is not null && Directory.Exists(reuseInputsFrom))
        {
            Directory.CreateDirectory(inputsDir);
            foreach (var file in Directory.GetFiles(reuseInputsFrom))
            {
                File.Copy(file, Path.Combine(inputsDir, Path.GetFileName(file)), overwrite: true);
            }

            inputs = InputsIn(inputsDir);
        }
        else
        {
            inputs = WriteInputs(inputsDir, fontSourceDir);
        }

        var paths = NSubstitute.Substitute.For<MediaBrowser.Common.Configuration.IApplicationPaths>();
        NSubstitute.SubstituteExtensions.Returns(paths.DataPath, dataDir);
        var service = new CoverArtService(
            paths,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CoverArtService>.Instance,
            NSubstitute.Substitute.For<IMediaItemService>());

        var outputs = new Dictionary<string, string>();
        foreach (var c in Cases.Where(c => filter?.Invoke(c) ?? true))
        {
            var generated = await service.GenerateFromDocumentAsync(c.Build(inputs));
            var target = Path.Combine(outDir, c.Name + Path.GetExtension(generated));
            File.Move(generated, target, overwrite: true);
            outputs[c.Name] = target;
        }

        return outputs;
    }

    // ------------------------------------------------------------- comparing

    internal sealed record DecodedImage(int Width, int Height, List<SKBitmap> Frames, List<int> DelaysMs, int RepetitionCount);

    /// <summary>
    /// Decodes every frame of a PNG or GIF with <see cref="SKCodec"/> into unpremultiplied RGBA, honouring
    /// the GIF frame dependencies so each bitmap is the full composited frame.
    /// </summary>
    internal static DecodedImage Decode(string path)
    {
        using var stream = File.OpenRead(path);
        using var codec = SKCodec.Create(stream) ?? throw new InvalidDataException("Cannot decode " + path);
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var frameInfos = codec.FrameInfo;
        int count = Math.Max(1, frameInfos.Length);

        var frames = new List<SKBitmap>(count);
        var delays = new List<int>(count);
        try
        {
            for (int i = 0; i < count; i++)
            {
                var bitmap = new SKBitmap(info);
                bitmap.Erase(SKColors.Transparent);
                int required = frameInfos.Length > 0 ? frameInfos[i].RequiredFrame : -1;
                if (required >= 0)
                {
                    frames[required].GetPixelSpan().CopyTo(bitmap.GetPixelSpan());
                }

                var options = new SKCodecOptions(i, required);
                var result = codec.GetPixels(info, bitmap.GetPixels(), options);
                if (result != SKCodecResult.Success)
                {
                    bitmap.Dispose();
                    throw new InvalidDataException($"Cannot decode frame {i} of {path}: {result}");
                }

                frames.Add(bitmap);
                delays.Add(frameInfos.Length > 0 ? frameInfos[i].Duration : 0);
            }

            return new DecodedImage(info.Width, info.Height, frames, delays, codec.RepetitionCount);
        }
        catch
        {
            foreach (var f in frames)
            {
                f.Dispose();
            }

            throw;
        }
    }

    private sealed class FrameStats
    {
        public readonly long[] Histogram = new long[256];
        public long Samples;
        public long Sum;
        public int Max;

        public double Mean => Samples == 0 ? 0 : (double)Sum / Samples;

        /// <summary>Smallest difference d such that at least 99 % of the samples are at most d.</summary>
        public int P99
        {
            get
            {
                long target = (long)Math.Ceiling(Samples * 0.99);
                long cumulative = 0;
                for (int d = 0; d < 256; d++)
                {
                    cumulative += Histogram[d];
                    if (cumulative >= target)
                    {
                        return d;
                    }
                }

                return 255;
            }
        }
    }

    private readonly record struct FrameDiff(FrameStats Rgb, FrameStats Alpha, bool NonOpaque)
    {
        /// <summary>
        /// The stats the class bounds apply to: RGB only while both images are fully opaque (alpha would
        /// only dilute the colour differences by a quarter), RGB plus alpha as soon as either image has a
        /// non-opaque pixel.
        /// </summary>
        public FrameStats Bound()
        {
            if (!NonOpaque)
            {
                return Rgb;
            }

            var merged = new FrameStats { Samples = Rgb.Samples + Alpha.Samples, Sum = Rgb.Sum + Alpha.Sum, Max = Math.Max(Rgb.Max, Alpha.Max) };
            for (int d = 0; d < 256; d++)
            {
                merged.Histogram[d] = Rgb.Histogram[d] + Alpha.Histogram[d];
            }

            return merged;
        }
    }

    // Absolute differences per channel, tracked as RGB (all three channels pooled) and alpha separately.
    // Where both pixels are fully transparent the colour channels are meaningless and count as equal.
    private static FrameDiff Diff(SKBitmap a, SKBitmap b, SKBitmap? diffOut)
    {
        var rgb = new FrameStats();
        var alpha = new FrameStats();
        bool nonOpaque = false;
        var pa = a.GetPixelSpan();
        var pb = b.GetPixelSpan();
        var po = diffOut is null ? default : diffOut.GetPixelSpan();
        for (int o = 0; o < pa.Length; o += 4)
        {
            bool bothClear = pa[o + 3] == 0 && pb[o + 3] == 0;
            nonOpaque |= pa[o + 3] != 255 || pb[o + 3] != 255;
            for (int c = 0; c < 4; c++)
            {
                int d = bothClear && c < 3 ? 0 : Math.Abs(pa[o + c] - pb[o + c]);
                var stats = c < 3 ? rgb : alpha;
                stats.Histogram[d]++;
                stats.Sum += d;
                stats.Samples++;
                if (d > stats.Max)
                {
                    stats.Max = d;
                }

                if (diffOut is not null && c < 3)
                {
                    po[o + c] = (byte)Math.Min(255, d * 8);
                }
            }

            if (diffOut is not null)
            {
                po[o + 3] = 255;
            }
        }

        return new FrameDiff(rgb, alpha, nonOpaque);
    }

    /// <summary>
    /// Compares two outputs under a tolerance class (spec S7.2). Mean, P99 and max are taken over R, G, B
    /// (all pooled); alpha is tracked separately, must be identical for Exact, and joins the pooled stats
    /// only when either image has a non-opaque pixel. Decoding must be complete. Frame count, size, and for
    /// <see cref="CorpusClass.Animated"/> delays and loop count must match exactly. The Text class needs
    /// <paramref name="inkBackground"/>, the flat colour behind the text.
    /// </summary>
    public static CorpusResult Compare(string baselineFile, string candidateFile, CorpusClass c, SKColor? inkBackground = null)
    {
        DecodedImage a, b;
        try
        {
            a = Decode(baselineFile);
        }
        catch (Exception ex)
        {
            return new CorpusResult(false, double.NaN, double.NaN, 255, "baseline unreadable: " + ex.Message);
        }

        try
        {
            b = Decode(candidateFile);
        }
        catch (Exception ex)
        {
            DisposeAll(a);
            return new CorpusResult(false, double.NaN, double.NaN, 255, "candidate unreadable: " + ex.Message);
        }

        try
        {
            return CompareDecoded(a, b, c, inkBackground, null);
        }
        finally
        {
            DisposeAll(a);
            DisposeAll(b);
        }
    }

    private static void DisposeAll(DecodedImage image)
    {
        foreach (var f in image.Frames)
        {
            f.Dispose();
        }
    }

    private static CorpusResult CompareDecoded(DecodedImage a, DecodedImage b, CorpusClass c, SKColor? inkBackground, string? diffPngPath)
    {
        if (a.Width != b.Width || a.Height != b.Height)
        {
            return new CorpusResult(false, 255, 255, 255, $"size {b.Width}x{b.Height} differs from {a.Width}x{a.Height}");
        }

        if (a.Frames.Count != b.Frames.Count)
        {
            return new CorpusResult(false, 255, 255, 255, $"frame count {b.Frames.Count} differs from {a.Frames.Count}");
        }

        var notes = new List<string>();
        if (c == CorpusClass.Animated)
        {
            if (a.Frames.Count < 2)
            {
                notes.Add($"animated class needs more than one frame, got {a.Frames.Count}");
            }

            if (!a.DelaysMs.SequenceEqual(b.DelaysMs))
            {
                notes.Add($"delays [{string.Join(",", b.DelaysMs)}] differ from [{string.Join(",", a.DelaysMs)}]");
            }

            if (a.RepetitionCount != b.RepetitionCount)
            {
                notes.Add($"loop {b.RepetitionCount} differs from {a.RepetitionCount}");
            }
        }

        double worstMean = 0;
        int worstP99 = 0, worstMax = 0, worstAlphaMax = 0;
        SKBitmap? diffBitmap = null;
        try
        {
            if (diffPngPath is not null)
            {
                diffBitmap = new SKBitmap(new SKImageInfo(a.Width, a.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
                diffBitmap.Erase(SKColors.Black);
            }

            for (int i = 0; i < a.Frames.Count; i++)
            {
                // The diff picture shows the largest per-pixel disagreement over all frames.
                using var frameDiff = diffBitmap is null ? null : new SKBitmap(diffBitmap.Info);
                var fd = Diff(a.Frames[i], b.Frames[i], frameDiff);
                var s = fd.Bound();
                worstAlphaMax = Math.Max(worstAlphaMax, fd.Alpha.Max);
                worstMean = Math.Max(worstMean, s.Mean);
                worstP99 = Math.Max(worstP99, s.P99);
                worstMax = Math.Max(worstMax, s.Max);
                if (frameDiff is not null && diffBitmap is not null)
                {
                    var src = frameDiff.GetPixelSpan();
                    var dst = diffBitmap.GetPixelSpan();
                    for (int o = 0; o < src.Length; o++)
                    {
                        if ((o & 3) != 3 && src[o] > dst[o])
                        {
                            dst[o] = src[o];
                        }
                        else if ((o & 3) == 3)
                        {
                            dst[o] = 255;
                        }
                    }
                }
            }

            if (diffBitmap is not null && diffPngPath is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(diffPngPath)!);
                File.WriteAllBytes(diffPngPath, Encode(diffBitmap, SKEncodedImageFormat.Png, 100));
            }
        }
        finally
        {
            diffBitmap?.Dispose();
        }

        switch (c)
        {
            case CorpusClass.Exact:
                if (worstMax != 0 || worstAlphaMax != 0)
                {
                    notes.Add($"max diff {worstMax}, alpha max diff {worstAlphaMax} (exact required)");
                }

                break;
            case CorpusClass.Close:
                CheckBound(notes, worstMean, worstP99, 1.5, 8);
                break;
            case CorpusClass.Loose:
            case CorpusClass.Animated:
                CheckBound(notes, worstMean, worstP99, 4, 48);
                break;
            case CorpusClass.Text:
                CheckBound(notes, worstMean, worstP99, 4, 48);
                if (inkBackground is null)
                {
                    notes.Add("text class needs the ink background colour");
                }
                else
                {
                    CheckInk(notes, a.Frames[0], b.Frames[0], inkBackground.Value);
                }

                break;
        }

        return new CorpusResult(notes.Count == 0, worstMean, worstP99, worstMax, string.Join("; ", notes));
    }

    private static void CheckBound(List<string> notes, double mean, int p99, double meanBound, int p99Bound)
    {
        if (mean > meanBound)
        {
            notes.Add($"mean {mean:F3} > {meanBound}");
        }

        if (p99 > p99Bound)
        {
            notes.Add($"p99 {p99} > {p99Bound}");
        }
    }

    internal readonly record struct Ink(double Mass, double CentroidX, double CentroidY, int MinX, int MinY, int MaxX, int MaxY);

    /// <summary>
    /// Ink = every pixel whose largest colour-channel difference from the flat background colour exceeds
    /// <see cref="InkThreshold"/>. Each ink pixel weighs (difference / 255), so antialiased edges count
    /// fractionally; mass is the sum of weights, the centroid is weight-averaged, and the bounding box
    /// covers all ink pixels. Returns null when nothing is ink.
    /// </summary>
    internal static Ink? MeasureInk(SKBitmap bitmap, SKColor background)
    {
        var p = bitmap.GetPixelSpan();
        int w = bitmap.Width;
        double mass = 0, sx = 0, sy = 0;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                int d = Math.Max(Math.Abs(p[o] - background.Red), Math.Max(Math.Abs(p[o + 1] - background.Green), Math.Abs(p[o + 2] - background.Blue)));
                if (d <= InkThreshold)
                {
                    continue;
                }

                double weight = d / 255.0;
                mass += weight;
                sx += weight * x;
                sy += weight * y;
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
            }
        }

        return maxX < 0 ? null : new Ink(mass, sx / mass, sy / mass, minX, minY, maxX, maxY);
    }

    // Spec S7.2 text geometry: centroid within 2 px, bbox edges within 3 px, mass within 12 %.
    private static void CheckInk(List<string> notes, SKBitmap a, SKBitmap b, SKColor background)
    {
        var ia = MeasureInk(a, background);
        var ib = MeasureInk(b, background);
        if (ia is null || ib is null)
        {
            notes.Add(ia is null && ib is null ? "no ink in either image" : ia is null ? "no ink in baseline" : "no ink in candidate");
            return;
        }

        var x = ia.Value;
        var y = ib.Value;
        double dcx = Math.Abs(x.CentroidX - y.CentroidX), dcy = Math.Abs(x.CentroidY - y.CentroidY);
        if (dcx > 2 || dcy > 2)
        {
            notes.Add(string.Format(CultureInfo.InvariantCulture, "ink centroid moved ({0:F2},{1:F2}) px, limit 2", dcx, dcy));
        }

        int edge = new[] { Math.Abs(x.MinX - y.MinX), Math.Abs(x.MinY - y.MinY), Math.Abs(x.MaxX - y.MaxX), Math.Abs(x.MaxY - y.MaxY) }.Max();
        if (edge > 3)
        {
            notes.Add($"ink bounding box edge moved {edge} px, limit 3");
        }

        double massDelta = Math.Abs(x.Mass - y.Mass) / Math.Max(x.Mass, y.Mass);
        if (massDelta > 0.12)
        {
            notes.Add(string.Format(CultureInfo.InvariantCulture, "ink mass changed {0:P1}, limit 12 %", massDelta));
        }
    }

    // ---------------------------------------------------------------- report

    /// <summary>
    /// Compares each case output in <paramref name="runDir"/> with the same name in <paramref name="baselineDir"/>,
    /// writes <c>metrics.csv</c>, <c>diff/*.png</c> and a standalone <c>index.html</c> (relative paths) into
    /// <paramref name="runDir"/>, and returns the per-case results.
    /// </summary>
    public static IReadOnlyList<(CorpusCase Case, CorpusResult Result)> CompareRun(
        string baselineDir, string runDir, IReadOnlyDictionary<string, string> runOutputs, Func<CorpusCase, bool>? filter = null)
    {
        var results = new List<(CorpusCase, CorpusResult)>();
        foreach (var c in Cases.Where(c => filter?.Invoke(c) ?? true))
        {
            // A case without a run output is a failure, never silently skipped.
            if (!runOutputs.TryGetValue(c.Name, out var candidate))
            {
                results.Add((c, new CorpusResult(false, double.NaN, double.NaN, 255, "no run output")));
                continue;
            }

            var baseline = Directory.GetFiles(baselineDir, c.Name + ".*").FirstOrDefault();
            if (baseline is null)
            {
                results.Add((c, new CorpusResult(false, double.NaN, double.NaN, 255, "no baseline output")));
                continue;
            }

            var diffPath = Path.Combine(runDir, "diff", c.Name + ".png");
            CorpusResult result;
            try
            {
                using var a = Wrap(Decode(baseline));
                using var b = Wrap(Decode(candidate));
                result = CompareDecoded(a.Image, b.Image, c.Class, c.InkBackground, diffPath);
            }
            catch (Exception ex)
            {
                result = new CorpusResult(false, double.NaN, double.NaN, 255, "unreadable: " + ex.Message);
            }

            results.Add((c, result));
        }

        WriteCsv(Path.Combine(runDir, "metrics.csv"), results);
        WriteHtml(Path.Combine(runDir, "index.html"), Path.GetFileName(baselineDir), results, runOutputs);
        return results;
    }

    private sealed class DecodedLease : IDisposable
    {
        public DecodedLease(DecodedImage image) => Image = image;

        public DecodedImage Image { get; }

        public void Dispose() => DisposeAll(Image);
    }

    private static DecodedLease Wrap(DecodedImage image) => new(image);

    private static void WriteCsv(string path, List<(CorpusCase Case, CorpusResult Result)> results)
    {
        var sb = new StringBuilder("case,class,pass,mean,p99,maxdiff,note\n");
        foreach (var (c, r) in results)
        {
            sb.Append(c.Name).Append(',').Append(c.Class).Append(',').Append(r.Pass ? "pass" : "FAIL").Append(',')
              .Append(r.Mean.ToString("F4", CultureInfo.InvariantCulture)).Append(',')
              .Append(r.P99.ToString("F0", CultureInfo.InvariantCulture)).Append(',')
              .Append(r.MaxDiff).Append(",\"").Append(r.Note.Replace("\"", "'")).Append("\"\n");
        }

        File.WriteAllText(path, sb.ToString());
    }

    private static void WriteHtml(
        string path, string baselineFolder, List<(CorpusCase Case, CorpusResult Result)> results,
        IReadOnlyDictionary<string, string> outputs)
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html><meta charset=\"utf-8\"><title>Render corpus</title>");
        sb.Append("<style>body{font:13px sans-serif;background:#222;color:#ddd;margin:16px}");
        sb.Append("table{border-collapse:collapse}td,th{padding:4px 8px;vertical-align:top;border-bottom:1px solid #444}");
        sb.Append("img{max-width:240px;background:#555}.fail{color:#ff6b6b}.pass{color:#6bff95}</style>");
        sb.Append("<h1>Render corpus</h1><table><tr><th>case</th><th>old (baseline)</th><th>new</th><th>diff x8</th></tr>");
        foreach (var (c, r) in results)
        {
            var file = outputs.TryGetValue(c.Name, out var outPath) ? Path.GetFileName(outPath) : null;
            var baseFile = Directory.GetFiles(Path.Combine(Path.GetDirectoryName(path)!, "..", baselineFolder), c.Name + ".*")
                .Select(Path.GetFileName).FirstOrDefault();
            sb.Append("<tr><td><b>").Append(c.Name).Append("</b><br>").Append(c.Class)
              .Append("<br><span class=\"").Append(r.Pass ? "pass\">pass" : "fail\">FAIL").Append("</span>")
              .Append("<br>mean ").Append(r.Mean.ToString("F3", CultureInfo.InvariantCulture))
              .Append("<br>p99 ").Append(r.P99.ToString("F0", CultureInfo.InvariantCulture))
              .Append("<br>max ").Append(r.MaxDiff)
              .Append("<br>").Append(System.Net.WebUtility.HtmlEncode(r.Note)).Append("</td>");
            sb.Append(baseFile is null ? "<td>(no baseline)</td>" : "<td><img src=\"../" + baselineFolder + "/" + baseFile + "\"></td>");
            sb.Append(file is null ? "<td>(no run output)</td>" : "<td><img src=\"" + file + "\"></td>");
            if (File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "diff", c.Name + ".png")))
            {
                sb.Append("<td><img src=\"diff/").Append(c.Name).Append(".png\"></td></tr>");
            }
            else
            {
                sb.Append("<td>(no diff image: missing file, unreadable, or size/frame mismatch)</td></tr>");
            }
        }

        sb.Append("</table>");
        File.WriteAllText(path, sb.ToString());
    }
}
