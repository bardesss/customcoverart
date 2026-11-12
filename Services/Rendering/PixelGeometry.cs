namespace CustomCoverArt.Services.Rendering;

/// <summary>Integer pixel rectangle; the rendering layer's own type so geometry helpers do not depend on an imaging library.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;
}

/// <summary>Integer pixel size.</summary>
public readonly record struct PixelSize(int Width, int Height);
