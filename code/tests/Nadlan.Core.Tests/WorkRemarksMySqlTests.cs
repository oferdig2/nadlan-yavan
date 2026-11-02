using Nadlan.Core.Validation;
using Nadlan.Core.Work;

namespace Nadlan.Core.Tests;

/// <summary>A remark on each work (migration 022, owner 2026-11-02): staff write it, the contractor sees it.</summary>
public partial class WorkMySqlTests
{
    [Fact]
    public async Task A_remark_on_a_work_is_kept_shown_to_the_contractor_and_cleared_when_empty()
    {
        if (ScratchMySql.ServerConnectionString is null) { return; }

        var w = await SeedAsync();
        var l = await LinkAsync(w);
        var clear = await WorkIdAsync(l, w.PA1, w.Clear);

        await l.Work.SetRemarksAsync(w.DataPerson, l.Id, clear, "  Leave the olive trees.\r\nGate code 1234  ");
        Assert.Equal("Leave the olive trees.\nGate code 1234", (await l.Store.GetItemAsync(l.Id, clear))!.Remarks);

        var json = System.Text.Json.JsonSerializer.Serialize(await Nadlan.Host.Work.WorkLinkEndpoints.ViewAsync((await l.Store.GetRequestAsync(l.Id))!,
            l.Store, new Nadlan.Host.Configuration.NadlanOptions(), new TestUrls(), true, CancellationToken.None));
        Assert.Contains("Leave the olive trees.", json);

        Assert.Equal("WORK_REMARKS_TOO_LONG", (await Assert.ThrowsAsync<DomainValidationException>(() =>
            l.Work.SetRemarksAsync(w.DataPerson, l.Id, clear, new string('x', 2001)))).Code);

        // Another request's work: not found; the remark stays.
        var other = await LinkAsync(w, "Other");
        await Assert.ThrowsAsync<EntityNotFoundException>(() => other.Work.SetRemarksAsync(w.DataPerson, other.Id, clear, "not mine"));

        await l.Work.SetRemarksAsync(w.DataPerson, l.Id, clear, "   ");
        Assert.Null((await l.Store.GetItemAsync(l.Id, clear))!.Remarks);
        var history = (await l.Store.ListEventsAsync(l.Id, 50)).Where(e => e.Action == WorkActions.Remarked).Select(e => e.Summary).ToList();
        Assert.Equal(2, history.Count);
        Assert.Contains(history, s => s.EndsWith("remark removed.", StringComparison.Ordinal));
    }
}
