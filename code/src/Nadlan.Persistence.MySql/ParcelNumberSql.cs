using Dapper;
using Nadlan.Core.Parcels;

namespace Nadlan.Persistence.MySql;

/// <summary>
/// Search by OT (building block) and plot number of a Parcel row aliased "p", shared by the Parcel and Asset lists.
/// Exact, not "contains" (OT 4 must not find 14, 40 and 47), on the search keys of migrations 012/013: the number alone
/// finds it with any extension ("47" -> 47, 47A, 47B, 47/3), with an extension only that one ("47A"). However either side
/// was typed - "047", "47 α", "47-A", Greek or Latin letters - it is the same key (<see cref="ParcelNumberKey"/>), while
/// "47/3" stays apart from "473"; the (base, key) indexes answer it without scanning the table.
/// </summary>
internal static class ParcelNumberSql
{
    public static void Add(List<string> where, DynamicParameters args, string? ot, string? plot)
    {
        AddOne(where, args, "ot", ot);
        AddOne(where, args, "plot", plot);
    }

    private static void AddOne(List<string> where, DynamicParameters args, string column, string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed))
        {
            return; // nothing typed: no filter
        }

        var key = ParcelNumberKey.Key(typed, null);
        if (key is null)
        {
            where.Add("FALSE"); // only separators ("-", "."): no number can match - never "no filter" (= everything)
            return;
        }

        var @base = ParcelNumberKey.Base(key)!;
        where.Add($"p.{column}_base = @{column}Base");
        args.Add($"{column}Base", @base);
        if (key != @base)
        {
            where.Add($"p.{column}_key = @{column}Key");
            args.Add($"{column}Key", key);
        }
    }
}
