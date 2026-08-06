using System.Collections.Generic;

namespace CustomCoverArt.Models;

/// <summary>The layered cover design consumed by both the client canvas and the server renderer.</summary>
public class CoverDocument
{
    public int Version { get; set; } = 2;
    public CanvasSettings Canvas { get; set; } = new();
    public BackgroundLayer Background { get; set; } = new();
    public EffectSettings Effects { get; set; } = new();
    public List<CoverLayer> Layers { get; set; } = new();
}

public class CanvasSettings
{
    public int Width { get; set; } = 1400;
    public int Height { get; set; } = 1400;
    public string Format { get; set; } = "auto";           // auto|png|gif
    public string DimensionPreset { get; set; } = "cover";
}

/// <summary>Background source, effects on it, and the pan/zoom transform.</summary>
public class BackgroundLayer
{
    public string Source { get; set; } = "upload";          // upload|poster|collage|none
    public string ImagePath { get; set; } = string.Empty;
    public string Fit { get; set; } = "cover";              // cover|contain|stretch
    public BackgroundTransform Transform { get; set; } = new();
    public float Blur { get; set; }
    public float Dim { get; set; } = 0.25f;
    public string DimColor { get; set; } = "#000000";
    public GradientSettings? Gradient { get; set; }

    /// <summary>
    /// Optional colour gradient composited OVER the finished background and UNDER the
    /// layers, for the "poster fading into solid colour" look. Null means no overlay.
    /// Linear only: the Type/Center/Radius fields it inherits from the reused
    /// GradientSettings type are inert here — see ApplyGradientOverlay.
    /// </summary>
    public GradientSettings? Overlay { get; set; }
    public CollageSettings? Collage { get; set; }
    public AnimationSettings? Animation { get; set; }
}

/// <summary>User pan/zoom applied to the fitted background. Identity = OffsetX/Y 0, Scale 1.</summary>
public class BackgroundTransform
{
    public float OffsetX { get; set; }                      // normalized pan, -1..1
    public float OffsetY { get; set; }
    public float Scale { get; set; } = 1f;                  // >= 1
}

/// <summary>
/// Non-destructive composition effects, applied in a fixed order around the layers:
/// soft-light before them, then vignette → grain → border after. Everything defaults
/// to disabled so a document written before Phase 3 renders exactly as it always did.
/// </summary>
public class EffectSettings
{
    public BorderSettings Border { get; set; } = new();
    public VignetteSettings Vignette { get; set; } = new();
    public GrainSettings Grain { get; set; } = new();
    public SoftLightSettings SoftLight { get; set; } = new();
    public string? Preset { get; set; }
}

/// <summary>Inset frame drawn last, on top of everything else.</summary>
public class BorderSettings
{
    public bool Enabled { get; set; }
    public string Color { get; set; } = "#ffffff";
    public int Thickness { get; set; } = 8;
    public int Radius { get; set; }
    public bool Double { get; set; }
    public int Gap { get; set; } = 6;
}

/// <summary>Radial darkening toward the edges.</summary>
public class VignetteSettings
{
    public bool Enabled { get; set; }
    public float Amount { get; set; } = 0.4f;
    public float Softness { get; set; } = 0.5f;
    public string Color { get; set; } = "#000000";
}

/// <summary>Film grain. <see cref="Seed"/> is stored so re-renders reproduce the same noise.</summary>
public class GrainSettings
{
    public bool Enabled { get; set; }
    public float Amount { get; set; } = 0.08f;
    public int Seed { get; set; } = 12345;
}

/// <summary>Flat colour wash under the layers, for a tinted/washed look.</summary>
public class SoftLightSettings
{
    public bool Enabled { get; set; }
    public string Color { get; set; } = "#ffffff";
    public float Opacity { get; set; } = 0.15f;
}

/// <summary>One text or image layer. A single flat type (Type discriminator) keeps System.Text.Json simple.</summary>
public class CoverLayer
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = "text";              // text|image
    public bool Visible { get; set; } = true;
    public float X { get; set; } = 0.5f;                    // normalized center
    public float Y { get; set; } = 0.5f;
    public float Width { get; set; }                        // normalized (image layers)
    public float Height { get; set; }
    public float Rotation { get; set; }                     // degrees
    public float Opacity { get; set; } = 1f;

    // text layer
    public string Content { get; set; } = string.Empty;
    public float Size { get; set; } = 0.086f;               // fraction of canvas height (~120/1400)
    public FontWeight Weight { get; set; } = FontWeight.Normal;
    public string Color { get; set; } = "#ffffff";
    public TextAlign Align { get; set; } = TextAlign.Center;
    public string FontPath { get; set; } = string.Empty;
    public TextShadowSettings Shadow { get; set; } = new();
    public TextOutlineSettings Outline { get; set; } = new();

    // image layer
    public string ImagePath { get; set; } = string.Empty;
}

public class TextShadowSettings
{
    public bool Enabled { get; set; }
    public string Color { get; set; } = "#000000";
    public int Blur { get; set; } = 4;
    public int OffsetX { get; set; } = 2;
    public int OffsetY { get; set; } = 2;
}

public class TextOutlineSettings
{
    public bool Enabled { get; set; }
    public string Color { get; set; } = "#000000";
    public int Width { get; set; } = 2;
}
