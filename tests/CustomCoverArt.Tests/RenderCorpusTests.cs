using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;
using SkiaSharp;
using Xunit;

namespace CustomCoverArt.Tests;

public class RenderCorpusTests
{
    // ------------------------------------------------------------------ the corpus run

    /// <summary>
    /// Environment contract: CCA_CORPUS_DIR unset -> no-op. Set without a baseline folder -> render
    /// the baseline. Set with a baseline -> render a run-* folder, compare, write metrics.csv and
    /// index.html, and fail when a case breaks its class bound.
    /// </summary>
    [Fact]
    public async Task Corpus_WritesBaselineOrCompares()
    {
        var root = Environment.GetEnvironmentVariable("CCA_CORPUS_DIR");
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        var fonts = Path.Combine(root, "fonts");
        var baseline = Path.Combine(root, "baseline");
        if (!Directory.Exists(baseline))
        {
            var outputs = await RenderCorpus.RenderAsync(baseline, fonts);
            Assert.Equal(RenderCorpus.Cases.Count, outputs.Count);
            Assert.All(outputs.Values, p => Assert.True(File.Exists(p), p));
            return;
        }

        var run = Path.Combine(root, "run-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var runOutputs = await RenderCorpus.RenderAsync(run, fonts, reuseInputsFrom: Path.Combine(baseline, "data", "customcoverart", "inputs"));
        var results = RenderCorpus.CompareRun(baseline, run, runOutputs);
        Assert.Equal(RenderCorpus.Cases.Count, results.Count);

        var failures = results.Where(r => !r.Result.Pass).Select(r => $"{r.Case.Name}: {r.Result.Note}").ToList();
        Assert.True(failures.Count == 0, "Corpus cases out of tolerance (see " + run + "):\n" + string.Join("\n", failures));
    }

    // ------------------------------------------------------- always-on corpus sanity

    [Fact]
    public void Cases_HaveUniqueNumberedNamesAndEveryClass()
    {
        var names = RenderCorpus.Cases.Select(c => c.Name).ToList();
        Assert.True(names.Count >= 30);
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.All(names, n => Assert.Matches(@"^\d\d-[a-z0-9-]+$", n));
        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal), names);
        foreach (var cls in Enum.GetValues<CorpusClass>())
        {
            Assert.Contains(RenderCorpus.Cases, c => c.Class == cls);
        }
    }

    [Fact]
    public void TextCases_DeclareTheirInkBackground_AndOnlyTextCasesDo()
    {
        foreach (var c in RenderCorpus.Cases)
        {
            Assert.Equal(c.Class == CorpusClass.Text, c.InkBackground is not null);
        }
    }

    [Fact]
    public void AnimatedCases_AreGifs_AndOthersArePng()
    {
        using var scratch = new TempDir();
        var inputs = RenderCorpus.InputsIn(scratch.Path);
        foreach (var c in RenderCorpus.Cases)
        {
            var doc = c.Build(inputs);
            if (c.Class == CorpusClass.Animated)
            {
                Assert.Equal("gif", doc.Canvas.Format);
                Assert.Equal((RenderCorpus.GifWidth, RenderCorpus.GifHeight), (doc.Canvas.Width, doc.Canvas.Height));
                Assert.True(doc.Background.Animation?.Enabled);
            }
            else
            {
                Assert.Equal("png", doc.Canvas.Format);
                Assert.Equal((RenderCorpus.Width, RenderCorpus.Height), (doc.Canvas.Width, doc.Canvas.Height));
                Assert.Null(doc.Background.Animation);
            }
        }
    }

    [Fact]
    public void WriteInputs_WritesDecodableInputs_AndCopiesTheFont()
    {
        using var scratch = new TempDir();
        var fonts = scratch.Sub("fonts");
        File.WriteAllBytes(Path.Combine(fonts, "sub.ttf"), new byte[] { 1, 2, 3 });

        var inputs = RenderCorpus.WriteInputs(scratch.Sub("inputs"), fonts);

        foreach (var (path, w, h) in new[] { (inputs.PhotoPng, 640, 800), (inputs.PhotoJpg, 640, 800), (inputs.LogoPng, 240, 240), (inputs.AnimatedGif, 120, 180) })
        {
            using var codec = SKCodec.Create(path);
            Assert.NotNull(codec);
            Assert.Equal((w, h), (codec.Info.Width, codec.Info.Height));
        }

        using (var gif = SKCodec.Create(inputs.AnimatedGif))
        {
            Assert.Equal(3, gif.FrameCount);
        }

        // The logo has real transparency in its corner.
        using (var logo = SKBitmap.Decode(inputs.LogoPng))
        {
            Assert.Equal(0, logo.GetPixel(0, 0).Alpha);
        }

        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(inputs.FontTtf));
        Assert.Equal(inputs, RenderCorpus.InputsIn(Path.GetDirectoryName(inputs.PhotoPng)!));
    }

    [Fact]
    public void WriteInputs_Throws_WhenTheFontIsMissing()
    {
        using var scratch = new TempDir();
        Assert.Throws<FileNotFoundException>(() => RenderCorpus.WriteInputs(scratch.Sub("inputs"), scratch.Sub("nofonts")));
    }

    [Fact]
    public async Task Render_IsDeterministic_AndWritesSizedOutputs()
    {
        using var scratch = new TempDir();
        var fonts = scratch.Sub("fonts");
        File.WriteAllBytes(Path.Combine(fonts, "sub.ttf"), new byte[] { 0 });
        Func<CorpusCase, bool> pick = c => c.Name == "01-solid" || c.Name == "35-anim-static-repeat";

        var one = await RenderCorpus.RenderAsync(scratch.Sub("one"), fonts, filter: pick);
        var two = await RenderCorpus.RenderAsync(scratch.Sub("two"), fonts, reuseInputsFrom: Path.Combine(scratch.Path, "one", "data", "customcoverart", "inputs"), filter: pick);

        Assert.Equal(2, one.Count);
        Assert.EndsWith(".png", one["01-solid"]);
        Assert.EndsWith(".gif", one["35-anim-static-repeat"]);
        using (var codec = SKCodec.Create(one["01-solid"]))
        {
            Assert.Equal((RenderCorpus.Width, RenderCorpus.Height), (codec.Info.Width, codec.Info.Height));
        }

        var anim = RenderCorpus.Decode(one["35-anim-static-repeat"]);
        Assert.Equal(4, anim.Frames.Count);
        Assert.All(anim.DelaysMs, d => Assert.Equal(100, d));

        foreach (var name in one.Keys)
        {
            Assert.Equal(File.ReadAllBytes(one[name]), File.ReadAllBytes(two[name]));
        }
    }

    [Fact]
    public async Task CompareRun_OfIdenticalOutputs_WritesMetricsAndAStandaloneIndex()
    {
        using var scratch = new TempDir();
        var fonts = scratch.Sub("fonts");
        File.WriteAllBytes(Path.Combine(fonts, "sub.ttf"), new byte[] { 0 });
        Func<CorpusCase, bool> pick = c => c.Name == "01-solid" || c.Name == "34-anim-passthrough";

        var baselineDir = scratch.Sub("baseline");
        var baseOut = await RenderCorpus.RenderAsync(baselineDir, fonts, filter: pick);
        var runDir = scratch.Sub("run-x");
        var runOut = await RenderCorpus.RenderAsync(runDir, fonts, Path.Combine(baselineDir, "data", "customcoverart", "inputs"), pick);

        var results = RenderCorpus.CompareRun(baselineDir, runDir, runOut, pick);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => { Assert.True(r.Result.Pass, r.Result.Note); Assert.Equal(0, r.Result.MaxDiff); });

        var csv = File.ReadAllLines(Path.Combine(runDir, "metrics.csv"));
        Assert.Equal("case,class,pass,mean,p99,maxdiff,note", csv[0]);
        Assert.Equal(3, csv.Length);
        Assert.StartsWith("01-solid,Exact,pass,0.0000,0,0,", csv[1]);

        var html = File.ReadAllText(Path.Combine(runDir, "index.html"));
        Assert.Contains("src=\"../baseline/01-solid.png\"", html);
        Assert.Contains("src=\"01-solid.png\"", html);
        Assert.Contains("src=\"diff/01-solid.png\"", html);
        Assert.DoesNotContain(":\\", html);
        Assert.True(File.Exists(Path.Combine(runDir, "diff", "34-anim-passthrough.png")));
    }

    // ------------------------------------------------------------- Compare itself

    [Fact]
    public void Compare_IdenticalFiles_PassEveryStillClass_WithMaxDiffZero()
    {
        using var scratch = new TempDir();
        var a = scratch.Png("a.png", 40, 30, (x, y) => new SKColor((byte)(x * 6), (byte)(y * 8), 90, 255));
        var b = scratch.Png("b.png", 40, 30, (x, y) => new SKColor((byte)(x * 6), (byte)(y * 8), 90, 255));

        foreach (var cls in new[] { CorpusClass.Exact, CorpusClass.Close, CorpusClass.Loose })
        {
            var r = RenderCorpus.Compare(a, b, cls);
            Assert.True(r.Pass, r.Note);
            Assert.Equal(0, r.MaxDiff);
            Assert.Equal(0d, r.Mean);
            Assert.Equal(0d, r.P99);
            Assert.Equal(string.Empty, r.Note);
        }
    }

    [Fact]
    public void Compare_KnownShift_ReportsExactMeanAndP99_AndAppliesTheClassBounds()
    {
        using var scratch = new TempDir();
        var a = scratch.Png("a.png", 50, 50, (x, y) => new SKColor(100, 120, 140, 255));
        // Red channel +10 everywhere: one of the three colour channels differs by 10.
        var b = scratch.Png("b.png", 50, 50, (x, y) => new SKColor(110, 120, 140, 255));

        var loose = RenderCorpus.Compare(a, b, CorpusClass.Loose);
        Assert.Equal(10 / 3d, loose.Mean, 6); // R differs by 10, G and B by 0: alpha does not dilute it
        Assert.Equal(10d, loose.P99);
        Assert.Equal(10, loose.MaxDiff);
        Assert.True(loose.Pass, loose.Note);

        var close = RenderCorpus.Compare(a, b, CorpusClass.Close);
        Assert.False(close.Pass);
        Assert.Contains("mean", close.Note);
        Assert.Contains("p99", close.Note);

        var exact = RenderCorpus.Compare(a, b, CorpusClass.Exact);
        Assert.False(exact.Pass);
        Assert.Contains("max diff 10", exact.Note);
    }

    [Fact]
    public void Compare_OneInHundredPixelsBeyondP99_IsIgnoredByP99ButNotByMax()
    {
        using var scratch = new TempDir();
        var a = scratch.Png("a.png", 100, 100, (x, y) => new SKColor(0, 0, 0, 255));
        // 50 of 10000 pixels (0.5 %) differ by 200 in R, G and B.
        var b = scratch.Png("b.png", 100, 100, (x, y) => y == 0 && x < 50 ? new SKColor(200, 200, 200, 255) : new SKColor(0, 0, 0, 255));

        var r = RenderCorpus.Compare(a, b, CorpusClass.Loose);
        Assert.Equal(0d, r.P99);
        Assert.Equal(200, r.MaxDiff);
        Assert.Equal(50 * 3 * 200 / 30000d, r.Mean, 6);
        Assert.True(r.Pass, r.Note);

        // 2 % beyond the threshold moves the 99th percentile.
        var c = scratch.Png("c.png", 100, 100, (x, y) => y < 2 ? new SKColor(200, 200, 200, 255) : new SKColor(0, 0, 0, 255));
        var wide = RenderCorpus.Compare(a, c, CorpusClass.Loose);
        Assert.Equal(200d, wide.P99);
        Assert.False(wide.Pass);
    }

    [Fact]
    public void Compare_SizeMismatch_Fails()
    {
        using var scratch = new TempDir();
        var a = scratch.Png("a.png", 20, 20, (x, y) => SKColors.Red);
        var b = scratch.Png("b.png", 20, 21, (x, y) => SKColors.Red);

        var r = RenderCorpus.Compare(a, b, CorpusClass.Loose);
        Assert.False(r.Pass);
        Assert.Contains("size 20x21 differs from 20x20", r.Note);
    }

    [Fact]
    public void Compare_UnreadableOrMissingFiles_FailWithoutThrowing()
    {
        using var scratch = new TempDir();
        var good = scratch.Png("a.png", 8, 8, (x, y) => SKColors.Red);
        var junk = Path.Combine(scratch.Path, "junk.png");
        File.WriteAllBytes(junk, new byte[] { 1, 2, 3, 4 });

        Assert.Contains("candidate unreadable", RenderCorpus.Compare(good, junk, CorpusClass.Exact).Note);
        Assert.Contains("baseline unreadable", RenderCorpus.Compare(junk, good, CorpusClass.Exact).Note);
        Assert.False(RenderCorpus.Compare(good, Path.Combine(scratch.Path, "missing.png"), CorpusClass.Exact).Pass);
    }

    [Fact]
    public void Compare_FullyTransparentPixels_IgnoreTheirHiddenColour()
    {
        using var scratch = new TempDir();
        var a = scratch.UnpremulPng("a.png", 10, 10, (x, y) => new SKColor(255, 0, 0, 0));
        var b = scratch.UnpremulPng("b.png", 10, 10, (x, y) => new SKColor(0, 255, 0, 0));
        var r = RenderCorpus.Compare(a, b, CorpusClass.Exact);
        Assert.True(r.Pass, r.Note);
        Assert.Equal(0, r.MaxDiff);

        // A visible pixel (alpha 255) with a different colour is a real difference.
        var c = scratch.UnpremulPng("c.png", 10, 10, (x, y) => new SKColor(255, 0, 0, 255));
        var d = scratch.UnpremulPng("d.png", 10, 10, (x, y) => new SKColor(0, 255, 0, 255));
        Assert.Equal(255, RenderCorpus.Compare(c, d, CorpusClass.Exact).MaxDiff);

        // Alpha itself always counts.
        var e = scratch.UnpremulPng("e.png", 10, 10, (x, y) => new SKColor(255, 0, 0, 40));
        Assert.Equal(40, RenderCorpus.Compare(a, e, CorpusClass.Exact).MaxDiff);
    }

    // ------------------------------------------------------------------ animated

    [Fact]
    public void Compare_IdenticalGifs_PassAnimated()
    {
        using var scratch = new TempDir();
        var a = scratch.Gif("a.gif", new[] { Color.Red, Color.Blue, Color.Green }, 10, 0);
        var b = scratch.Gif("b.gif", new[] { Color.Red, Color.Blue, Color.Green }, 10, 0);

        var r = RenderCorpus.Compare(a, b, CorpusClass.Animated);
        Assert.True(r.Pass, r.Note);
        Assert.Equal(0, r.MaxDiff);
    }

    [Fact]
    public void Compare_GifFrameCountMismatch_FailsAnimated()
    {
        using var scratch = new TempDir();
        var a = scratch.Gif("a.gif", new[] { Color.Red, Color.Blue, Color.Green }, 10, 0);
        var b = scratch.Gif("b.gif", new[] { Color.Red, Color.Blue, Color.Green, Color.Red }, 10, 0);

        var r = RenderCorpus.Compare(a, b, CorpusClass.Animated);
        Assert.False(r.Pass);
        Assert.Contains("frame count 4 differs from 3", r.Note);
    }

    [Fact]
    public void Compare_GifDelayMismatch_FailsAnimated()
    {
        using var scratch = new TempDir();
        var a = scratch.Gif("a.gif", new[] { Color.Red, Color.Blue }, 10, 0);
        var b = scratch.Gif("b.gif", new[] { Color.Red, Color.Blue }, 12, 0);

        var r = RenderCorpus.Compare(a, b, CorpusClass.Animated);
        Assert.False(r.Pass);
        Assert.Contains("delays", r.Note);
        Assert.Equal(0, r.MaxDiff);
    }

    [Fact]
    public void Compare_GifLoopMismatch_FailsAnimated()
    {
        using var scratch = new TempDir();
        var a = scratch.Gif("a.gif", new[] { Color.Red, Color.Blue }, 10, 0);
        var b = scratch.Gif("b.gif", new[] { Color.Red, Color.Blue }, 10, 1);

        var r = RenderCorpus.Compare(a, b, CorpusClass.Animated);
        Assert.False(r.Pass);
        Assert.Contains("loop", r.Note);
    }

    [Fact]
    public void Compare_GifFramePixelsOutsideLoose_FailsAnimated_OnTheWorstFrame()
    {
        using var scratch = new TempDir();
        var a = scratch.Gif("a.gif", new[] { Color.Red, Color.Blue }, 10, 0);
        var b = scratch.Gif("b.gif", new[] { Color.Red, Color.Green }, 10, 0);

        var r = RenderCorpus.Compare(a, b, CorpusClass.Animated);
        Assert.False(r.Pass);
        Assert.True(r.Mean > 4);
        Assert.Contains("mean", r.Note);
    }

    [Fact]
    public void Compare_Animated_RequiresMoreThanOneFrame_InBothImages()
    {
        using var scratch = new TempDir();
        var one = scratch.Gif("a.gif", new[] { Color.Red }, 10, 0);
        var r = RenderCorpus.Compare(one, one, CorpusClass.Animated);
        Assert.False(r.Pass);
        Assert.Contains("more than one frame", r.Note);
    }

    [Fact]
    public void Compare_TruncatedImage_IsAFailureNotAPartialSuccess()
    {
        using var scratch = new TempDir();
        var good = scratch.Png("a.png", 64, 64, (x, y) => new SKColor((byte)(x * 4), (byte)(y * 4), (byte)(x ^ y), 255));
        var bytes = File.ReadAllBytes(good);
        var cut = Path.Combine(scratch.Path, "cut.png");
        File.WriteAllBytes(cut, bytes[..(bytes.Length * 2 / 3)]);

        var r = RenderCorpus.Compare(good, cut, CorpusClass.Loose);
        Assert.False(r.Pass);
        Assert.Contains("candidate unreadable", r.Note);
    }

    [Fact]
    public void Compare_Exact_FailsOnAnAlphaOnlyDifference_AndRgbBoundsIgnoreAlphaWhenOpaque()
    {
        using var scratch = new TempDir();
        var a = scratch.UnpremulPng("a.png", 10, 10, (x, y) => new SKColor(10, 20, 30, 255));
        var b = scratch.UnpremulPng("b.png", 10, 10, (x, y) => new SKColor(10, 20, 30, 250));
        var exact = RenderCorpus.Compare(a, b, CorpusClass.Exact);
        Assert.False(exact.Pass);
        Assert.Contains("alpha max diff 5", exact.Note);

        // Once an image has a non-opaque pixel, alpha joins the pooled statistics (4 channels).
        var c = scratch.UnpremulPng("c.png", 10, 10, (x, y) => new SKColor(30, 20, 30, 255));
        var d = scratch.UnpremulPng("d.png", 10, 10, (x, y) => new SKColor(10, 20, 30, 255));
        Assert.Equal(20 / 3d, RenderCorpus.Compare(c, d, CorpusClass.Loose).Mean, 6);
        Assert.Equal((20 + 5) / 4d, RenderCorpus.Compare(c, b, CorpusClass.Loose).Mean, 6);
    }

    [Fact]
    public async Task CompareRun_MissingRunOutput_Fails_AndSizeMismatchGetsAPlaceholderInTheIndex()
    {
        using var scratch = new TempDir();
        var fonts = scratch.Sub("fonts");
        File.WriteAllBytes(Path.Combine(fonts, "sub.ttf"), new byte[] { 0 });
        Func<CorpusCase, bool> pick = c => c.Name == "01-solid" || c.Name == "02-solid-vignette";

        var baselineDir = scratch.Sub("baseline");
        await RenderCorpus.RenderAsync(baselineDir, fonts, filter: pick);
        var runDir = scratch.Sub("run-x");
        var runOut = new Dictionary<string, string>(
            await RenderCorpus.RenderAsync(runDir, fonts, Path.Combine(baselineDir, "data", "customcoverart", "inputs"), pick));

        // 02 is replaced by a picture of the wrong size; 01 is dropped from the outputs.
        var wrong = scratch.Png("wrong.png", 10, 10, (x, y) => SKColors.Red);
        File.Copy(wrong, runOut["02-solid-vignette"], overwrite: true);
        runOut.Remove("01-solid");

        var results = RenderCorpus.CompareRun(baselineDir, runDir, runOut, pick);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Case.Name == "01-solid" && !r.Result.Pass && r.Result.Note == "no run output");
        Assert.Contains(results, r => r.Case.Name == "02-solid-vignette" && !r.Result.Pass && r.Result.Note.Contains("size 10x10"));
        var html = File.ReadAllText(Path.Combine(runDir, "index.html"));
        Assert.DoesNotContain("diff/02-solid-vignette.png", html);
        Assert.DoesNotContain("diff/01-solid.png", html);
        Assert.Contains("no diff image", html);
    }

    [Fact]
    public async Task Case34_Passthrough_KeepsTheThreeFramesOfItsSource()
    {
        using var scratch = new TempDir();
        var fonts = scratch.Sub("fonts");
        File.WriteAllBytes(Path.Combine(fonts, "sub.ttf"), new byte[] { 0 });
        var outputs = await RenderCorpus.RenderAsync(scratch.Sub("o"), fonts, filter: c => c.Name == "34-anim-passthrough");

        var d = RenderCorpus.Decode(outputs["34-anim-passthrough"]);
        Assert.Equal(3, d.Frames.Count);
        foreach (var f in d.Frames)
        {
            f.Dispose();
        }
    }

    [Fact]
    public void DimCase_DimsASolidImageWithADistinctColour()
    {
        var c = Assert.Single(RenderCorpus.Cases, c => c.Name == "36-solid-image-dim");
        Assert.Equal(CorpusClass.Exact, c.Class);
        var doc = c.Build(RenderCorpus.InputsIn("x"));
        Assert.True(doc.Background.Dim > 0);
        Assert.Equal(BackgroundImageName, Path.GetFileName(doc.Background.ImagePath));
        Assert.NotEqual("#3d8b6e", doc.Background.DimColor);
    }

    private const string BackgroundImageName = "solid.png";

    [Fact]
    public void Decode_ReadsGifFramesAndDelaysInMilliseconds()
    {
        using var scratch = new TempDir();
        var path = scratch.Gif("a.gif", new[] { Color.Red, Color.Blue, Color.Red }, 7, 0);

        var d = RenderCorpus.Decode(path);
        Assert.Equal(3, d.Frames.Count);
        Assert.All(d.DelaysMs, ms => Assert.Equal(70, ms));
        Assert.Equal(-1, d.RepetitionCount);
        Assert.Equal(new SKColor(255, 0, 0, 255), d.Frames[0].GetPixel(2, 2));
        Assert.Equal(new SKColor(0, 0, 255, 255), d.Frames[1].GetPixel(2, 2));
        foreach (var f in d.Frames)
        {
            f.Dispose();
        }
    }

    // --------------------------------------------------------------- text geometry

    private static readonly SKColor InkBg = new(0x3a, 0x5a, 0x7a);

    private static string InkImage(TempDir scratch, string name, int left, int top, int w, int h)
        => scratch.Png(name, 120, 80, (x, y) => x >= left && x < left + w && y >= top && y < top + h ? SKColors.White : InkBg);

    [Fact]
    public void Compare_Text_IdenticalInk_Passes()
    {
        using var scratch = new TempDir();
        var a = InkImage(scratch, "a.png", 40, 30, 30, 10);
        var b = InkImage(scratch, "b.png", 40, 30, 30, 10);
        var r = RenderCorpus.Compare(a, b, CorpusClass.Text, InkBg);
        Assert.True(r.Pass, r.Note);
    }

    [Fact]
    public void Compare_Text_WithinGeometryLimits_Passes()
    {
        using var scratch = new TempDir();
        var a = InkImage(scratch, "a.png", 40, 30, 30, 10);
        // One pixel right and one down: centroid 1 px, edges 1 px, same mass.
        var b = InkImage(scratch, "b.png", 41, 31, 30, 10);
        var r = RenderCorpus.Compare(a, b, CorpusClass.Text, InkBg);
        Assert.True(r.Pass, r.Note);
    }

    [Fact]
    public void Compare_Text_ShiftedGlyphBlock_FailsGeometry_EvenThoughThePixelBoundPasses()
    {
        using var scratch = new TempDir();
        var a = InkImage(scratch, "a.png", 40, 30, 30, 10);
        var b = InkImage(scratch, "b.png", 44, 30, 30, 10);

        var loose = RenderCorpus.Compare(a, b, CorpusClass.Loose);
        Assert.True(loose.Pass, "pixel statistics alone do not see the shift: " + loose.Note);

        var text = RenderCorpus.Compare(a, b, CorpusClass.Text, InkBg);
        Assert.False(text.Pass);
        Assert.Contains("centroid", text.Note);
        Assert.Contains("bounding box", text.Note);
    }

    [Fact]
    public void Compare_Text_MassChangeBeyondTwelvePercent_Fails()
    {
        using var scratch = new TempDir();
        var a = InkImage(scratch, "a.png", 40, 30, 30, 10);
        var b = InkImage(scratch, "b.png", 40, 30, 30, 13); // 30 % more ink, bottom edge +3, centroid +1.5
        var r = RenderCorpus.Compare(a, b, CorpusClass.Text, InkBg);
        Assert.False(r.Pass);
        Assert.Contains("mass", r.Note);
        Assert.DoesNotContain("centroid", r.Note);
        Assert.DoesNotContain("bounding box", r.Note);
    }

    [Fact]
    public void Compare_Text_StrayInkExtendingTheBoundingBox_Fails()
    {
        using var scratch = new TempDir();
        var a = InkImage(scratch, "a.png", 40, 30, 30, 10);
        // Same block plus a 1 px line sticking out 8 px to the right: tiny mass, but the bbox grows.
        var b = scratch.Png("b.png", 120, 80, (x, y) =>
            (x >= 40 && x < 70 && y >= 30 && y < 40) || (y == 35 && x >= 70 && x < 78) ? SKColors.White : InkBg);
        var r = RenderCorpus.Compare(a, b, CorpusClass.Text, InkBg);
        Assert.False(r.Pass);
        Assert.Contains("bounding box edge moved 8", r.Note);
    }

    [Fact]
    public void Compare_Text_AntialiasedEdgesWeighFractionally()
    {
        using var scratch = new TempDir();
        // A half-covered column of grey next to a solid block: ink mass counts it at its strength, not as a full pixel.
        var edge = new SKColor(0x3a + 0x40, 0x5a + 0x40, 0x7a + 0x40);
        var bmp = scratch.Png("a.png", 20, 10, (x, y) => x < 5 ? SKColors.White : x == 5 ? edge : InkBg);
        var decoded = RenderCorpus.Decode(bmp);
        var ink = RenderCorpus.MeasureInk(decoded.Frames[0], InkBg)!.Value;
        decoded.Frames[0].Dispose();
        Assert.Equal(0, ink.MinX);
        Assert.Equal(5, ink.MaxX);
        Assert.Equal(9, ink.MaxY);
        // White differs from the background by max(255-0x3a, 255-0x5a, 255-0x7a) = 197.
        double expected = 10 * (5 * 197 / 255.0 + 0x40 / 255.0);
        Assert.Equal(expected, ink.Mass, 6);
    }

    [Fact]
    public void Compare_Text_WithoutAnInkBackground_FailsLoudly()
    {
        using var scratch = new TempDir();
        var a = InkImage(scratch, "a.png", 40, 30, 30, 10);
        var r = RenderCorpus.Compare(a, a, CorpusClass.Text);
        Assert.False(r.Pass);
        Assert.Contains("ink background", r.Note);
    }

    [Fact]
    public void Compare_Text_NoInkAtAll_Fails_BecauseABlankTextCaseIsBroken()
    {
        using var scratch = new TempDir();
        var a = scratch.Png("a.png", 60, 40, (x, y) => InkBg);
        var r = RenderCorpus.Compare(a, a, CorpusClass.Text, InkBg);
        Assert.False(r.Pass);
        Assert.Contains("no ink", r.Note);
    }

    [Fact]
    public void MeasureInk_IgnoresDifferencesAtOrBelowTheThreshold()
    {
        using var scratch = new TempDir();
        var faint = new SKColor((byte)(InkBg.Red + RenderCorpus.InkThreshold), InkBg.Green, InkBg.Blue);
        var path = scratch.Png("a.png", 10, 10, (x, y) => faint);
        var decoded = RenderCorpus.Decode(path);
        Assert.Null(RenderCorpus.MeasureInk(decoded.Frames[0], InkBg));
        decoded.Frames[0].Dispose();
    }

    // ------------------------------------------------------------------ helpers

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cca_corpus_" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public string Sub(string name)
        {
            var p = System.IO.Path.Combine(Path, name);
            Directory.CreateDirectory(p);
            return p;
        }

        public string Png(string name, int w, int h, Func<int, int, SKColor> pixel)
        {
            using var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    bitmap.SetPixel(x, y, pixel(x, y));
                }
            }

            return Save(name, bitmap);
        }

        public string UnpremulPng(string name, int w, int h, Func<int, int, SKColor> pixel)
        {
            using var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            var span = bitmap.GetPixelSpan();
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    var c = pixel(x, y);
                    int o = (y * w + x) * 4;
                    span[o] = c.Red;
                    span[o + 1] = c.Green;
                    span[o + 2] = c.Blue;
                    span[o + 3] = c.Alpha;
                }
            }

            return Save(name, bitmap);
        }

        private string Save(string name, SKBitmap bitmap)
        {
            var file = System.IO.Path.Combine(Path, name);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(file, data.ToArray());
            return file;
        }

        /// <summary>A 16x16 GIF with one flat-colour frame each; delay in centiseconds, repeat count 0 = forever.</summary>
        public string Gif(string name, Color[] frames, int delayCentis, int repeat)
        {
            using var gif = new Image<Rgba32>(16, 16, frames[0]);
            gif.Frames.RootFrame.Metadata.GetGifMetadata().FrameDelay = delayCentis;
            for (int i = 1; i < frames.Length; i++)
            {
                using var f = new Image<Rgba32>(16, 16, frames[i]);
                f.Frames.RootFrame.Metadata.GetGifMetadata().FrameDelay = delayCentis;
                gif.Frames.AddFrame(f.Frames.RootFrame);
            }

            gif.Metadata.GetGifMetadata().RepeatCount = (ushort)repeat;
            var file = System.IO.Path.Combine(Path, name);
            gif.SaveAsGif(file);
            return file;
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
