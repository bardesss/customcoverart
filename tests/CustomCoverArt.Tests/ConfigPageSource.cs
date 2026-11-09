using System.IO;
using Xunit;

namespace CustomCoverArt.Tests;

/// <summary>
/// The config page ships as three embedded resources: markup (configPage.html), styles
/// (configPage.css) and the controller script (configPage.js). The structure tests were
/// written against the old single file, so <see cref="Bundle"/> reassembles the three
/// into that shape: the markup, then a &lt;style&gt; block, then a
/// &lt;script type="text/javascript"&gt; block. Helpers such as Markup(), Styles(),
/// FunctionBody() and HandlerBody() keep working whichever file a line now lives in.
/// Read a single part when a test is about that part.
/// </summary>
internal static class ConfigPageSource
{
    private const string Prefix = "CustomCoverArt.Configuration.";

    public static string Html() => Read("configPage.html");

    public static string Css() => Read("configPage.css");

    public static string Js() => Read("configPage.js");

    public static string Bundle() =>
        Html()
        + "\n<style>\n" + Css() + "\n</style>\n"
        + "<script type=\"text/javascript\">\n" + Js() + "\n</script>\n";

    private static string Read(string file)
    {
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(Prefix + file);
        Assert.True(stream is not null, $"Embedded resource {Prefix + file} not found.");
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }
}
