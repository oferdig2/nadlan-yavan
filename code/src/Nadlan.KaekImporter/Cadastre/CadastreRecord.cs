namespace Nadlan.KaekImporter.Cadastre;

// What the importer sends to GreekPlot (POST api/cadastre/records) for one KAEK: its units with their owners and acts.
// Built only from parsed data that has passed the ΑΦΜ rule (LegalEntityRule): a person's ΑΦΜ, ID card, birth date or
// parents' names are never in here. The same shape is read by Nadlan.Core.Cadastre.CadastreRecordInput on the server.

/// <summary>Found = units were read; NotOperating = the search found nothing (the Cadastre isn't operating there);
/// Error = the KAEK could not be read.</summary>
public static class CadastreStatus
{
    public const string Found = "Found";
    public const string NotOperating = "NotOperating";
    public const string NeedsOcr = "NeedsOcr";
    public const string Error = "Error";
}

/// <summary>Where an act was read: a summary's act line, a summary's previous-title list, or the record page's old titles (f items).</summary>
public static class ActSource
{
    public const string Summary = "Summary";
    public const string OldTitle = "OldTitle";
    public const string RecordTitle = "RecordTitle";
}

public static class ActType
{
    public const string Sale = "Sale";
    public const string Auction = "Auction";
    public const string Seizure = "Seizure";
    /// <summary>"Άρση κατάσχεσης": lifts the earlier seizures (they are no longer a lead).</summary>
    public const string SeizureLifted = "SeizureLifted";
    public const string ParentalGift = "ParentalGift";
    public const string Inheritance = "Inheritance";
    public const string Other = "Other";
}

public static class PartyRole
{
    public const string Acquirer = "Acquirer";
    public const string Transferor = "Transferor";
    public const string Other = "Other";
}

public sealed record CadastreRecordDto(string Kaek, string Status, string? Error, IReadOnlyList<CadastreUnitDto> Units);

/// <param name="Status">Found, or NeedsOcr when a summary of this unit is a scan without usable text.</param>
public sealed record CadastreUnitDto(
    int UnitV, int UnitH, string Status, string? Error,
    string? FloorText, decimal? TitleAreaSqm, string? TitleNumber, string? PlotShareText, string? Address,
    IReadOnlyList<CadastreOwnerDto> Owners, IReadOnlyList<CadastreActDto> Acts);

/// <param name="Kind">Person | LegalEntity | Unknown (see <see cref="PartyKind"/>).</param>
/// <param name="EntityTaxId">ΑΦΜ, only for a LegalEntity.</param>
/// <param name="Address">Only when the owner is also a party to a summary we read (§4).</param>
public sealed record CadastreOwnerDto(string Name, string? ShareText, string Kind, string? EntityTaxId, string? EntityTypeText, string? Address);

/// <param name="CoversUnits">The units the summary lists (e.g. "050097508005/0/1 050097508005/0/14"); a price per m² is only
/// meaningful when it lists exactly this one unit.</param>
public sealed record CadastreActDto(
    string Source, string? RegNo, int? RegYear, string? Office, string ActTypeCode, string? ActTypeText, DateOnly? ActDate,
    string? DeedNo, decimal? TransactionValue, decimal? ObjectiveValue, string? CoversUnits, IReadOnlyList<CadastrePartyDto> Parties);

public sealed record CadastrePartyDto(
    string Role, string? RoleText, string? CommentText, string Name, string? RightText, string? ShareText,
    string Kind, string? EntityTaxId, string? EntityTypeText, string? Address);
