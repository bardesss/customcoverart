using CustomCoverArt.Models;

namespace CustomCoverArt.Services;

/// <summary>Converts a legacy flat CoverArtSettings into a one-text-layer CoverDocument.
/// Pure (no I/O) so every legacy path can route through the document renderer.</summary>
public static class DocumentMigration
{
    public static CoverDocument FromSettings(CoverArtSettings s)
    {
        var doc = new CoverDocument
        {
            Canvas = new CanvasSettings
            {
                Width = s.ExportWidth,
                Height = s.ExportHeight,
                Format = string.IsNullOrEmpty(s.OutputFormat) ? "auto" : s.OutputFormat,
                DimensionPreset = s.DimensionPreset,
            },
            Background = new BackgroundLayer
            {
                Source = s.BackgroundSource,
                ImagePath = s.BackgroundImagePath,
                Fit = s.BackgroundFit,
                Blur = s.BackgroundBlur,
                Dim = s.BackgroundDim,
                DimColor = s.DimColor,
                Gradient = s.BackgroundGradient,
                Collage = s.Collage,
                Animation = s.Animation,
                Transform = new BackgroundTransform(), // identity
            },
        };

        var height = s.ExportHeight <= 0 ? 1400 : s.ExportHeight;
        var (x, y) = AnchorFor(s.TextAlign, s.TextBaseline, s.TextPadding);
        doc.Layers.Add(new CoverLayer
        {
            Id = "title",
            Type = "text",
            Content = s.Title,
            Size = s.TextSize / (float)height,
            Weight = s.TextWeight,
            Color = s.TextColor,
            Align = s.TextAlign,
            FontPath = s.CustomFontPath,
            X = x,
            Y = y,
            Shadow = new TextShadowSettings
            {
                Enabled = s.TextShadow, Color = s.TextShadowColor, Blur = s.TextShadowBlur,
                OffsetX = s.TextShadowOffsetX, OffsetY = s.TextShadowOffsetY,
            },
            Outline = new TextOutlineSettings
            {
                Enabled = s.TextOutline, Color = s.TextOutlineColor, Width = s.TextOutlineWidth,
            },
        });

        return doc;
    }

    /// <summary>
    /// Ensures the nested objects a client-built <see cref="CoverDocument"/> is expected to
    /// have are non-null. System.Text.Json does not enforce non-null on non-nullable
    /// reference types, so a client can POST a partial/malformed document (e.g.
    /// <c>"background": null</c> or <c>"layers": null</c>). Every entry point that accepts a
    /// client-supplied <see cref="CoverDocument"/> (document/preview, document/apply,
    /// SaveTemplate's NormalizeTemplate) should call this first so a bad body degrades
    /// gracefully instead of NRE-ing. Mutates and returns the same instance.
    /// </summary>
    public static CoverDocument Normalize(CoverDocument doc)
    {
        doc.Canvas ??= new CanvasSettings();
        doc.Background ??= new BackgroundLayer();
        doc.Background.Transform ??= new BackgroundTransform();
        doc.Effects ??= new EffectSettings();
        // The effect sub-objects are dereferenced unconditionally by EffectsComposer,
        // and a client can POST any of them as explicit null (System.Text.Json does not
        // enforce non-null on non-nullable reference types, so the initializers above
        // are overwritten rather than kept).
        doc.Effects.Border ??= new BorderSettings();
        doc.Effects.Vignette ??= new VignetteSettings();
        doc.Effects.Grain ??= new GrainSettings();
        doc.Effects.SoftLight ??= new SoftLightSettings();
        doc.Layers = (doc.Layers ?? new List<CoverLayer>())
            .Where(l => l is not null)
            .ToList();
        foreach (var layer in doc.Layers)
        {
            layer.Shadow ??= new TextShadowSettings();
            layer.Outline ??= new TextOutlineSettings();
        }
        return doc;
    }

    private static (float X, float Y) AnchorFor(TextAlign align, TextBaseline baseline, float padding)
    {
        var x = align switch
        {
            TextAlign.Left => padding,
            TextAlign.Right => 1f - padding,
            _ => 0.5f,
        };
        var y = baseline switch
        {
            TextBaseline.Top => padding,
            TextBaseline.Bottom => 1f - padding,
            _ => 0.5f,
        };
        return (x, y);
    }
}
