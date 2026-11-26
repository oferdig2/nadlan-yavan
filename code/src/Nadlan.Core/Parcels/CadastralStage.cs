using Nadlan.Core.Validation;

namespace Nadlan.Core.Parcels;

/// <summary>
/// Where a Parcel stands in the Cadastre (brief AI/Remarks/CodingAgent-PolygonStageAndUpdate-2026-11-26.md §2, migration 034):
/// seen on the Cadastre map (the click request that answered: <see cref="Polygon"/>) or by the lawyer service (a KAEK found
/// there = in operation, "Δεν βρέθηκαν εγγραφές" = not operating: <see cref="Lawyer"/>). NULL = unknown (entered by hand,
/// or imported before this was kept). Staff only: never on presentations, public links or the deals portal.
/// </summary>
public static class CadastralStages
{
    /// <summary>A GETPST click ("Γεωτεμάχια Κτηματολογίου", orange lines), or the lawyer service found the KAEK.</summary>
    public const string InOperation = "InOperation";

    /// <summary>A GETPSTKG click ("Ανάρτηση Κτηματογράφησης", yellow lines): still being surveyed / posted.</summary>
    public const string SurveyPost = "SurveyPost";

    /// <summary>A GETPSTKGPRO click ("Προανάρτηση Κτηματογράφησης").</summary>
    public const string PrePosting = "PrePosting";

    /// <summary>The lawyer service answered "Δεν βρέθηκαν εγγραφές" (it doesn't say which posting stage).</summary>
    public const string NotOperating = "NotOperating";

    /// <summary>The map filter's value for "no stage known" (stored as NULL, never as this text).</summary>
    public const string Unknown = "Unknown";

    /// <summary>What can be stored (parcel.cadastral_stage CHECK).</summary>
    public static readonly IReadOnlyList<string> Stored = new[] { InOperation, SurveyPost, PrePosting, NotOperating };

    /// <summary>The map filter's choices, in the order shown.</summary>
    public static readonly IReadOnlyList<string> FilterValues = new[] { InOperation, SurveyPost, PrePosting, NotOperating, Unknown };

    /// <summary>What a map click can tell (the lawyer's NotOperating never comes from the map).</summary>
    public static readonly IReadOnlyList<string> FromMap = new[] { InOperation, SurveyPost, PrePosting };

    public const string Polygon = "Polygon";
    public const string Lawyer = "Lawyer";

    /// <summary>The stage the site's map-click request answers for (null for anything else).</summary>
    public static string? FromClickRequest(string? request) => request switch
    {
        "GETPST" => InOperation,
        "GETPSTKG" => SurveyPost,
        "GETPSTKGPRO" => PrePosting,
        _ => null,
    };

    /// <summary>The click request that answers for a map stage (the reverse of <see cref="FromClickRequest"/>).</summary>
    public static string? RequestOf(string? stage) => stage switch
    {
        InOperation => "GETPST",
        SurveyPost => "GETPSTKG",
        PrePosting => "GETPSTKGPRO",
        _ => null,
    };

    /// <summary>
    /// The request seen with a map stage: as sent, which must be the one that answers for that stage, or derived from it.
    /// </summary>
    public static string RequestFor(string stage, string? request)
    {
        var expected = RequestOf(stage) ?? throw new DomainValidationException("PARCEL_STAGE_INVALID", $"{stage} is not seen on the map.");
        var r = string.IsNullOrWhiteSpace(request) ? null : request.Trim().ToUpperInvariant();
        return r is null || r == expected ? expected
            : throw new DomainValidationException("PARCEL_STAGE_REQUEST", $"The click request {r} does not answer for {stage} (that is {expected}).");
    }

    /// <summary>"In operation", "Survey post", ... for history lines; "Unknown" for null.</summary>
    public static string Label(string? stage) => stage switch
    {
        InOperation => "In operation",
        SurveyPost => "Survey post",
        PrePosting => "Pre-posting",
        NotOperating => "Not operating",
        _ => "Unknown",
    };

    /// <summary>A stage a map click may report, or 400. Null / blank = none.</summary>
    public static string? ParseFromMap(string? value)
    {
        var v = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (v is null) return null;
        return FromMap.FirstOrDefault(s => string.Equals(s, v, StringComparison.OrdinalIgnoreCase))
               ?? throw new DomainValidationException("PARCEL_STAGE_INVALID", $"cadastralStage must be {string.Join(", ", FromMap)}.");
    }

    /// <summary>The map filter's values, or 400 on an unknown one.</summary>
    public static IReadOnlyList<string> ParseFilter(IEnumerable<string>? values)
    {
        var list = new List<string>();
        foreach (var raw in values ?? Array.Empty<string>())
        {
            var v = raw?.Trim();
            if (string.IsNullOrEmpty(v)) continue;
            var hit = FilterValues.FirstOrDefault(s => string.Equals(s, v, StringComparison.OrdinalIgnoreCase))
                      ?? throw new DomainValidationException("PARCEL_STAGE_INVALID", $"Unknown Cadastre stage '{v}'. Use {string.Join(", ", FilterValues)}.");
            if (!list.Contains(hit)) list.Add(hit);
        }

        return list.Count == FilterValues.Count ? Array.Empty<string>() : list; // all ticked = no filter
    }

    /// <summary>
    /// When it was seen, as stored: now when not given, and never later than now (a PC with a wrong clock must not stop every
    /// later sighting from winning, §2.2 "the newest sighting wins").
    /// </summary>
    public static DateTime SeenUtc(DateTime? seen, DateTime nowUtc)
    {
        if (seen is not DateTime s) return nowUtc;
        var utc = s.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(s, DateTimeKind.Utc) : s.ToUniversalTime();
        return utc > nowUtc ? nowUtc : utc;
    }
}
