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
