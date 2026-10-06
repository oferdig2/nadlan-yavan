using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Nadlan.Core.Assets;
using Nadlan.Core.Files;
using Nadlan.Core.Parcels;
using Nadlan.Core.Portfolios;
using Nadlan.Core.Security;

using Nadlan.Core.Validation;
using Nadlan.Host.Geo;

namespace Nadlan.Host.Presentation;

/// <summary>
/// The customer presentation (present.html): a Portfolio, or a list of Asset ids, as a buyer may see them - through
/// the same access rules as the map (hidden Assets are simply not there, prices only where allowed). Only what a
/// buyer should read goes out: no internal remarks, conditions, managing contact or provisional KAEKs.
/// </summary>
public static class PresentationEndpoints
{
    private const int MaxAssets = 100;
    private const int MaxMediaPerAsset = 12;

    // All rows of the Portfolio are read (one per Asset and Parcel), THEN put in the Portfolio's order and cut to
    // MaxAssets - cutting first (by id) would drop the wrong ones. Same bound as the Portfolio panel.
    private const int MaxRows = 10_000;

    // Only marketing media go in front of a customer, whoever presents: the presenter may see title deeds, ID scans
    // and engineering drawings (Legal, Engineering, Cadastral files), the customer must not.
    private const string CustomerCategory = "Marketing";

    // What a buyer's browser can show as a photo or play as a video, and nothing that could run as a page (SVG, XHTML,
    // XML...): an allow-list, so a new or odd type is left out rather than let in. HEIC is out too - most browsers
    // can't show it.
    private static readonly HashSet<string> CustomerMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/webp", "image/gif", "image/avif",
        "video/mp4", "video/webm", "video/quicktime",
    };

    public sealed record LinkDto(long? PortfolioId, long[]? AssetIds, string? Title);

    // The title the customer reads travels in the link SEALED by the server (data protection keys, as the sign-in
    // cookie) and bound to exactly these properties: a hand-made link can't put "pay the deposit to IBAN ..." under
    // the GreekPlot name. Links last half a year.
    private const string TitlePurpose = "Nadlan.Presentation.Title.v1";
    private static readonly TimeSpan LinkLifetime = TimeSpan.FromDays(180);
    private const int MaxTitleLength = 120;

    private sealed record SealedTitle(long? P, long[]? A, string T);

    private static string Target(long? portfolioId, IReadOnlyList<long>? assetIds)
        => portfolioId is long p ? "p" + p : "a" + string.Join(",", assetIds ?? Array.Empty<long>());

    public static void MapPresentationEndpoints(this IEndpointRouteBuilder app)
    {
        // A link to send: only someone who may edit what is presented (the Portfolio, or every one of the Assets) may
        // write the customer's title.
        app.MapPost("/api/presentation/link", async (LinkDto dto, UserAccess me, AccessPolicy policy, IDataProtectionProvider protection,
            CancellationToken ct) =>
        {
            var ids = (dto.AssetIds ?? Array.Empty<long>()).Distinct().ToArray();
            if (dto.PortfolioId is null && ids.Length is 0 or > MaxAssets)
            {
                throw new DomainValidationException("PRESENTATION_EMPTY", $"Choose 1 to {MaxAssets} Assets.");
            }

            // Without a title it is just the address: anyone may present what they can see (Sales, viewers, buyers);
            // the page itself shows each viewer only what they may see.
            var query = dto.PortfolioId is long p ? $"portfolio={p}" : "assets=" + string.Join(",", ids);
            var title = CleanTitle(dto.Title);
            if (title is null)
            {
                return Results.Ok(new { url = "/present.html?" + query });
            }

            // Writing text the customer reads under the GreekPlot name: only someone who may edit what is presented.
            if (dto.PortfolioId is long pid)
            {
                if (!(await policy.PortfolioAsync(me, pid, ct)).CanEdit)
                {
                    throw new ForbiddenException("PRESENTATION_TITLE_FORBIDDEN", "Only someone who may edit this Portfolio can give its presentation a title. Leave the title empty.");
                }
            }
            else
            {
                foreach (var id in ids)
                {
                    if (!(await policy.AssetAsync(me, id, ct)).CanEdit)
                    {
                        throw new ForbiddenException("PRESENTATION_TITLE_FORBIDDEN", $"A title needs edit rights on every Asset (not on #{id}). Leave the title empty.");
                    }
                }
            }
            var sealedTitle = protection.CreateProtector(TitlePurpose).ToTimeLimitedDataProtector()
                .Protect(JsonSerializer.Serialize(new SealedTitle(dto.PortfolioId, dto.PortfolioId is null ? ids : null, title)), LinkLifetime);
            return Results.Ok(new { url = "/present.html?" + query + "&t=" + Uri.EscapeDataString(sealedTitle) });
        });

        // ?portfolioId=12  or  ?assetIds=5&assetIds=9 (in that order); t = the sealed title from the link
        app.MapGet("/api/presentation", async (long? portfolioId, long[]? assetIds, string? t, UserAccess me, AccessPolicy policy,
            IPortfolioStore portfolios, IAssetStore assets, IParcelStore parcels, IFileAttachmentStore files, IFileUrlProvider urls,
            IDataProtectionProvider protection, CancellationToken ct) =>
        {
            List<long> order;
            AssetQuery query;
            if (portfolioId is long pid)
            {
                var portfolio = (await policy.PortfolioAsync(me, pid, ct)).CanView ? await portfolios.GetAsync(pid, ct) : null;
                if (portfolio is null)
                {
                    throw new EntityNotFoundException("Portfolio", pid);
                }

                order = (await portfolios.ListAssetIdsAsync(pid, ct)).ToList();
                query = new AssetQuery { PortfolioIds = new[] { pid }, Limit = MaxRows, Scope = me.Scope };
            }
            else
            {
                order = (assetIds ?? Array.Empty<long>()).Distinct().ToList();
                if (order.Count == 0 || order.Count > MaxAssets)
                {
                    throw new DomainValidationException("PRESENTATION_EMPTY", $"Give a portfolioId, or 1 to {MaxAssets} assetIds.");
                }

                query = new AssetQuery { AssetIds = order, Limit = MaxRows, Scope = me.Scope };
            }

            // One row per Asset and Parcel: an Asset on two Parcels is one stop with both polygons.
            var rank = order.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
            var all = (await assets.QueryAsync(query, ct)).GroupBy(a => a.AssetId)
                .OrderBy(g => rank.GetValueOrDefault(g.Key, int.MaxValue)).ThenBy(g => g.Key).ToList();
            var groups = all.Take(MaxAssets).ToList();

            var items = new List<object>();
            foreach (var g in groups)
            {
                var first = g.First();
                var asset = await assets.GetAsync(g.Key, ct);
                decimal? plotSqm = null; // official areas of its Parcels, those that have one
                foreach (var parcelId in g.Select(a => a.ParcelId).Distinct())
                {
                    if ((await parcels.GetAsync(parcelId, ct))?.OfficialAreaSqm is decimal sqm)
                    {
                        plotSqm = (plotSqm ?? 0) + sqm;
                    }
                }

                items.Add(new
                {
                    assetId = g.Key,
                    propertyType = first.PropertyTypeName,
                    area = first.GeographicArea,
                    kaek = g.Where(a => !a.RegistryIdIsProvisional).Select(a => a.RegistryId).FirstOrDefault(),
                    statusName = first.StatusName,
                    statusColor = first.StatusColor,
                    askPrice = first.PriceVisible ? first.AskPrice : null,
                    currencyCode = first.PriceVisible ? first.CurrencyCode : null,
                    houseSqm = asset?.HouseSqm,
                    plotSqm,
                    polygons = g.Select(a => GeoJson.Polygon(a.Geometry)),
                    media = await MediaAsync(me, files, urls, g.Key, g.Select(a => a.ParcelId).Distinct(), ct),
                });
            }

            // No Portfolio name or notes: they are internal ("Deal with X"). The title is the presenter's, from a sealed
            // link for exactly these properties - and only shown when there is something to show.
            var title = items.Count > 0 ? OpenTitle(protection, t, portfolioId, portfolioId is null ? order : null) : null;
            return Results.Ok(new { title, items, total = all.Count, truncated = all.Count > groups.Count });
        });
    }

    // Besides the 120 visible characters, a ceiling on what they are made of: one "character" can be a family emoji or a
    // letter under a stack of accents, and the sealed link must stay short enough to open.
    private const int MaxTitleChars = 240;

    // Letters that look like nothing (Hangul fillers, braille blank) or a mark that does nothing (combining grapheme
    // joiner, Mongolian vowel separator): invisible, so dropped like control characters.
    private static readonly HashSet<char> BlankLookingChars = new() { 'ᅟ', 'ᅠ', 'ㅤ', 'ﾠ', '⠀', '͏', '᠎' };

    // Accents per letter: real text has one or two; "Zalgo" text stacks dozens to smear over the page.
    private const int MaxMarksPerLetter = 2;

    /// <summary>
    /// The title as plain visible text: no control or invisible formatting characters (right-to-left overrides,
    /// zero-width joiners used to disguise text), at most 2 accents per letter, spaces collapsed, cut on whole
    /// characters (never inside an emoji) at 120 of them or 240 text units, whichever comes first.
    /// </summary>
    internal static string? CleanTitle(string? raw)
    {
        if (raw is null)
        {
            return null;
        }

        var sb = new System.Text.StringBuilder(raw.Length);
        var marks = 0;
        for (var i = 0; i < raw.Length; i++)
        {
            var ch = raw[i];
            // The zero-width joiner holds emoji together (family emoji): kept right after an emoji, dropped anywhere else.
            if (ch == '‍' && i > 0 && (char.IsLowSurrogate(raw[i - 1]) || raw[i - 1] == '️'))
            {
                sb.Append(ch);
                continue;
            }

            var category = char.GetUnicodeCategory(ch);
            if (category is System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format
                or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator
                || BlankLookingChars.Contains(ch))
            {
                if (char.IsWhiteSpace(ch))
                {
                    sb.Append(' '); // a tab or line break becomes a space; anything invisible is dropped
                }

                continue;
            }

            if (category is System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.EnclosingMark
                && ch != '️') // the emoji presentation selector is not an accent
            {
                if (++marks > MaxMarksPerLetter)
                {
                    continue;
                }
            }
            else
            {
                marks = 0;
            }

            sb.Append(char.IsWhiteSpace(ch) ? ' ' : ch);
        }

        var text = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (text.Length == 0)
        {
            return null;
        }

        // Whole characters only: drop the last ones until both limits hold.
        var info = new System.Globalization.StringInfo(text);
        var count = Math.Min(info.LengthInTextElements, MaxTitleLength);
        var cut = info.SubstringByTextElements(0, count);
        while (cut.Length > MaxTitleChars && count > 1)
        {
            cut = info.SubstringByTextElements(0, --count);
        }

        return cut.Length <= MaxTitleChars ? cut.TrimEnd() : null; // one endless emoji chain: no title rather than a broken link
    }

    // "photo.jpg" / "video.mp4": the kind plus the original's (plain) extension.
    private static string NeutralName(FileListItem f)
    {
        var ext = Path.GetExtension(f.OriginalFileName).ToLowerInvariant();
        ext = ext.Length is > 1 and <= 6 && ext.Skip(1).All(char.IsAsciiLetterOrDigit) ? ext : "";
        return (f.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ? "video" : "photo") + ext;
    }

    /// <summary>The sealed title if it is genuine, unexpired and for exactly these properties; else none (never an error).</summary>
    private static string? OpenTitle(IDataProtectionProvider protection, string? token, long? portfolioId, IReadOnlyList<long>? assetIds)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        try
        {
            var json = protection.CreateProtector(TitlePurpose).ToTimeLimitedDataProtector().Unprotect(token);
            var sealedTitle = JsonSerializer.Deserialize<SealedTitle>(json);
            return sealedTitle is not null && Target(sealedTitle.P, sealedTitle.A) == Target(portfolioId, assetIds) ? sealedTitle.T : null;
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException or FormatException)
        {
            return null; // tampered, expired, or from another server
        }
    }

    /// <summary>
    /// Marketing photos and videos of the Asset, then of its Parcels (and only if the viewer may see marketing files).
    /// Captions are what someone wrote, else the file type ("Drone photo") - never the original file name
    /// ("ID_scan_Papadopoulos.jpg").
    /// </summary>
    private static async Task<List<object>> MediaAsync(UserAccess me, IFileAttachmentStore files, IFileUrlProvider urls, long assetId,
        IEnumerable<long> parcelIds, CancellationToken ct)
    {
        var all = new List<FileListItem>(await files.ListReadyAsync(FileTargetTypes.Asset, assetId, ct));
        foreach (var parcelId in parcelIds)
        {
            all.AddRange(await files.ListReadyAsync(FileTargetTypes.Parcel, parcelId, ct));
        }

        return all.Where(f => string.Equals(f.Category, CustomerCategory, StringComparison.OrdinalIgnoreCase) && me.CanSeeFileCategory(f.Category)
                              && CustomerMediaTypes.Contains(f.MimeType.Split(';')[0].Trim()))
            .Take(MaxMediaPerAsset)
            .Select(f => (object)new
            {
                kind = f.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ? "video" : "image",
                // Neutral names: the uploader's file name must not reach the customer through the download name.
                url = urls.GetUrl(f.StorageKey, NeutralName(f)),
                thumbUrl = f.HasThumbnail ? urls.GetUrl(FileAttachment.ThumbnailKey(f.StorageKey), "photo.jpg") : null,
                caption = string.IsNullOrWhiteSpace(f.Caption) ? f.FileTypeName : f.Caption,
            })
            .ToList();
    }
}
