using System.Globalization;
using Nadlan.KaekImporter.Geometry;

namespace Nadlan.KaekImporter.Ktimanet;

/// <summary>A shape returned by a Ktimatologio map click, in the Greek grid.</summary>
public sealed record KtimanetShape(string Kaek, IReadOnlyList<IReadOnlyList<EgsaPoint>> Rings)
{
    /// <summary>
    /// A real land parcel has an all-digit KAEK (e.g. 120981108035). Roads, streams, beaches etc. are "special
    /// properties" with letters in the KAEK (e.g. 12098ΕΚ00029); those are not imported.
    /// </summary>
    public bool IsLandParcel => Kaek.Length > 0 && Kaek.All(char.IsAsciiDigit);
}

/// <summary>
/// The request the site sends to <c>PostHandler</c> on a map click: <c>Message=&lt;request&gt;|x,y|</c>. Which request
/// depends on the map's "Θέλω να δω..." (I want to see) choice, and each one only answers for its own set of parcels -
/// asked with the other request, a point comes back empty (checked live 2026-10-25 in Theologos and Skroponeria).
/// </summary>
public static class KtimanetClick
{
    /// <summary>"Ανάρτηση Κτηματογράφησης" (Cadastral Survey Post): the parcels of an area still being surveyed (yellow lines).</summary>
    public const string Posting = "GETPSTKG";

    /// <summary>"Γεωτεμάχια Κτηματολογίου" (Land Parcels): the parcels where the cadastre is in operation (orange lines).</summary>
    public const string Operating = "GETPST";

    /// <summary>The request the panel says goes with the lines it read; only one of the known ones (else null).</summary>
    public static string? FromPanel(string? request) => request is Posting or Operating ? request : null;

    /// <summary>
    /// Whether a <c>PostHandler</c> request body is a map click: <c>GETPST</c> (Land Parcels), <c>GETPSTKG</c> (Cadastral
    /// Survey Post) or <c>GETPSTKGPRO</c> (Pre-Posting; was already let through before 2026-10-25). Their replies all
    /// have the layout <see cref="KtimanetReply"/> reads.
    /// </summary>
    public static bool IsMapClick(string? postData) =>
        postData is not null && postData.Split('&').Any(field => field.StartsWith("Message=GETPST", StringComparison.Ordinal));
}

/// <summary>
/// Parses the reply of a map click (<c>PostHandler</c> <c>Message=GETPSTKG|x,y|</c>, or <c>GETPST</c> in Land Parcels
/// areas - see <see cref="KtimanetClick"/>):
/// <c>GETPSTKG|x,y|&lt;type&gt;@[minX,minY,maxX,maxY]@@&lt;KAEK&gt;@X..~X..=X..=~~~~Y..~Y..=Y..=</c>
/// (the same after <c>GETPST|x,y|</c>).
/// X values of every ring come first (rings end with '='), then "~~~~", then the Y values in the same layout.
/// Nothing after the second '|' means there is no shape at that point (e.g. the sea).
/// </summary>
public static class KtimanetReply
{
    public static KtimanetShape? Parse(string reply)
    {
        var parts = reply.Split('|', 3);
        if (parts.Length < 3 || parts[2].Trim().Length == 0)
        {
            return null;
        }

        var body = parts[2];
        var afterBox = body.IndexOf("@@", StringComparison.Ordinal);
        if (afterBox < 0)
        {
            throw new FormatException($"Unexpected Ktimatologio reply (no '@@'): {Preview(reply)}");
        }

        var rest = body[(afterBox + 2)..];
        var at = rest.IndexOf('@');
        if (at < 0)
        {
            throw new FormatException($"Unexpected Ktimatologio reply (no geometry): {Preview(reply)}");
        }

        var kaek = rest[..at].Trim();
        var geometry = rest[(at + 1)..];
        var split = geometry.IndexOf("~~~~", StringComparison.Ordinal);
        if (split < 0)
        {
            throw new FormatException($"Unexpected Ktimatologio reply (no X/Y separator): {Preview(reply)}");
        }

        var xRings = Rings(geometry[..split]);
        var yRings = Rings(geometry[(split + 4)..]);
        if (xRings.Count == 0 || xRings.Count != yRings.Count)
        {
            throw new FormatException($"Unexpected Ktimatologio reply ({xRings.Count} X rings, {yRings.Count} Y rings) for {kaek}.");
        }

        var rings = new List<IReadOnlyList<EgsaPoint>>();
        for (var r = 0; r < xRings.Count; r++)
        {
            if (xRings[r].Length != yRings[r].Length)
            {
                throw new FormatException($"Unexpected Ktimatologio reply (ring {r} has {xRings[r].Length} X and {yRings[r].Length} Y values) for {kaek}.");
            }

            rings.Add(xRings[r].Select((x, i) => new EgsaPoint(x, yRings[r][i])).ToList());
        }

        return new KtimanetShape(kaek, rings);
    }

    private static List<double[]> Rings(string text) =>
        text.Split('=', StringSplitOptions.RemoveEmptyEntries)
            .Select(ring => ring.Split('~', StringSplitOptions.RemoveEmptyEntries)
                .Select(v => double.Parse(v, NumberStyles.Float, CultureInfo.InvariantCulture))
                .ToArray())
            .Where(ring => ring.Length > 0)
            .ToList();

    private static string Preview(string reply) => reply.Length <= 120 ? reply : reply[..120] + "...";
}
