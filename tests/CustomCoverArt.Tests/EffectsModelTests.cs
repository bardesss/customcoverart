using CustomCoverArt.Models;
using Xunit;

namespace CustomCoverArt.Tests;

public class EffectsModelTests
{
    /// <summary>
    /// Every effect is opt-in. A document that never touched the Effects card must
    /// render exactly as it did before Phase 3 — defaults that were "on" would
    /// silently restyle every saved design and applied cover.
    /// </summary>
    [Fact]
    public void EffectSettings_DefaultsAreDisabled()
    {
        var fx = new EffectSettings();
        Assert.False(fx.Border.Enabled);
        Assert.False(fx.Vignette.Enabled);
        Assert.False(fx.Grain.Enabled);
        Assert.False(fx.SoftLight.Enabled);
        Assert.Null(fx.Preset);
    }

    [Fact]
    public void BorderSettings_SaneDefaults()
    {
        var b = new BorderSettings();
        Assert.Equal("#ffffff", b.Color);
        Assert.Equal(0, b.Radius);
        Assert.False(b.Double);
    }

    /// <summary>
    /// The Phase 1 document carried an EffectSettings with only Preset on it. Old
    /// saved templates deserialize into the expanded shape with the nested objects
    /// present (not null), because the renderer dereferences them unconditionally.
    /// </summary>
    [Fact]
    public void EffectSettings_FromLegacyJson_HasNonNullSubObjects()
    {
        var fx = System.Text.Json.JsonSerializer.Deserialize<EffectSettings>("{\"Preset\":null}");

        Assert.NotNull(fx);
        Assert.NotNull(fx!.Border);
        Assert.NotNull(fx.Vignette);
        Assert.NotNull(fx.Grain);
        Assert.NotNull(fx.SoftLight);
    }

    /// <summary>
    /// An explicit null in the request body overwrites a property initializer, so
    /// Normalize has to put the effect sub-objects back before anything dereferences
    /// them — the same guard Background.Transform and layer Shadow/Outline already have.
    /// </summary>
    [Fact]
    public void Normalize_NullEffectSubObjects_AreRestored()
    {
        var doc = System.Text.Json.JsonSerializer.Deserialize<CoverDocument>(
            "{\"Effects\":{\"Border\":null,\"Vignette\":null,\"Grain\":null,\"SoftLight\":null}}")!;

        CustomCoverArt.Services.DocumentMigration.Normalize(doc);

        Assert.NotNull(doc.Effects.Border);
        Assert.NotNull(doc.Effects.Vignette);
        Assert.NotNull(doc.Effects.Grain);
        Assert.NotNull(doc.Effects.SoftLight);
        Assert.False(doc.Effects.Border.Enabled);
    }

    /// <summary>Grain must carry its seed so re-rendering a saved design reproduces the same noise.</summary>
    [Fact]
    public void GrainSettings_RoundTripsItsSeed()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new GrainSettings { Enabled = true, Amount = 0.2f, Seed = 4242 });
        var back = System.Text.Json.JsonSerializer.Deserialize<GrainSettings>(json)!;

        Assert.True(back.Enabled);
        Assert.Equal(4242, back.Seed);
        Assert.Equal(0.2f, back.Amount);
    }
}
