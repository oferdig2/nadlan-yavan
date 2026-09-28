using System.Text.Json;
using System.Threading.Channels;
using Nadlan.KaekImporter.Api;
using Nadlan.KaekImporter.Geometry;
using Nadlan.KaekImporter.Ktimanet;

namespace Nadlan.KaekImporter.Import;

/// <summary>
/// "Import each parcel I click": the user clicks parcels on the Ktimatologio map as usual, and the site's own reply
/// to that click (which holds the KAEK and polygon) is imported. No extra requests to the site; the user sets the pace.
/// </summary>
public sealed class ClickImport
{
    private readonly ImportSession _session;
    private readonly KtimanetPage _site;
    private readonly Channel<string> _replies = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _worker;

    public ClickImport(KtimanetPage site, NadlanApiClient? nadlan, int? geographicAreaId, TextWriter console)
    {
        _site = site;
        _session = new ImportSession(site, nadlan, geographicAreaId, console);
        _worker = Task.Run(WorkAsync);
    }

    /// <summary>Called with the body of every map-click reply (PostHandler GETPSTKG) while this mode is on.</summary>
    public void OnClickReply(string body) => _replies.Writer.TryWrite(body);

    /// <summary>Imports what is still queued, then writes the report.</summary>
    public async Task StopAsync()
    {
        _replies.Writer.TryComplete();
        await _worker;
        await _session.FinishAsync("Click import finished: " + _session.Report.Summary);
    }

    private async Task WorkAsync()
    {
        await foreach (var body in _replies.Reader.ReadAllAsync())
        {
            try
            {
                var reply = body.StartsWith('"') ? JsonSerializer.Deserialize<string>(body) ?? "" : body;
                var shape = KtimanetReply.Parse(reply);
                if (shape is null)
                {
                    await _site.LogAsync("Nothing registered where you clicked.");
                    continue;
                }

                var rings = Egsa87.ToWgs84(shape.Rings);
                if (await _session.HandleAsync(shape, rings, CancellationToken.None) == ShapeOutcome.AlreadyHandled)
                {
                    await _site.LogAsync($"{shape.Kaek}: already handled in this session");
                }
            }
            catch (Exception ex) when (ex is FormatException or JsonException or HttpRequestException or TaskCanceledException)
            {
                await _site.LogAsync("Could not import that click: " + ex.Message, "err");
            }
            catch (StopImportException ex)
            {
                await _site.LogAsync(ex.Message, "err");
                await _site.RefreshStateAsync(); // e.g. token refused: the panel offers "Connect to Nadlan"
            }
        }
    }
}
