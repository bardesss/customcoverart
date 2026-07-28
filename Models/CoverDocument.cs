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

/// <summary>Composition effects. Empty in Phase 1; populated in Phase 3.</summary>
public class EffectSettings
{
    public string? Preset { get; set; }
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
