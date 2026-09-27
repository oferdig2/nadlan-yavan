using System.Globalization;
using System.Text;

namespace Nadlan.KaekImporter.Import;

/// <summary>NotSaved: found on the site during an offline run (no Nadlan server), so not stored anywhere but the report.</summary>
public enum ParcelResult { Created, AlreadyInNadlan, Rejected, NotSaved }

public sealed record ReportRow(string Kaek, ParcelResult Result, long? ParcelId, double? AreaSqm, IReadOnlyList<string> OverlapsWith, string? Message);

/// <summary>What happened to each KAEK in one run; written to a CSV the user can open in Excel.</summary>
public sealed class ImportReport
{
    private readonly List<ReportRow> _rows = new();
    private readonly HashSet<string> _roads = new(StringComparer.Ordinal);

    public int Requests { get; set; }
    public int AreasDone { get; set; }
    public int AreasTotal { get; set; }
    public IReadOnlyList<ReportRow> Rows => _rows;
    public int Count(ParcelResult result) => _rows.Count(r => r.Result == result);
    public int Roads => _roads.Count;

    public void Add(ReportRow row) => _rows.Add(row);
    public void AddRoad(string kaek) => _roads.Add(kaek);

    public object Counts => new
    {
        created = Count(ParcelResult.Created),
        exists = Count(ParcelResult.AlreadyInNadlan),
        rejected = Count(ParcelResult.Rejected),
        found = Count(ParcelResult.NotSaved),
        roads = Roads,
        requests = Requests,
        areasDone = AreasDone,
        areasTotal = AreasTotal,
    };

    public string Summary => Count(ParcelResult.NotSaved) > 0
        ? $"{Count(ParcelResult.NotSaved)} parcels found - NOT saved (offline), {Roads} roads skipped ({Requests} requests to the site)."
        : $"{Count(ParcelResult.Created)} created, {Count(ParcelResult.AlreadyInNadlan)} already in Nadlan, " +
        $"{Count(ParcelResult.Rejected)} rejected, {Roads} roads skipped ({Requests} requests to the site).";

    public async Task<string> WriteCsvAsync(DateTime startedLocal)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Nadlan", "KAEK imports");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"kaek-import-{startedLocal:yyyyMMdd-HHmmss}.csv");

        var csv = new StringBuilder("KAEK,Result,ParcelId,AreaSqm,OverlapsWith,Message\r\n");
        foreach (var r in _rows)
        {
            csv.Append(Cell(r.Kaek)).Append(',')
               .Append(r.Result switch
               {
                   ParcelResult.Created => "Created",
                   ParcelResult.AlreadyInNadlan => "Already in Nadlan",
                   ParcelResult.NotSaved => "Found (offline, not saved)",
                   _ => "Rejected",
               }).Append(',')
               .Append(r.ParcelId?.ToString(CultureInfo.InvariantCulture)).Append(',')
               .Append(r.AreaSqm?.ToString("0.00", CultureInfo.InvariantCulture)).Append(',')
               .Append(Cell(string.Join(" ", r.OverlapsWith))).Append(',')
               .Append(Cell(r.Message ?? "")).Append("\r\n");
        }

        foreach (var road in _roads.Order(StringComparer.Ordinal))
        {
            csv.Append(Cell(road)).Append(",Skipped (road / special property),,,,\r\n");
        }

        // BOM so Excel opens the Greek text correctly.
        await File.WriteAllTextAsync(path, csv.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    // ="..." stops Excel turning the 12-digit KAEK into 1.21E+11.
    private static string Cell(string value)
    {
        var v = value.Length > 11 && value.All(char.IsAsciiDigit) ? "=\"" + value + "\"" : value;
        return v.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }
}
