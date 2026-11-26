using Nadlan.Core.Cadastre;
using Nadlan.Core.Validation;

namespace Nadlan.Core.Tests;

/// <summary>
/// The server's side of the Cadastre records (migration 031): the checks on what the importer sends - above all the 400 on a
/// person's ΑΦΜ, the second guard behind the importer's - and what the parcel card shows. Made-up names and numbers only.
/// </summary>
public class CadastreRecordsServerTests
{
    internal const string Kaek = "990000000001";

    internal static CadastreOwnerInput Person(string name = "ΔΟΚΙΜΟΠΟΥΛΟΣ ΝΙΚΟΣ", string? tax = null) => new(name, "1 / 2", "Person", tax, null, null);
    internal static CadastreOwnerInput Company(string tax = "090000009") => new("ΔΟΚΙΜΑΣΤΙΚΗ Α.Ε.", "1 / 2", "LegalEntity", tax, "ΝΟΜΙΚΟ ΠΡΟΣΩΠΟ ΙΔΙΩΤΙΚΟΥ ΔΙΚΑΙΟΥ", "ΟΔΟΣ ΕΤΑΙΡΕΙΑΣ 1, ΑΘΗΝΑ");

    internal static CadastreActInput Sale(DateOnly date, decimal price, string covers, params CadastrePartyInput[] parties) =>
        new("Summary", "31", date.Year, "ΔΟΚΙΜΩΝ", "Sale", "Αγοραπωλησία", date, "4321", price, price * 3, covers, parties);

    internal static CadastrePartyInput Buyer(string? tax = null, string kind = "Person") =>
        new("Acquirer", "ΑΠΟΚΤΩΝ", "ΑΓΟΡΑΣΤΡΙΑ", "ΑΓΟΡΑΣΤΟΠΟΥΛΟΥ ΕΛΕΝΗ", "ΚΥΡΙΟΤΗΤΑ-ΠΛΗΡΗΣ", "100 / 100", kind, tax, null, "ΟΔΟΣ ΔΟΚΙΜΗΣ 5, ΑΘΗΝΑ");

    internal static CadastreRecordInput Record(params CadastreUnitInput[] units) => new(Kaek, "Found", null, units);

    internal static CadastreUnitInput Unit(int h, decimal? area, CadastreOwnerInput[] owners, params CadastreActInput[] acts) =>
        new(0, h, "Found", null, h == 0 ? null : "1", area, "Α" + h, h == 0 ? null : "9/1000", "ΟΔΟΣ ΔΟΚΙΜΗΣ 12, 10000 ΑΘΗΝΑ", owners, acts);

    [Fact]
    public void A_persons_tax_number_is_kept_now()
    {
        // V2 (owner, 2026-11-26): the 400 of V1 is gone; a person's, an unknown owner's and a party's tax number are kept.
        var units = CadastreRules.Validate(Record(Unit(1, 85, new[] { Person(tax: "100000001"), new CadastreOwnerInput("ΑΓΝΩΣΤΟΣ", "1 / 1", "Unknown", "100000002", null, null) },
            Sale(new DateOnly(2024, 3, 28), 21000, "990000000001/0/1", Buyer(tax: "100000003")))));

        Assert.Equal(new[] { "100000001", "100000002" }, units[0].Owners.Select(o => o.TaxId));
        Assert.Equal("100000003", units[0].Acts[0].Parties[0].TaxId);
        Assert.Equal("CADASTRE_BAD_TAX_ID", Assert.Throws<DomainValidationException>(() =>
            CadastreRules.Validate(Record(Unit(1, 85, new[] { Person(tax: "ΑΦΜ 1000") })))).Code);
    }

    [Fact]
    public void A_companys_tax_number_is_kept()
    {
        var units = CadastreRules.Validate(Record(Unit(1, 85, new[] { Person(), Company() },
            Sale(new DateOnly(2024, 3, 28), 21000, "990000000001/0/1", Buyer(tax: "090000009", kind: "LegalEntity")))));

        Assert.Equal("090000009", units[0].Owners[1].TaxId);
        Assert.Null(units[0].Owners[0].TaxId);
        Assert.Equal(0.5m, units[0].Owners[0].ShareDecimal);
        Assert.Equal("090000009", units[0].Acts[0].Parties[0].TaxId);
        Assert.Equal("CADASTRE_BAD_TAX_ID", Assert.Throws<DomainValidationException>(() =>
            CadastreRules.Validate(Record(Unit(1, 85, new[] { Company(tax: "ΑΦΜ 0900") })))).Code);
    }

    [Fact]
    public void Records_are_checked_for_shape()
    {
        Assert.Equal("CADASTRE_BAD_KAEK", Assert.Throws<DomainValidationException>(() => CadastreRules.Validate(new CadastreRecordInput("12345", "Found", null, null))).Code);
        Assert.Throws<DomainValidationException>(() => CadastreRules.Validate(new CadastreRecordInput(Kaek, "Found", null, Array.Empty<CadastreUnitInput>())));
        Assert.Throws<DomainValidationException>(() => CadastreRules.Validate(new CadastreRecordInput(Kaek, "NotOperating", null, new[] { Unit(0, null, Array.Empty<CadastreOwnerInput>()) })));
        Assert.Throws<DomainValidationException>(() => CadastreRules.Validate(new CadastreRecordInput(Kaek, "Maybe", null, null)));
        Assert.Throws<DomainValidationException>(() => CadastreRules.Validate(Record(Unit(1, 85, Array.Empty<CadastreOwnerInput>()), Unit(1, 85, Array.Empty<CadastreOwnerInput>()))));
        Assert.Throws<DomainValidationException>(() => CadastreRules.Validate(Record(Unit(1, 85, new[] { Person() with { Kind = "Robot" } }))));
        Assert.Equal("CADASTRE_TOO_LONG", Assert.Throws<DomainValidationException>(() =>
            CadastreRules.Validate(Record(Unit(1, 85, new[] { Person(new string('Α', 301)) })))).Code);

        var notOperating = CadastreRules.Validate(new CadastreRecordInput(Kaek, "NotOperating", null, null));
        var row = Assert.Single(notOperating);
        Assert.Equal(("NotOperating", (int?)null), (row.Status, row.UnitV));
    }

    [Theory]
    [InlineData("3 / 10", 0.3)]
    [InlineData("1 / 1", 1.0)]
    [InlineData("1/3", 0.333333)]
    [InlineData("100%", 1.0)]
    [InlineData("50 %", 0.5)]
    [InlineData("ΠΛΗΡΗΣ", null)]
    [InlineData("3 / 0", null)]
    public void Shares_as_numbers(string text, double? expected)
    {
        Assert.Equal(expected is null ? null : (decimal)expected, CadastreRules.ShareDecimal(text));
    }

    private static CadastreSnapshot Snapshot(params CadastreUnitInput[] units) =>
        new(Kaek, DateTime.UtcNow, "Admin", CadastreRules.Validate(Record(units)));

    [Fact]
    public void The_last_sale_with_a_price_per_square_metre_when_it_covers_this_one_unit()
    {
        var snap = Snapshot(
            Unit(0, 2052.50m, new[] { Company() }),
            Unit(1, 30m, new[] { Person() },
                Sale(new DateOnly(2019, 1, 10), 15000, "990000000001/0/1"),
                Sale(new DateOnly(2024, 3, 28), 21000, "990000000001/0/1")));

        var sale = CadastreReading.LastSale(snap)!;
        Assert.Equal((new DateOnly(2024, 3, 28), 21000m, 700m, 1), (sale.Date!.Value, sale.Price!.Value, sale.PricePerSqm!.Value, sale.UnitH!.Value));

        // A sale of a flat with its storage room: no price per m² (the price is for both).
        var both = Snapshot(Unit(1, 30m, new[] { Person() }, Sale(new DateOnly(2024, 3, 28), 21000, "990000000001/0/1 990000000001/0/14")));
        Assert.Null(CadastreReading.LastSale(both)!.PricePerSqm);

        // Old titles have no price and don't count as the last sale.
        var old = Snapshot(Unit(1, 30m, new[] { Person() }, Sale(new DateOnly(1984, 10, 26), 0, "") with { Source = "OldTitle", TransactionValue = null }));
        Assert.Null(CadastreReading.LastSale(old));
    }

    [Fact]
    public void A_seizure_after_the_last_sale_is_shown_one_before_it_is_not()
    {
        CadastreActInput Seizure(DateOnly d) => new("Summary", "777", d.Year, "ΔΟΚΙΜΩΝ", "Seizure", "Αναγκαστική κατάσχεση", d, "777", 20000, null, Kaek + "/0/4",
            Array.Empty<CadastrePartyInput>());
        var auction = new CadastreActInput("Summary", "5413", 2024, "ΔΟΚΙΜΩΝ", "Auction", "Περίληψη κατακυρωτικής έκθεσης", new DateOnly(2024, 7, 10), "4322",
            318000, 300000m, Kaek + "/0/4", Array.Empty<CadastrePartyInput>());

        var snap = Snapshot(Unit(4, 60, new[] { Person() }, Seizure(new DateOnly(2023, 5, 1)), auction, Seizure(new DateOnly(2026, 11, 5))));

        var open = Assert.Single(CadastreReading.OpenSeizures(snap));
        Assert.Equal((new DateOnly(2026, 11, 5), 20000m), (open.Date!.Value, open.Claim!.Value));
        Assert.Equal("Auction", CadastreReading.LastSale(snap)!.ActTypeCode);
    }

    [Fact]
    public void A_lifted_seizure_is_no_longer_shown()
    {
        CadastreActInput Act(string code, string text, DateOnly d) => new("Summary", "778", d.Year, "ΔΟΚΙΜΩΝ", code, text, d, "778", code == "Seizure" ? 20000 : null,
            null, Kaek + "/0/4", Array.Empty<CadastrePartyInput>());

        // Seized, then lifted (e.g. the debt was paid): no warning.
        var lifted = Snapshot(Unit(4, 60, new[] { Person() }, Act("Seizure", "Αναγκαστική κατάσχεση", new DateOnly(2025, 2, 1)),
            Act("SeizureLifted", "Άρση κατάσχεσης", new DateOnly(2025, 9, 1))));
        Assert.Empty(CadastreReading.OpenSeizures(lifted));

        // A lifting before a newer seizure doesn't close the newer one.
        var again = Snapshot(Unit(4, 60, new[] { Person() }, Act("SeizureLifted", "Άρση κατάσχεσης", new DateOnly(2025, 9, 1)),
            Act("Seizure", "Αναγκαστική κατάσχεση", new DateOnly(2026, 11, 5))));
        Assert.Equal(new DateOnly(2026, 11, 5), Assert.Single(CadastreReading.OpenSeizures(again)).Date);
    }
}
