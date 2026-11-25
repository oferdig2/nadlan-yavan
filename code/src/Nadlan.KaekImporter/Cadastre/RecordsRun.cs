using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Nadlan.KaekImporter.Cadastre;

/// <summary>"KAEKs I paste": separated by commas, semicolons, spaces or new lines; anything that isn't 12 digits is ignored.</summary>
public static partial class KaekList
{
    public sealed record Parsed(IReadOnlyList<string> Kaeks, int Ignored, int Duplicates);

    public static Parsed Parse(string? text)
    {
        var kaeks = new List<string>();
        int ignored = 0, duplicates = 0;
        foreach (var token in Separators().Split(text ?? "").Where(t => t.Length > 0))
        {
            if (!Kaek().IsMatch(token)) ignored++;
            else if (kaeks.Contains(token)) duplicates++;
            else kaeks.Add(token);
        }

        return new Parsed(kaeks, ignored, duplicates);
    }

    [GeneratedRegex(@"[,;\s]+")]
    private static partial Regex Separators();

    [GeneratedRegex(@"^\d{12}$")]
    private static partial Regex Kaek();
}

/// <summary>Where the run's results go: GreekPlot (POST api/cadastre/records).</summary>
public interface IRecordsSink
{
    /// <returns>null when saved, else why not.</returns>
    Task<string?> SaveAsync(CadastreRecordDto record, CancellationToken ct);
}

/// <summary>What the records panel shows during a run.</summary>
public interface IRecordsPanel
{
    Task StatusAsync(string text);
    Task LogAsync(string text, string kind = "");
    Task ProgressAsync(RecordsProgress progress);
    Task FinishedAsync(string text);
}

public sealed record RecordsProgress(int Done, int Total, string? Current, int Owners, int Sales, string? LastPrice, int NotOperating, int Errors);

/// <summary>
/// A records run (brief §0): one KAEK at a time, at most <see cref="MaxKaeks"/>, until done or Stop. Any sign of trouble at the
/// site stops it. Each KAEK is saved to GreekPlot as soon as it is read, and logged locally (KAEK, time, result - no names).
/// </summary>
public sealed class RecordsRun(ICadastreSite site, IRecordsSink sink, IRecordsPanel panel, TextWriter console, string? logFolder = null,
    Func<byte[], SummaryData>? readSummary = null)
{
    public const int MaxKaeks = 300;
    private const int MaxFailuresInARow = 3;

    private readonly StringBuilder _csv = new("Time (UTC),KAEK,Result,Units,Owners,Acts,Last sale,Message\r\n");

    public async Task<string> RunAsync(IReadOnlyList<string> kaeks, CancellationToken ct)
    {
        if (kaeks.Count > MaxKaeks) throw new ArgumentException($"At most {MaxKaeks} KAEKs per run.");
        var started = DateTime.Now;
        int owners = 0, sales = 0, notOperating = 0, errors = 0, done = 0, failuresInARow = 0;
        string? lastPrice = null;
        string summary;
        try
        {
            foreach (var kaek in kaeks)
            {
                ct.ThrowIfCancellationRequested();
                await panel.StatusAsync($"KAEK {done + 1} of {kaeks.Count}: {kaek} (one request every 5-15 s)");
                await panel.ProgressAsync(new RecordsProgress(done, kaeks.Count, kaek, owners, sales, lastPrice, notOperating, errors));

                CadastreRecordDto record;
                try
                {
                    record = await new CadastreFetcher(site, text => console.WriteLine("  " + text), readSummary).FetchAsync(kaek, ct);
                    failuresInARow = 0;
                }
                catch (CadastreTroubleException ex)
                {
                    await SaveErrorAsync(kaek, ex.Message, ct);
                    errors++;
                    Log(kaek, CadastreStatus.Error, null, ex.Message);
                    throw;
                }
                catch (CadastreBlockedException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Not the site's doing (e.g. a page laid out differently): this KAEK is an Error, the run goes on - unless
                    // it keeps happening, then the layout has probably changed and every request would be wasted.
                    console.WriteLine($"Reading KAEK {kaek} failed: {ex}");
                    await SaveErrorAsync(kaek, "Could not read the record: " + ex.Message, ct);
                    errors++;
                    done++;
                    Log(kaek, CadastreStatus.Error, null, ex.Message);
                    await panel.LogAsync($"{kaek}: could not be read ({ex.Message})", "err");
                    if (++failuresInARow >= MaxFailuresInARow)
                    {
                        throw new CadastreTroubleException($"{MaxFailuresInARow} KAEKs in a row could not be read; stopped. See the log.");
                    }

                    continue;
                }

                var problem = await sink.SaveAsync(record, ct);
                if (problem is not null)
                {
                    Log(kaek, record.Status, record, "NOT SAVED: " + problem);
                    throw new RecordsSinkException(problem);
                }

                done++;
                var unitOwners = record.Units.Sum(u => u.Owners.Count);
                owners += unitOwners;
                var last = LastSale(record);
                if (last is not null)
                {
                    sales++;
                    lastPrice = last.TransactionValue is decimal v ? "€" + v.ToString("#,0", CultureInfo.InvariantCulture) : lastPrice;
                }

                if (record.Status == CadastreStatus.NotOperating) notOperating++;
                Log(kaek, record.Status, record, null);
                await panel.LogAsync(record.Status == CadastreStatus.NotOperating
                    ? $"{kaek}: no records (the Cadastre isn't operating there)"
                    : $"{kaek}: {record.Units.Count} unit(s), {unitOwners} owner(s)" + (last is null ? "" : ", last sale " + Describe(last))
                      + (record.Units.Any(u => u.Status == CadastreStatus.NeedsOcr) ? " - a summary is a scan (not read)" : ""),
                    record.Status == CadastreStatus.NotOperating ? "warn" : "ok");
                await panel.ProgressAsync(new RecordsProgress(done, kaeks.Count, null, owners, sales, lastPrice, notOperating, errors));
            }

            summary = $"Done: {done} of {kaeks.Count} KAEK(s) saved to GreekPlot - {owners} owner(s), {sales} sale(s), {notOperating} not operating.";
        }
        catch (OperationCanceledException)
        {
            summary = $"Stopped: {done} of {kaeks.Count} KAEK(s) saved.";
        }
        catch (Exception ex) when (ex is CadastreTroubleException or CadastreBlockedException or RecordsSinkException)
        {
            console.WriteLine("Records run stopped: " + ex.Message);
            await panel.LogAsync(ex.Message, "err");
            summary = $"Stopped after {done} of {kaeks.Count} KAEK(s): {ex.Message}";
        }

        await panel.ProgressAsync(new RecordsProgress(done, kaeks.Count, null, owners, sales, lastPrice, notOperating, errors));
        var log = await WriteLogAsync(started);
        console.WriteLine(summary + (log is null ? "" : " Log: " + log));
        await panel.FinishedAsync(summary);
        return summary;
    }

    private async Task SaveErrorAsync(string kaek, string message, CancellationToken ct)
    {
        try
        {
            await sink.SaveAsync(new CadastreRecordDto(kaek, CadastreStatus.Error, CadastreText.Cap(message, 500), Array.Empty<CadastreUnitDto>()),
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            console.WriteLine($"Could not record the error for {kaek} in GreekPlot: {ex.Message}");
        }
    }

    /// <summary>The unit's newest sale or auction with a price (what the card shows as "Last sale").</summary>
    public static CadastreActDto? LastSale(CadastreRecordDto record) =>
        record.Units.SelectMany(u => u.Acts)
            .Where(a => a.Source == ActSource.Summary && a.ActTypeCode is ActType.Sale or ActType.Auction)
            .OrderByDescending(a => a.ActDate).FirstOrDefault();

    private static string Describe(CadastreActDto a) =>
        (a.TransactionValue is decimal v ? "€" + v.ToString("#,0", CultureInfo.InvariantCulture) : "no price") +
        (a.ActDate is DateOnly d ? " on " + d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) : "") +
        (a.ActTypeCode == ActType.Auction ? " (auction)" : "");

    private void Log(string kaek, string status, CadastreRecordDto? record, string? message)
    {
        var last = record is null ? null : LastSale(record);
        _csv.Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(',')
            .Append("=\"").Append(kaek).Append("\",").Append(status).Append(',')
            .Append(record?.Units.Count.ToString(CultureInfo.InvariantCulture)).Append(',')
            .Append(record?.Units.Sum(u => u.Owners.Count).ToString(CultureInfo.InvariantCulture)).Append(',')
            .Append(record?.Units.Sum(u => u.Acts.Count).ToString(CultureInfo.InvariantCulture)).Append(',')
            .Append(last is null ? "" : Csv(Describe(last))).Append(',')
            .Append(Csv(message ?? "")).Append("\r\n");
        console.WriteLine($"Records: {kaek} {status}" + (message is null ? "" : " - " + message));
    }

    private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    private async Task<string?> WriteLogAsync(DateTime startedLocal)
    {
        try
        {
            var folder = logFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Nadlan", "KAEK imports");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"kaek-records-{startedLocal:yyyyMMdd-HHmmss}.csv");
            await File.WriteAllTextAsync(path, _csv.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            return path;
        }
        catch (IOException ex)
        {
            console.WriteLine("Could not write the records log: " + ex.Message);
            return null;
        }
    }
}

/// <summary>GreekPlot did not save a KAEK's record: the run stops (reading on would only waste requests).</summary>
public sealed class RecordsSinkException(string message) : Exception("GreekPlot did not save the record: " + message);
