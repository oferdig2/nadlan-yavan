namespace Nadlan.KaekImporter.Cadastre;

// What the importer sends to GreekPlot (POST api/cadastre/records) for one KAEK: its units with their owners and acts. The
// same shape is read by Nadlan.Core.Cadastre.CadastreRecordInput on the server. V2 (owner, 2026-11-26): every field about
// owners and parties is kept - a person's ΑΦΜ, parents, birth, ID card and address too; the fields are all optional.
// The PDFs the run keeps (the two extracts and the last sale's summary) go up separately (POST api/cadastre/documents).

/// <summary>Found = units were read; NotOperating = the search found nothing (the Cadastre isn't operating there);
/// Error = the KAEK could not be read.</summary>
public static class CadastreStatus
{
    public const string Found = "Found";
    public const string NotOperating = "NotOperating";
    public const string NeedsOcr = "NeedsOcr";

    /// <summary>A unit over the run's unit cap: only its row in the search is kept.</summary>
    public const string NotRead = "NotRead";
    public const string Error = "Error";
}

/// <summary>The unit's descriptive extract (KF).</summary>
public static class ExtractStatus
{
    public const string Read = "Read";
    public const string NeedsOcr = "NeedsOcr";
    public const string NotFetched = "NotFetched";
    public const string Error = "Error";
}

/// <summary>Where an act was read: a summary's act line, a summary's previous-title list, the record page's old titles (f
/// items), or (V2) a registration in the descriptive extract.</summary>
public static class ActSource
{
    public const string Summary = "Summary";
    public const string OldTitle = "OldTitle";
    public const string RecordTitle = "RecordTitle";
    public const string Extract = "Extract";
}

/// <summary>The descriptive extract's sections.</summary>
public static class ExtractSection
{
    public const string Initial = "Initial";     // ΑΡΧΙΚΕΣ ΕΓΓΡΑΦΕΣ
    public const string Later = "Later";         // ΜΕΤΑΓΕΝΕΣΤΕΡΕΣ ΕΓΓΡΑΦΕΣ
    public const string Servitude = "Servitude"; // ΔΟΥΛΕΙΕΣ
    public const string Other = "Other";         // any other section of registrations (encumbrances ...)
}

/// <summary>Where an owner's or a party's row came from.</summary>
public static class PersonSource
{
    public const string RecordPage = "RecordPage";
    public const string DescriptiveExtract = "DescriptiveExtract";
    public const string Summary = "Summary";
}

/// <summary>The PDFs kept (cadastre_document.kind).</summary>
public static class DocumentKind
{
    public const string DescriptiveExtract = "DescriptiveExtract";
    public const string SpatialExtract = "SpatialExtract";
    public const string Summary = "Summary";
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

/// <param name="Status">Found; NeedsOcr when a summary of this unit is a scan without usable text; NotRead over the unit cap.</param>
public sealed record CadastreUnitDto(
    int UnitV, int UnitH, string Status, string? Error,
    string? FloorText, decimal? TitleAreaSqm, string? TitleNumber, string? PlotShareText, string? Address,
    IReadOnlyList<CadastreOwnerDto> Owners, IReadOnlyList<CadastreActDto> Acts)
{
    /// <summary>ΕΜΒΑΔΟΝ (measured), from the descriptive extract.</summary>
    public decimal? MeasuredAreaSqm { get; init; }
    public string? RemarksText { get; init; }

    /// <summary>The OT number the extract's remarks give ("Ο.Τ. 18" = 18): a suggestion on the card, never applied.</summary>
    public string? OtText { get; init; }
    public string? ExtractStatus { get; init; }
    public string? PropertyKind { get; init; }
    public string? OfficeText { get; init; }
    public string? MapSheet { get; init; }

    /// <summary>The neighbours' KAEKs written on the diagram, space separated.</summary>
    public string? NeighbourKaeks { get; init; }
}

/// <summary>Everything else about a person or a body (owner, 2026-11-26: keep it all). Every field optional.</summary>
public sealed record PersonDetailsDto(
    string? FatherName = null, string? MotherName = null, DateOnly? BirthDate = null, string? BirthPlace = null,
    string? IdCardNumber = null, DateOnly? IdCardDate = null, string? IdCardAuthority = null, string? RightType = null, string? Source = null)
{
    public static PersonDetailsDto None { get; } = new();

    /// <summary>This one's fields, the gaps filled from <paramref name="other"/> (Source stays this one's).</summary>
    public PersonDetailsDto Fill(PersonDetailsDto? other) => other is null ? this : new(
        FatherName ?? other.FatherName, MotherName ?? other.MotherName, BirthDate ?? other.BirthDate, BirthPlace ?? other.BirthPlace,
        IdCardNumber ?? other.IdCardNumber, IdCardDate ?? other.IdCardDate, IdCardAuthority ?? other.IdCardAuthority, RightType ?? other.RightType,
        Source);
}

/// <param name="Kind">Person | LegalEntity | Unknown (see <see cref="PartyKind"/>): a classification, never a reason to drop data.</param>
/// <param name="TaxId">ΑΦΜ of a person or an entity.</param>
/// <param name="Address">From the descriptive extract or a summary that names this owner.</param>
public sealed record CadastreOwnerDto(string Name, string? ShareText, string Kind, string? TaxId, string? EntityTypeText, string? Address,
    PersonDetailsDto Details);

/// <param name="CoversUnits">The units the summary lists (e.g. "050097508005/0/1 050097508005/0/14"); a price per m² is only
/// meaningful when it lists exactly this one unit.</param>
public sealed record CadastreActDto(
    string Source, string? RegNo, int? RegYear, string? Office, string ActTypeCode, string? ActTypeText, DateOnly? ActDate,
    string? DeedNo, decimal? TransactionValue, decimal? ObjectiveValue, string? CoversUnits, IReadOnlyList<CadastrePartyDto> Parties)
{
    public DateOnly? RegDate { get; init; }
    public string? Section { get; init; }
    public string? RightText { get; init; }
    public string? DocumentText { get; init; }
    public string? IssuerText { get; init; }
    public string? RegStatusText { get; init; }
}

public sealed record CadastrePartyDto(
    string Role, string? RoleText, string? CommentText, string Name, string? RightText, string? ShareText,
    string Kind, string? TaxId, string? EntityTypeText, string? Address, PersonDetailsDto Details);

/// <summary>A PDF the run keeps, in memory until it is uploaded to GreekPlot (never written to disk).</summary>
public sealed record FetchedDocument(int UnitV, int UnitH, string Kind, string? RegNo, int? RegYear, bool TextReadable, byte[] Bytes)
{
    public string Label => Kind switch
    {
        DocumentKind.DescriptiveExtract => "descriptive extract",
        DocumentKind.SpatialExtract => "diagram",
        _ => $"summary {RegNo}/{RegYear}",
    };
}

/// <summary>One KAEK as read: the record GreekPlot keeps and the PDFs to store with it.</summary>
public sealed record KaekFetch(CadastreRecordDto Record, IReadOnlyList<FetchedDocument> Documents);
