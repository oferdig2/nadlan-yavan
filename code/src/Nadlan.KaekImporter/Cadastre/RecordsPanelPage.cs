using Microsoft.Playwright;
using Nadlan.KaekImporter.Api;

namespace Nadlan.KaekImporter.Cadastre;

/// <summary>
/// The records panel in the lawyer-service tab (Browser/records-panel.js). Calls are best effort: the tab may be reloading.
/// The last status and counts are kept here too, so a panel rebuilt after a reload shows them again.
/// </summary>
public sealed class RecordsPanelPage(IPage page) : IRecordsPanel
{
    public string? LastStatus { get; private set; }
    public object? LastProgress { get; private set; }

    public Task StatusAsync(string text)
    {
        LastStatus = text;
        return Ui("t => window.NadlanRecords && NadlanRecords.status(t)", text);
    }

    public Task LogAsync(string text, string kind = "") => Ui("([t, k]) => window.NadlanRecords && NadlanRecords.log(t, k)", new[] { text, kind });

    public Task ProgressAsync(RecordsProgress p)
    {
        LastProgress = new
        {
            done = p.Done, total = p.Total, current = p.Current, owners = p.Owners, sales = p.Sales, lastPrice = p.LastPrice,
            notOperating = p.NotOperating, errors = p.Errors,
        };
        return Ui("c => window.NadlanRecords && NadlanRecords.progress(c)", LastProgress);
    }

    /// <summary>The run has started: Start off, Stop on (the panel doesn't rely on its own call's answer, see records-panel.js).</summary>
    public Task RunningAsync() => Ui("() => window.NadlanRecords && NadlanRecords.running(true)", null);

    public Task FinishedAsync(string text)
    {
        LastStatus = text;
        return Ui("t => window.NadlanRecords && NadlanRecords.finished(t)", text);
    }

    private async Task Ui(string script, object? arg)
    {
        try
        {
            await page.EvaluateAsync(script, arg);
        }
        catch (PlaywrightException)
        {
            // Tab reloading or closed.
        }
    }
}

/// <summary>GreekPlot as the records' destination.</summary>
public sealed class GreekPlotRecordsSink(NadlanApiClient client) : IRecordsSink
{
    public Task<string?> SaveAsync(CadastreRecordDto record, CancellationToken ct) => client.SaveCadastreRecordAsync(record, ct);
}
