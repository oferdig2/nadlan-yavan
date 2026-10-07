using Dapper;
using Nadlan.Core.Parcels;

namespace Nadlan.Persistence.MySql;

/// <summary>
/// Search by OT (building block) and plot number of a Parcel row aliased "p", shared by the Parcel and Asset lists.
/// Exact, not "contains" (OT 4 must not find 14, 40 and 47), on the search keys of migration 012: the number alone
/// finds it with any extension ("47" -> 47, 47A, 47B), with an extension only that one ("47A"). However either side was
/// typed - "047", "47 α", "47-A", Greek or Latin letters - it is the same key (<see cref="ParcelNumberKey"/>), and the
/// (base, key) indexes answer it without scanning the table.
/// </summary>
internal static class ParcelNumberSql
{
    public static void Add(List<string> where, DynamicParameters args, string? ot, string? plot)
    {
        AddOne(where, args, "ot", ParcelNumberKey.Key(ot, null));
        AddOne(where, args, "plot", ParcelNumberKey.Key(plot, null));
    }

    private static void AddOne(List<string> where, DynamicParameters args, string column, string? key)
    {
        if (key is null)
        {
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
