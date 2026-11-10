using Nadlan.Core.Analytics;
using UAParser;

namespace Nadlan.Host.Analytics;

/// <summary>
/// Browser / OS / device from the user agent (UA-Parser's rules, built into the package) and the client hints Chrome sends
/// by itself (Sec-CH-UA-Mobile, Sec-CH-UA-Platform). Phone, tablet or desktop is ours: UA-Parser doesn't say. An iPad
/// says "Macintosh" nowadays - a Mac with a touch screen is taken as an iPad.
/// </summary>
public sealed class UaParserAgentParser : IUserAgentParser
{
    private static readonly Parser Rules = Parser.GetDefault();

    public AgentInfo Parse(string? userAgent, IReadOnlyDictionary<string, string> clientHints, bool? touch)
    {
        var hintMobile = Hint(clientHints, "Sec-CH-UA-Mobile");
        var hintPlatform = Hint(clientHints, "Sec-CH-UA-Platform");
        var hintModel = Hint(clientHints, "Sec-CH-UA-Model");
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return new AgentInfo(null, null, Cut(hintPlatform, 50), null, hintMobile == "?1" ? "phone" : null, Cut(hintModel, 100));
        }

        var c = Rules.Parse(userAgent);
        var os = Known(c.OS.Family) ?? Cut(hintPlatform, 50);
        var ipad = c.Device.Family.Contains("iPad", StringComparison.OrdinalIgnoreCase) || (os == "Mac OS X" && touch == true);
        string type;
        if (ipad || c.Device.Family.Contains("Tablet", StringComparison.OrdinalIgnoreCase)
            || userAgent.Contains("Tablet", StringComparison.OrdinalIgnoreCase)
            || (os == "Android" && !userAgent.Contains("Mobile", StringComparison.Ordinal) && hintMobile != "?1"))
        {
            type = "tablet";
        }
        else if (hintMobile == "?1" || userAgent.Contains("Mobi", StringComparison.Ordinal) || os is "iOS" or "Android" || c.Device.Family == "iPhone")
        {
            type = "phone";
        }
        else
        {
            type = "desktop";
        }

        // Chrome on Android hides the model ("K"); a model hint, when the browser sent one, is the real one.
        var model = Cut(hintModel, 100) ?? (ipad ? "iPad" : Model(c.Device));
        return new AgentInfo(Cut(Known(c.UA.Family), 50), Cut(Version(c.UA.Major, c.UA.Minor), 30), Cut(ipad && os == "Mac OS X" ? "iPadOS" : os, 50),
            Cut(Version(c.OS.Major, c.OS.Minor), 30), type, model);
    }

    private static string? Model(Device d)
    {
        var family = Known(d.Family);
        if (family is null || family is "K" or "Generic Smartphone" or "Generic Tablet" or "Mac")
        {
            return null;
        }

        var brand = Known(d.Brand);
        var model = Known(d.Model) ?? family;
        if (model is "K")
        {
            return null;
        }

        return Cut(brand is null || model.StartsWith(brand, StringComparison.OrdinalIgnoreCase) || brand.StartsWith("Generic", StringComparison.Ordinal) ? model : brand + " " + model, 100);
    }

    private static string? Known(string? s) => string.IsNullOrWhiteSpace(s) || s == "Other" ? null : s.Trim();

    private static string? Version(string? major, string? minor)
        => string.IsNullOrWhiteSpace(major) ? null : string.IsNullOrWhiteSpace(minor) ? major : major + "." + minor;

    /// <summary>A client hint without its quotes: "\"Android\"" -> Android.</summary>
    private static string? Hint(IReadOnlyDictionary<string, string> hints, string name)
        => hints.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim().Trim('"') is { Length: > 0 } t ? t : null : null;

    private static string? Cut(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Length <= max ? s : s[..max];
}
