using Dapper;

namespace Nadlan.Persistence.MySql;

/// <summary>
/// Search by OT (building block) and plot number of a Parcel row aliased "p", shared by the Parcel and Asset lists.
/// Exact, not "contains" (OT 4 must not find 14, 40 and 47): the number itself or with its extension - "47" and "47A"
/// both find OT 47 / ext A. Spaces and case are ignored (the columns' collation is case-insensitive).
/// </summary>
internal static class ParcelNumberSql
{
    public static void Add(List<string> where, DynamicParameters args, string? ot, string? plot)
    {
        if (Normalize(ot) is { } o)
        {
            where.Add("(p.ot = @otNumber OR CONCAT(p.ot, COALESCE(p.ot_ext, '')) = @otNumber)");
            args.Add("otNumber", o);
        }

        if (Normalize(plot) is { } pl)
        {
            where.Add("(p.plot_number = @plotNumber OR CONCAT(p.plot_number, COALESCE(p.plot_ext, '')) = @plotNumber)");
            args.Add("plotNumber", pl);
        }
    }

    private static string? Normalize(string? value)
    {
        var v = string.Concat((value ?? "").Where(c => !char.IsWhiteSpace(c)));
        return v.Length == 0 ? null : v;
    }
}
