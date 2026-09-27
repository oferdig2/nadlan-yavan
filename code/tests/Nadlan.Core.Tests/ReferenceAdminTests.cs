using Nadlan.Core.Reference;
using Nadlan.Core.Validation;

namespace Nadlan.Core.Tests;

public class ReferenceAdminTests
{
    [Theory]
    [InlineData("under offer")]
    [InlineData("")]
    [InlineData("TOO_LONG_TOO_LONG_TOO_LONG_TOO_LONG_TOO_LONG")]
    public async Task Codes_must_be_simple_upper_case(string code)
    {
        var ex = await Assert.ThrowsAsync<DomainValidationException>(() =>
            new ReferenceAdminService(new Store()).CreateAsync(ReferenceTable.PropertyType, Row(code)));

        Assert.Equal("REFERENCE_CODE_INVALID", ex.Code);
    }

    [Fact]
    public async Task Codes_are_normalized_to_upper_case()
    {
        var store = new Store();
        await new ReferenceAdminService(store).CreateAsync(ReferenceTable.PropertyType, Row("villa"));

        Assert.Equal("VILLA", store.Rows.Single().Code);
    }

    [Fact]
    public async Task Code_cannot_change_on_edit()
    {
        var store = new Store();
        store.Rows.Add(new ReferenceRow(1, "FOR_SALE", "For sale", true, 10, "#16a34a", null));

        await new ReferenceAdminService(store).UpdateAsync(ReferenceTable.AssetStatus, new ReferenceRow(1, "RENAMED", "On the market", true, 10, "#16a34a", null));

        Assert.Equal("FOR_SALE", store.Rows.Single().Code);
        Assert.Equal("On the market", store.Rows.Single().Name);
    }

    [Fact]
    public async Task File_types_need_a_known_category_and_statuses_a_valid_colour()
    {
        var service = new ReferenceAdminService(new Store());

        Assert.Equal("REFERENCE_CATEGORY_INVALID", (await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.CreateAsync(ReferenceTable.FileType, Row("SURVEY") with { Category = "Misc" }))).Code);
        Assert.Equal("REFERENCE_COLOR_INVALID", (await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.CreateAsync(ReferenceTable.AssetStatus, Row("RESERVED") with { Color = "red" }))).Code);
    }

    private static ReferenceRow Row(string code) => new(0, code, "Name", true, 10, null, null);

    private sealed class Store : IReferenceAdminStore
    {
        public List<ReferenceRow> Rows { get; } = new();
        public Task<IReadOnlyList<ReferenceRow>> ListAsync(ReferenceTable t, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ReferenceRow>>(Rows);
        public Task<ReferenceRow?> GetAsync(ReferenceTable t, int id, CancellationToken ct = default) => Task.FromResult(Rows.FirstOrDefault(r => r.Id == id));
        public Task<int> InsertAsync(ReferenceTable t, ReferenceRow row, CancellationToken ct = default) { Rows.Add(row with { Id = Rows.Count + 1 }); return Task.FromResult(Rows.Count); }
        public Task UpdateAsync(ReferenceTable t, ReferenceRow row, CancellationToken ct = default) { Rows[Rows.FindIndex(r => r.Id == row.Id)] = row; return Task.CompletedTask; }
    }
}
