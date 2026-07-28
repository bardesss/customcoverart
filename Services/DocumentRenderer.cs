using CustomCoverArt.Models;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace CustomCoverArt.Services;

/// <summary>
/// Compositor: renders a <see cref="CoverDocument"/> onto an ImageSharp canvas.
/// Shared by the single-image and animated paths; extended by later phases
/// (image layers, effects). This is the sole owner of background/gradient/font/
/// text-drawing logic — moved here from <see cref="CoverArtService"/> so both
/// the legacy flat-settings path (via <see cref="DocumentMigration"/>) and
/// future document-native endpoints render through the same code.
/// </summary>
public static class DocumentRenderer
{
    /// <summary>
    /// Composites one frame (background + visible layers) onto the supplied canvas.
    /// </summary>
    public static void ComposeDocumentFrame(Image<Rgba32> canvas, Image? background, CoverDocument doc)
    {
        if (background is not null)
        {
            ApplyBackgroundLayer(canvas, background, doc.Background);
        }
        else
        {
            CreateGradientBackground(canvas, doc.Background);
        }

        foreach (var layer in doc.Layers)
        {
            if (!layer.Visible) { continue; }

            if (layer.Type == "text")
            {
                RenderTextLayerWithFallback(canvas, layer, doc);
            }
            // image layers: added in Phase 2
        }
    }

    // Parses a #rrggbb colour, falling back to a default instead of throwing so
    // a malformed colour in the request body cannot abort the whole render.
    internal static Color SafeColor(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return fallback;
        }

        try
        {
            return Color.ParseHex(hex);
        }
        catch
        {
            return fallback;
        }
    }

    internal static void ApplyBackgroundLayer(Image<Rgba32> image, Image backgroundImage, BackgroundLayer bg)
    {
        var fit = (bg.Fit ?? "cover").Trim().ToLowerInvariant();
        var baseColor = SafeColor(bg.DimColor, Color.Black);

        if (fit == "stretch")
        {
            // Distort to fill the whole canvas exactly.
            backgroundImage.Mutate(x => x.Resize(image.Width, image.Height));
            if (bg.Blur > 0)
            {
                backgroundImage.Mutate(x => x.GaussianBlur(bg.Blur));
            }
            image.Mutate(x => x.DrawImage(backgroundImage, Point.Empty, 1f));
        }
        else if (fit == "contain")
        {
            // Fit the whole image inside the canvas (like background-size: contain),
            // letterboxing the remainder with the base colour.
            image.Mutate(x => x.Fill(baseColor));

            var scale = Math.Min((float)image.Width / backgroundImage.Width,
                                 (float)image.Height / backgroundImage.Height);
            var w = Math.Max(1, (int)Math.Round(backgroundImage.Width * scale));
            var h = Math.Max(1, (int)Math.Round(backgroundImage.Height * scale));
            backgroundImage.Mutate(x => x.Resize(w, h));
            if (bg.Blur > 0)
            {
                backgroundImage.Mutate(x => x.GaussianBlur(bg.Blur));
            }

            var px = (image.Width - w) / 2;
            var py = (image.Height - h) / 2;
            image.Mutate(x => x.DrawImage(backgroundImage, new Point(px, py), 1f));
        }
        else
        {
            // "cover" (default): scale to fill the canvas keeping the source aspect
            // ratio, then centre-crop (like CSS background-size: cover).
            var scale = Math.Max((float)image.Width / backgroundImage.Width,
                                 (float)image.Height / backgroundImage.Height);
            var newWidth = Math.Max(image.Width, (int)Math.Ceiling(backgroundImage.Width * scale));
            var newHeight = Math.Max(image.Height, (int)Math.Ceiling(backgroundImage.Height * scale));

            backgroundImage.Mutate(x => x.Resize(newWidth, newHeight));

            var offsetX = (newWidth - image.Width) / 2;
            var offsetY = (newHeight - image.Height) / 2;
            backgroundImage.Mutate(x => x.Crop(new Rectangle(offsetX, offsetY, image.Width, image.Height)));

            if (bg.Blur > 0)
            {
                backgroundImage.Mutate(x => x.GaussianBlur(bg.Blur));
            }
            image.Mutate(x => x.DrawImage(backgroundImage, Point.Empty, 1f));
        }

        // Apply dimming by compositing a solid overlay at fractional opacity.
        // IMPORTANT: do NOT use Fill() with a semi-transparent SolidBrush here —
        // on backgrounds decoded without an alpha channel (JPEG posters load as
        // Rgb24) Fill ignores the brush alpha and paints fully opaque, which
        // blacked out the entire background for any non-zero dim. DrawImage with
        // an opacity blends correctly on every pixel format. Text is drawn later,
        // so only the background is dimmed.
        if (bg.Dim > 0)
        {
            using var dimOverlay = new Image<Rgba32>(image.Width, image.Height, baseColor);
            image.Mutate(x => x.DrawImage(dimOverlay, Point.Empty, bg.Dim));
        }
    }

    internal static void CreateGradientBackground(Image<Rgba32> image, BackgroundLayer bg)
    {
        if (bg.Gradient?.IsEnabled == true)
        {
            ApplyGradientBackground(image, bg.Gradient);
        }
        else
        {
            var backgroundColor = SafeColor(bg.DimColor, Color.Black);
            image.Mutate(x => x.Fill(backgroundColor));
        }
    }

    /// <summary>
    /// Applies a multi-stop gradient background (linear at a given angle, or radial).
    /// </summary>
    internal static void ApplyGradientBackground(Image<Rgba32> image, GradientSettings gradient)
    {
        var stops = BuildColorStops(gradient);

        if (gradient.Type == GradientType.Radial)
        {
            var centerX = gradient.CenterX * image.Width;
            var centerY = gradient.CenterY * image.Height;
            var radius = Math.Max(1f, gradient.Radius * Math.Min(image.Width, image.Height));

            var brush = new RadialGradientBrush(new PointF(centerX, centerY), radius, GradientRepetitionMode.None, stops);
            image.Mutate(x => x.Fill(brush));
        }
        else
        {
            // Linear: a line through the centre at `Angle` degrees, long enough to
            // span the whole canvas so the gradient covers it corner to corner.
            var rad = gradient.Angle * Math.PI / 180.0;
            var dx = (float)Math.Cos(rad);
            var dy = (float)Math.Sin(rad);
            var cx = image.Width / 2f;
            var cy = image.Height / 2f;
            var half = (Math.Abs(dx) * image.Width + Math.Abs(dy) * image.Height) / 2f;

            var p0 = new PointF(cx - dx * half, cy - dy * half);
            var p1 = new PointF(cx + dx * half, cy + dy * half);

            var brush = new LinearGradientBrush(p0, p1, GradientRepetitionMode.None, stops);
            image.Mutate(x => x.Fill(brush));
        }
    }

    /// <summary>
    /// Builds the colour stops from the gradient settings — the explicit Stops
    /// list if it has 2+ entries, otherwise the Start/End colours as a fallback.
    /// </summary>
    internal static ColorStop[] BuildColorStops(GradientSettings gradient)
    {
        if (gradient.Stops is { Count: >= 2 })
        {
            return gradient.Stops
                .OrderBy(s => s.Position)
                .Select(s => new ColorStop(Math.Clamp(s.Position, 0f, 1f), SafeColor(s.Color, Color.Gray)))
                .ToArray();
        }

        return new[]
        {
            new ColorStop(0f, SafeColor(gradient.StartColor, Color.Black)),
            new ColorStop(1f, SafeColor(gradient.EndColor, Color.White))
        };
    }

    /// <summary>
    /// Renders a text layer: main text plus optional shadow/outline effects.
    /// Coordinates are normalized on the layer (0..1 of canvas width/height);
    /// converted here to pixels against the document's canvas.
    /// </summary>
    internal static void RenderTextLayer(Image<Rgba32> canvas, CoverLayer layer, CoverDocument doc)
    {
        var fontPixelSize = layer.Size * doc.Canvas.Height;
        var font = CreateFont(layer, fontPixelSize);

        // Parse text color
        var textColor = SafeColor(layer.Color, Color.White);

        // Convert normalized layer coordinates to canvas pixels.
        var textPosition = new PointF(layer.X * canvas.Width, layer.Y * canvas.Height);

        // Create text options
        var textOptions = new RichTextOptions(font)
        {
            Origin = textPosition,
            HorizontalAlignment = GetHorizontalAlignment(layer.Align),
            VerticalAlignment = VerticalAlignment.Center
        };

        // Apply text effects
        if (layer.Shadow.Enabled)
        {
            var shadowColor = SafeColor(layer.Shadow.Color, Color.Black);
            var shadowPosition = new PointF(
                textPosition.X + layer.Shadow.OffsetX,
                textPosition.Y + layer.Shadow.OffsetY
            );

            var shadowOptions = new RichTextOptions(font)
            {
                Origin = shadowPosition,
                HorizontalAlignment = textOptions.HorizontalAlignment,
                VerticalAlignment = textOptions.VerticalAlignment
            };

            canvas.Mutate(x => x.DrawText(shadowOptions, layer.Content, shadowColor));
        }

        if (layer.Outline.Enabled)
        {
            var outlineColor = SafeColor(layer.Outline.Color, Color.Black);
            var outlineWidth = Math.Clamp(layer.Outline.Width, 0, 10);

            // Draw outline by drawing text multiple times with slight offsets
            for (int x = -outlineWidth; x <= outlineWidth; x++)
            {
                for (int y = -outlineWidth; y <= outlineWidth; y++)
                {
                    if (x == 0 && y == 0) continue;

                    var outlinePosition = new PointF(textPosition.X + x, textPosition.Y + y);
                    var outlineOptions = new RichTextOptions(font)
                    {
                        Origin = outlinePosition,
                        HorizontalAlignment = textOptions.HorizontalAlignment,
                        VerticalAlignment = textOptions.VerticalAlignment
                    };

                    canvas.Mutate(ctx => ctx.DrawText(outlineOptions, layer.Content, outlineColor));
                }
            }
        }

        // Draw main text
        canvas.Mutate(x => x.DrawText(textOptions, layer.Content, textColor));
    }

    /// <summary>
    /// Renders a text layer with fallback mechanisms if the primary render throws.
    /// </summary>
    internal static void RenderTextLayerWithFallback(Image<Rgba32> canvas, CoverLayer layer, CoverDocument doc)
    {
        try
        {
            RenderTextLayer(canvas, layer, doc);
        }
        catch
        {
            // Fallback: simple text overlay without advanced effects.
            try
            {
                var fallbackFontPx = Math.Max(12, layer.Size * doc.Canvas.Height * 0.5f);
                var font = BundledFamily(FontWeight.Normal).CreateFont(fallbackFontPx);
                var textColor = SafeColor(layer.Color, Color.White);
                var position = new PointF(canvas.Width / 2f, canvas.Height / 2f);

                canvas.Mutate(x => x.DrawText(layer.Content, font, textColor, position));
            }
            catch
            {
                // Ultimate fallback: Just fill with the dim/background color
                var backgroundColor = SafeColor(doc.Background.DimColor, Color.Black);
                canvas.Mutate(x => x.Fill(backgroundColor));
            }
        }
    }

    private static HorizontalAlignment GetHorizontalAlignment(TextAlign textAlign)
    {
        return textAlign switch
        {
            TextAlign.Left => HorizontalAlignment.Left,
            TextAlign.Right => HorizontalAlignment.Right,
            TextAlign.Center => HorizontalAlignment.Center,
            _ => HorizontalAlignment.Center
        };
    }

    // Bundled fonts (Noto Sans, SIL OFL-1.1 — the same family Jellyfin's web UI
    // uses), embedded so text ALWAYS renders, even on Jellyfin Docker images
    // that ship no system fonts. Every UI weight has its own face so the font
    // weight control actually changes the rendered thickness.
    private static readonly FontCollection BundledFonts = new();
    private static readonly object FontLock = new();
    private static readonly Dictionary<FontWeight, FontFamily> BundledFamilies = new();

    private static FontFamily BundledFamily(FontWeight weight)
    {
        lock (FontLock)
        {
            if (BundledFamilies.TryGetValue(weight, out var existing))
            {
                return existing;
            }

            var faceName = weight switch
            {
                FontWeight.Light => "NotoSans-Light",
                FontWeight.Medium => "NotoSans-Medium",
                FontWeight.SemiBold => "NotoSans-SemiBold",
                FontWeight.Bold => "NotoSans-Bold",
                FontWeight.ExtraBold => "NotoSans-ExtraBold",
                _ => "NotoSans-Regular" // Normal and any unmapped value
            };

            var resource = $"CustomCoverArt.Resources.fonts.{faceName}.ttf";
            using var stream = typeof(DocumentRenderer).Assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Bundled font not found: {resource}");

            var family = BundledFonts.Add(stream);
            BundledFamilies[weight] = family;
            return family;
        }
    }

    /// <summary>
    /// Creates a font: a client-uploaded custom font if one was provided,
    /// otherwise the bundled Noto Sans face for the requested weight.
    /// </summary>
    internal static Font CreateFont(CoverLayer layer, float fontPixelSize)
    {
        // Custom uploaded font takes priority (its path is already sandboxed).
        if (!string.IsNullOrEmpty(layer.FontPath) && File.Exists(layer.FontPath))
        {
            try
            {
                var fontCollection = new FontCollection();
                var fontFamily = fontCollection.Add(layer.FontPath);
                return fontFamily.CreateFont(fontPixelSize);
            }
            catch
            {
                // Fall back to the bundled font if the custom one fails to load.
            }
        }

        return BundledFamily(layer.Weight).CreateFont(fontPixelSize);
    }
}
