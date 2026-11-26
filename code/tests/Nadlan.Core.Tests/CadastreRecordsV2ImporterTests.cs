using System.Text;
using Nadlan.KaekImporter.Cadastre;

namespace Nadlan.Core.Tests;

/// <summary>
/// V2 of the importer's owners &amp; sales module (brief AI/Remarks/CodingAgent-KtimaRecordsV2-2026-11-26.md): the two extracts,
/// every owner field, the last sale's summary, the unit cap. Every fixture is written by hand from the labels the brief lists
/// (§1.2, §1.3): names, KAEKs, tax numbers, dates and places are all made up. Nothing here talks to the real site.
/// </summary>
public class CadastreRecordsV2ImporterTests
{
    private const string Kaek = "990000000001";
    private const string PersonTax = "100000001";   // made up
    private const string CompanyTax = "090000009";  // made up
    private const string ShowDocs = "/Professionals/Inquiry/Main/ShowDocs";

    // ---- made-up fixtures ----------------------------------------------------------------------------------------------

    private static string Estates(params int[] units) =>
        "<table class=\"Grid\"><tr class=\"HeaderRow\"><th>ΚΑΕΚ</th></tr>" +
        string.Concat(units.Select(h =>
            $"<tr class=\"NormalRow\"><td>{Kaek}/0/ {h}</td><td>{(h == 0 ? "" : h.ToString())}</td><td>{(h == 0 ? "1.200,50" : "85,20")}</td><td>Α{h}</td>" +
            $"<td>{(h == 0 ? "" : "9/1000")}</td><td>ΟΔΟΣ ΔΟΚΙΜΗΣ</td><td>12</td><td>10000</td><td>ΔΟΚΙΜΑΣΤΙΚΗ ΘΕΣΗ</td><td>ΔΗΜΟΣ ΔΟΚΙΜΩΝ</td>" +
            $"<td class=\"RowCommands\"><a class=\"Icon IconMore\" href=\"/Professionals/Inquiry/Main/viewResults/{Kaek}-0-{h}?p=1\"></a></td></tr>")) +
        "</table>";

    private static string Owner(string afm, string surname, string name, string father, string share) =>
        $"<tr class=\"NormalRow\"><td>{afm}</td><td>{surname}</td><td>{name}</td><td>{father}</td><td>{share}</td></tr>";

    private static string Doc(string label, string item, int h) =>
        $"<tr class=\"NormalRow\"><td>{label}</td><td><a class=\"Icon IconMore\" title=\"Επιλέξτε\" " +
        $"href=\"/Professionals/Inquiry/Main/showScans?KAEK={Kaek}-0-{h}&amp;item={item}&amp;pid=77001\"></a></td></tr>";

    private static string Scans(string item, int h) => $"/Professionals/Inquiry/Main/showScans?KAEK={Kaek}-0-{h}&item={item}&pid=77001";

    /// <summary>The extracts' form as brief V1 §2.3 describes it (the real one may carry a token: see the token tests).</summary>
    private static string ExtractForm(int h, string extra = "", string formAttributes = "method=\"post\"") =>
        $"<form action=\"{ShowDocs}\" {formAttributes}>{extra}" +
        $"<button type=\"submit\" name=\"ShowKF\" value=\"{Kaek}-0-{h}\">Απόσπασμα περιγραφικής βάσης</button>" +
        $"<button type=\"submit\" name=\"ShowKD\" value=\"{Kaek}-0-{h}\">Απόσπασμα χωρικής βάσης</button></form>";

    private static string RecordPage(string owners, string docs, string? form) =>
        "<html><head><title>Αναζήτηση</title></head><body>" +
        "<a href=\"/Professionals/Account/UserProfile\">ΔΟΚΙΜΑΣΤΗΣ (Δικηγόρος)</a> <a href=\"/Professionals/Account/Logout\">Αποσύνδεση</a>" +
        "<table class=\"MasterGrid\"><tr><th colspan=\"5\">Τρέχοντες Κύριοι</th></tr>" +
        "<tr class=\"HeaderRow\"><th>ΑΦΜ</th><th>Επώνυμο</th><th>Όνομα</th><th>Όνομα Πατρός</th><th>Ποσοστό</th></tr>" + owners + "</table>" +
        "<table class=\"MasterGrid\"><tr><th>Έγγραφα</th><th></th></tr>" + docs + "</table>" + (form ?? "") + "</body></html>";

    private static List<PdfTextLine> Lines(params string[] texts)
    {
        var y = 800.0;
        return texts.Select(t => new PdfTextLine(1, y -= 16, 40, t)).ToList();
    }

    /// <summary>A plot's descriptive extract: an initial registration, the auction 5413/2024 of bare ownership, a usufruct.</summary>
    private static readonly string[] PlotExtract =
    {
        "ΕΛΛΗΝΙΚΗ ΔΗΜΟΚΡΑΤΙΑ",
        "ΥΠΟΥΡΓΕΙΟ ΨΗΦΙΑΚΗΣ ΔΙΑΚΥΒΕΡΝΗΣΗΣ",
        "ΕΛΛΗΝΙΚΟ ΚΤΗΜΑΤΟΛΟΓΙΟ",
        "ΚΤΗΜΑΤΟΛΟΓΙΚΟ ΓΡΑΦΕΙΟ ΔΟΚΙΜΩΝ",
        "ΚΑΕΚ 99 000 00 00 001 / 0 / 0",
        "ΑΠΟΣΠΑΣΜΑ ΠΕΡΙΓΡΑΦΙΚΗΣ ΒΑΣΗΣ",
        "Το παρόν αποτελεί ενημερωτικό υλικό και δεν έχει αποδεικτική ισχύ.",
        "ΕΝΗΜΕΡΩΤΙΚΟ ΥΛΙΚΟ",
        "ΓΕΩΤΕΜΑΧΙΟ",
        "ΕΜΒΑΔΟΝ ΤΙΤΛΟΥ 1.200,50",
        "ΕΜΒΑΔΟΝ 1.198,20",
        "ΔΙΕΥΘΥΝΣΗ ΟΔΟΣ ΔΟΚΙΜΗΣ 12",
        "ΘΕΣΗ ΚΑΤΩ ΔΟΚΙΜΑΣΤΙΚΗ ΘΕΣΗ",
        "ΤΚ 10000",
        "ΔΗΜΟΣ/ΚΟΙΝΟΤΗΤΑ ΔΟΚΙΜΩΝ",
        "ΠΑΡΑΤΗΡΗΣΕΙΣ Ο.Τ. 18",
        "ΑΡΜΟΔΙΟΤΗΤΑ ΚΓ ΔΟΚΙΜΩΝ ΑΠΟ 01/01/2020",
        "ΑΝΑΜΟΡΦΩΣΕΙΣ / ΑΛΛΑΓΕΣ",
        "ΕΜΒΑΔΟΝ 1.200,00",
        "ΑΡΧΙΚΕΣ ΕΓΓΡΑΦΕΣ",
        "ΚΥΡΙΟΤΗΤΑ",
        "ΠΛΗΡΗΣ",
        "Αριθμός & Ημ/νία Καταχώρισης 10 01/02/2010 ΚΓ ΔΟΚΙΜΩΝ ΟΡΙΣΤΙΚΗ ΚΑΤΑΧΩΡΙΣΗ",
        "Πράξη/εις ΑΡΧΙΚΗ ΕΓΓΡΑΦΗ",
        "ΚΥΡΙΟΣ (ΠΟΣΟΣΤΟ: 100/100)",
        "Ονοματεπώνυμο Φυσικού Προσώπου ΠΑΛΑΙΟΠΟΥΛΟΣ ΑΝΔΡΕΑΣ",
        "Α.Φ.Μ. 100000005",
        "ΜΕΤΑΓΕΝΕΣΤΕΡΕΣ ΕΓΓΡΑΦΕΣ",
        "ΚΥΡΙΟΤΗΤΑ",
        "ΨΙΛΗ",
        "Αριθμός & Ημ/νία Καταχώρισης 5.413 18/07/2024 ΚΓ ΔΟΚΙΜΩΝ ΟΡΙΣΤΙΚΗ ΚΑΤΑΧΩΡΙΣΗ",
        "Πράξη/εις ΠΩΛΗΣΗ ΔΙΑ ΠΛΕΙΣΤΗΡΙΑΣΜΟΥ",
        "Έγγραφο ΣΥΜΒΟΛΑΙΟΓΡΑΦΙΚΟ ΕΓΓΡΑΦΟ, 4876 - 10/7/2024",
        "ΔΟΚΙΜΑΣΤΗΣ ΣΥΜΒΟΛΑΙΟΓΡΑΦΟΣ, ΔΟΚΙΜΙ",
        "ΠΡΟΗΝ ΚΥΡΙΟΣ (ΠΟΣΟΣΤΟ: 100/100)",
        "Ονοματεπώνυμο Φυσικού Προσώπου ΠΑΛΑΙΟΠΟΥΛΟΣ ΑΝΔΡΕΑΣ",
        "Ονοματεπώνυμο Πατέρα ΠΑΛΑΙΟΠΟΥΛΟΣ ΓΕΩΡΓΙΟΣ",
        "Α.Φ.Μ. 100000005",
        "ΝΕΟΣ ΚΥΡΙΟΣ (ΠΟΣΟΣΤΟ: 100/100)",
        "Ονοματεπώνυμο Φυσικού Προσώπου ΑΓΟΡΑΣΤΟΠΟΥΛΟΥ ΕΛΕΝΗ",
        "Ονοματεπώνυμο Πατέρα ΑΓΟΡΑΣΤΟΠΟΥΛΟΣ ΝΙΚΟΛΑΟΣ",
        "Ονοματεπώνυμο Μητέρας ΔΟΚΙΜΗ ΜΑΡΙΑ",
        "Ημερομηνία & Τόπος Γέννησης 01/01/1970 ΑΘΗΝΑ",
        "Α.Φ.Μ. " + PersonTax,
        "Διεύθυνση ΟΔΟΣ ΔΟΚΙΜΗΣ 5, 10000 ΑΘΗΝΑ",
        "Α.Δ.Τ. ΧΧ000000 02/03/2001 Τ.Α. ΔΟΚΙΜΩΝ",
        "ΔΟΥΛΕΙΕΣ",
        "ΕΠΙΚΑΡΠΙΑ",
        "Αριθμός & Ημ/νία Καταχώρισης 5.414 18/07/2024 ΚΓ ΔΟΚΙΜΩΝ ΟΡΙΣΤΙΚΗ ΚΑΤΑΧΩΡΙΣΗ",
        "Πράξη/εις ΣΥΣΤΑΣΗ ΕΠΙΚΑΡΠΙΑΣ",
        "ΔΙΚΑΙΟΥΧΟΣ (ΠΟΣΟΣΤΟ: 100/100)",
        "Ονοματεπώνυμο Φυσικού Προσώπου ΔΟΚΙΜΟΠΟΥΛΟΣ ΠΕΤΡΟΣ",
        "Α.Φ.Μ. 100000006",
    };

    /// <summary>A flat's extract: the areas as a table row, the OT only in a change, a company as the new owner.</summary>
    private static string[] FlatExtract(int h) => new[]
    {
        "ΚΑΕΚ 99 000 00 00 001 / 0 / " + h,
        "ΑΠΟΣΠΑΣΜΑ ΠΕΡΙΓΡΑΦΙΚΗΣ ΒΑΣΗΣ",
        "ΟΡΙΖΟΝΤΙΟΣ ΙΔΙΟΚΤΗΣΙΑ",
        "ΕΜΒΑΔΟΝ ΤΙΤΛΟΥ ΕΜΒΑΔΟΝ",
        "85,20 84,90",
        "ΟΡΟΦΟΣ 1",
        "ΠΑΡΑΤΗΡΗΣΕΙΣ ΔΙΑΜΕΡΙΣΜΑ Α1",
        "ΑΝΑΜΟΡΦΩΣΕΙΣ / ΑΛΛΑΓΕΣ",
        "ΠΑΡΑΤΗΡΗΣΕΙΣ Ο.Τ. 18Α",
        "ΜΕΤΑΓΕΝΕΣΤΕΡΕΣ ΕΓΓΡΑΦΕΣ",
        "ΚΥΡΙΟΤΗΤΑ",
        "ΠΛΗΡΗΣ",
        "Αριθμός & Ημ/νία Καταχώρισης 31 02/04/2024 ΚΓ ΔΟΚΙΜΩΝ",
        "Πράξη/εις ΑΓΟΡΑΠΩΛΗΣΙΑ",
        "Έγγραφο ΣΥΜΒΟΛΑΙΟΓΡΑΦΙΚΟ ΕΓΓΡΑΦΟ, 4321 - 28/3/2024",
        "ΠΡΟΗΝ ΚΥΡΙΟΣ (ΠΟΣΟΣΤΟ: 100/100)",
        "Ονοματεπώνυμο Φυσικού Προσώπου ΠΩΛΗΤΟΠΟΥΛΟΣ ΓΕΩΡΓΙΟΣ",
        "ΝΕΟΣ ΚΥΡΙΟΣ (ΠΟΣΟΣΤΟ: 100/100)",
        "Επωνυμία ΔΟΚΙΜΑΣΤΙΚΗ ΑΝΩΝΥΜΗ ΕΤΑΙΡΕΙΑ",
        "Έδρα ΑΘΗΝΑ",
        "Α.Φ.Μ. " + CompanyTax,
    };

    private static readonly string[] Diagram =
    {
        "ΚΤΗΜΑΤΟΛΟΓΙΚΟ ΓΡΑΦΕΙΟ ΔΟΚΙΜΩΝ",
        "ΑΠΟΣΠΑΣΜΑ ΧΩΡΙΚΗΣ ΒΑΣΗΣ",
        "ΝΟΜΟΣ: ΔΟΚΙΜΩΝ ΟΤΑ: ΔΗΜΟΣ ΔΟΚΙΜΩΝ",
        "ΦΥΛΛΟ ΧΑΡΤΟΥ: 6411/7 ΚΑΕΚ: " + Kaek,
        "ΚΛΙΜΑΚΑ 1:1000",
        "990000000002",
        "99 000 00 00 003",
        "ΟΔΟΣ 38",
        Kaek,
    };

    /// <summary>The summary of 5413/2024 (V1's layout, brief V1 §2.5): the auction with its price.</summary>
    private static readonly string[] AuctionSummary =
    {
        "ΠΕΡΙΛΗΨΗ ΚΑΤΑΧΩΡΙΣΗΣ ΕΓΓΡΑΠΤΕΑΣ ΠΡΑΞΗΣ",
        "1) ΣΤΟΙΧΕΙΑ ΕΓΓΡΑΦΟΥ",
        "ΑΡΙΘΜΟΣ ΕΓΓΡΑΦΟΥ: 4876 ΕΚΔΟΥΣΑ ΑΡΧΗ/ΔΙΚΑΣΤΗΡΙΟ: ΣΥΜΒΟΛΑΙΟΓΡΑΦΟΣ",
        "ΗΜΕΡΟΜΗΝΙΑ: 10/07/2024 ΟΝΟΜ/ΜΟ: ΔΟΚΙΜΑΣΤΗΣ / ΔΟΚΙΜΙ",
        "2) ΕΙΔΟΣ ΕΓΓΡΑΠΤΕΑΣ ΠΡΑΞΗΣ / ΑΝΤΙΚΕΙΜΕΝΙΚΗ ΑΞΙΑ / ΑΞΙΑ ΣΥΝΑΛΛΑΓΗΣ",
        "1) Περίληψη κατακυρωτικής έκθεσης / 300.000,00 / 318000",
        "ΧΩΡΙΚΗ ΜΕΤΑΒΟΛΗ",
        "ΚΑΕΚ 990000000001 / 0 / 0",
        "ΣΤΟΙΧΕΙΑ ΠΡΟΣΩΠΩΝ",
        "ΦΥΣΙΚΟ ΠΡΟΣΩΠΟ ΝΟΜΙΚΟ ΠΡΟΣΩΠΟ",
        "Όνομα / Είδος Νομικού Προσώπου ΕΛΕΝΗ",
        "Επώνυμο / Επωνυμία ΑΓΟΡΑΣΤΟΠΟΥΛΟΥ",
        "Όνομα Πατρός ΝΙΚΟΛΑΟΣ",
        "ΑΦΜ-ΔΟΥ " + PersonTax,
        "Διεύθυνση (Οδός, Αριθμός, ΤΚ, Πόλη) ΟΔΟΣ ΔΟΚΙΜΗΣ, 5, 10000, ΑΘΗΝΑ",
        "Είδος Δικαιώματος ΚΥΡΙΟΤΗΤΑ-ΨΙΛΗ Ποσοστό Δικαιώματος 100 / 100",
        "Ιδιότητα στην Εγγραπτέα Πράξη ΑΠΟΚΤΩΝ",
        "Παρατηρήσεις / Σχόλια ΥΠΕΡΘΕΜΑΤΙΣΤΡΙΑ",
    };

    // ---- the allow-list: the two extracts, exactly ----------------------------------------------------------------------

    [Theory]
    [InlineData("ShowKF=990000000001-0-0")]
    [InlineData("ShowKD=990000000001-0-3")]
    [InlineData("__RequestVerificationToken=CfDJ8Abc_def-123%2B%3D&ShowKF=990000000001-0-0")]
    [InlineData("ShowKD=990000000001-0-0&__RequestVerificationToken=CfDJ8Abc")]
    public void The_extracts_are_allowed_with_exactly_their_forms_body(string body)
    {
        CadastreAllowList.Check("POST", ShowDocs, body);
    }

    [Theory]
    [InlineData("POST", ShowDocs, "")]
    [InlineData("POST", ShowDocs, "ShowKF=99000000000-0-0")]                         // 11 digits
    [InlineData("POST", ShowDocs, "ShowKF=990000000001-0-0&x=1")]                    // another field
    [InlineData("POST", ShowDocs, "ShowKX=990000000001-0-0")]
    [InlineData("POST", ShowDocs, "ShowKF=990000000001-0-0&ShowKD=990000000001-0-0")] // both buttons
    [InlineData("POST", ShowDocs, "__RequestVerificationToken=a&ShowKF=990000000001-0-0&__RequestVerificationToken=b")]
    [InlineData("POST", ShowDocs, "ShowKF=990000000001-0-0&")]
    [InlineData("POST", ShowDocs + "?x=1", "ShowKF=990000000001-0-0")]
    [InlineData("GET", ShowDocs + "?ShowKF=990000000001-0-0", null)]
    [InlineData("POST", "/Professionals/Inquiry/Main/FetchEstates", "ShowKF=990000000001-0-0")]
    [InlineData("POST", "/Professionals/Contracts/Main/ShowDocs", "ShowKF=990000000001-0-0")]
    [InlineData("GET", "/Professionals/Inquiry/Main/SearchByPerson", null)]            // person search: still never
    [InlineData("POST", "/Professionals/Inquiry/Main/FetchPersons", "ShowKF=990000000001-0-0")]
    [InlineData("GET", "/Professionals/Inquiry/Main/SearchByAct", null)]               // act search: still never
    [InlineData("POST", "/Professionals/Inquiry/Main/FetchActs", "ShowKF=990000000001-0-0")]
    public void Anything_else_about_the_extracts_is_refused(string method, string path, string? body)
    {
        Assert.Throws<CadastreBlockedException>(() => CadastreAllowList.Check(method, path, body));
    }

    [Fact]
    public void The_page_script_allows_the_extracts_only_as_their_form_posts_them()
    {
        var script = CadastreAllowList.RequestScript;
        Assert.Contains("/professionals/inquiry/main/showdocs", script);
        Assert.Contains("__RequestVerificationToken=", script);
        Assert.Contains("Show(?:KF|KD)", script);
        Assert.Contains("an extract only as its own form posts it", script);
        Assert.Contains("redirect: 'manual'", script);
        Assert.True(script.IndexOf("if (!ok) blocked", StringComparison.Ordinal) < script.IndexOf("await fetch(", StringComparison.Ordinal));
        Assert.Contains("new TextDecoder('utf-8')", script); // not a PDF: the site's own words come back
    }

    [Fact]
    public void A_document_reply_that_is_not_a_PDF_stops_with_the_sites_own_words()
    {
        var reply = new SiteResponse(200, "https://ktimatologio.gov.gr" + ShowDocs, false, "text/html",
            "<html><body><p>Η υπηρεσία θα χρεωθεί. Συνέχεια;</p></body></html>", null);
        var ex = Assert.Throws<CadastreTroubleException>(() => CadastreReplyCheck.ThrowOnTrouble("POST", ShowDocs, true, reply));
        Assert.Contains("Η υπηρεσία θα χρεωθεί", ex.Message);
    }

    // ---- the extract's form ---------------------------------------------------------------------------------------------

    private static RecordPageData Page(string form) => CadastrePages.ParseRecord(RecordPage("", "", form));
    private static readonly string RecordPath = $"/Professionals/Inquiry/Main/viewResults/{Kaek}-0-0?p=1";

    [Fact]
    public void The_extracts_body_is_what_the_form_itself_would_post()
    {
        Assert.Equal("ShowKF=990000000001-0-0", ExtractRequest.Plan(Page(ExtractForm(0)), RecordPath, ExtractRequest.Descriptive, Kaek, 0, 0)!.Body);
        Assert.Equal("ShowKD=990000000001-0-0", ExtractRequest.Plan(Page(ExtractForm(0)), RecordPath, ExtractRequest.Spatial, Kaek, 0, 0)!.Body);

        // An anti-forgery token (unverified, brief V2 §1.1): sent with the button, in the form's order.
        var token = ExtractForm(0, "<input name=\"__RequestVerificationToken\" type=\"hidden\" value=\"CfDJ8Made+Up/Token=\">");
        Assert.Equal("__RequestVerificationToken=CfDJ8Made%2BUp%2FToken%3D&ShowKF=990000000001-0-0",
            ExtractRequest.Plan(Page(token), RecordPath, ExtractRequest.Descriptive, Kaek, 0, 0)!.Body);

        // No button on the page: nothing to request.
        Assert.Null(ExtractRequest.Plan(Page(""), RecordPath, ExtractRequest.Descriptive, Kaek, 0, 0));

        // A harmless script hook is noted for the log, not a reason to stop.
        var hooked = ExtractForm(0, formAttributes: "method=\"post\" onsubmit=\"showLoading()\"");
        Assert.Contains("showLoading", Assert.Single(ExtractRequest.Plan(Page(hooked), RecordPath, ExtractRequest.Descriptive, Kaek, 0, 0)!.Notes));
    }

    [Theory]
    [InlineData("<input type=\"hidden\" name=\"ProfileId\" value=\"7\">", "method=\"post\"", "ProfileId")]              // a field we don't know
    [InlineData("<input type=\"text\" name=\"__RequestVerificationToken\" value=\"x\">", "method=\"post\"", "__RequestVerificationToken")] // not hidden
    [InlineData("", "", "method")]                                                                                         // GET
    [InlineData("", "method=\"post\" data-ajax=\"true\"", "AJAX")]
    [InlineData("", "method=\"post\" enctype=\"multipart/form-data\"", "multipart")]
    [InlineData("", "method=\"post\" onsubmit=\"return confirm('Η αναζήτηση χρεώνεται 2 €')\"", "χρεώνεται")]        // a charging notice
    [InlineData("", "method=\"post\" data-confirm=\"Θα χρεωθείτε\"", "χρεωθείτε")]
    public void Anything_unexpected_on_the_form_stops_before_anything_is_sent(string extra, string attributes, string shown)
    {
        var ex = Assert.Throws<CadastreTroubleException>(() =>
            ExtractRequest.Plan(Page(ExtractForm(0, extra, attributes)), RecordPath, ExtractRequest.Descriptive, Kaek, 0, 0));
        Assert.Contains(shown, ex.Message);
        Assert.Contains("Nothing was sent", ex.Message);
    }

    [Fact]
    public void A_button_for_another_unit_or_another_action_stops_too()
    {
        Assert.Throws<CadastreTroubleException>(() =>
            ExtractRequest.Plan(Page(ExtractForm(1)), RecordPath, ExtractRequest.Descriptive, Kaek, 0, 0)); // the button says /0/1
        var elsewhere = ExtractForm(0).Replace(ShowDocs, "/Professionals/Contracts/Main/ShowDocs");
        Assert.Throws<CadastreTroubleException>(() =>
            ExtractRequest.Plan(Page(elsewhere), RecordPath, ExtractRequest.Descriptive, Kaek, 0, 0));
    }

    // ---- the descriptive extract (KF) -----------------------------------------------------------------------------------

    [Fact]
    public void A_plots_descriptive_extract_gives_the_property_the_OT_and_every_registration()
    {
        var x = DescriptiveExtractPdf.Parse(Lines(PlotExtract));

        Assert.True(x.Readable);
        Assert.Equal(("990000000001/0/0", "ΓΕΩΤΕΜΑΧΙΟ"), (x.Kaek, x.PropertyKind));
        Assert.Equal((1200.50m, 1198.20m), (x.TitleAreaSqm!.Value, x.MeasuredAreaSqm!.Value));
        Assert.Equal(("Ο.Τ. 18", "18"), (x.RemarksText, x.OtText));
        Assert.Equal("ΚΓ ΔΟΚΙΜΩΝ ΑΠΟ 01/01/2020", x.OfficeText);
        Assert.Equal("ΟΔΟΣ ΔΟΚΙΜΗΣ 12, ΚΑΤΩ ΔΟΚΙΜΑΣΤΙΚΗ ΘΕΣΗ, 10000, ΔΟΚΙΜΩΝ", x.AddressText);
        Assert.Equal(new[] { ExtractSection.Initial, ExtractSection.Later, ExtractSection.Servitude }, x.Entries.Select(e => e.Section));

        var auction = x.Entries[1];
        Assert.Equal(("ΚΥΡΙΟΤΗΤΑ ΨΙΛΗ", "5413", 2024, new DateOnly(2024, 7, 18)), (auction.RightText, auction.RegNo, auction.RegYear!.Value, auction.RegDate!.Value));
        Assert.Equal(("ΚΓ ΔΟΚΙΜΩΝ", "ΟΡΙΣΤΙΚΗ ΚΑΤΑΧΩΡΙΣΗ"), (auction.Office, auction.RegStatusText));
        Assert.Equal(("ΠΩΛΗΣΗ ΔΙΑ ΠΛΕΙΣΤΗΡΙΑΣΜΟΥ", ActType.Auction), (auction.ActText, auction.ActTypeCode));
        Assert.Equal(("4876", new DateOnly(2024, 7, 10)), (auction.DeedNo, auction.DeedDate!.Value));
        Assert.Equal("ΔΟΚΙΜΑΣΤΗΣ ΣΥΜΒΟΛΑΙΟΓΡΑΦΟΣ, ΔΟΚΙΜΙ", auction.IssuerText);
        Assert.Null(auction.Price); // no price on the extract (brief §1.2): it comes from the summary

        Assert.Equal(2, auction.Persons.Count);
        var before = auction.Persons[0];
        Assert.Equal((PartyRole.Transferor, "ΠΑΛΑΙΟΠΟΥΛΟΣ ΑΝΔΡΕΑΣ", "100000005", "ΠΑΛΑΙΟΠΟΥΛΟΣ ΓΕΩΡΓΙΟΣ"),
            (before.Role, before.Name, before.TaxId, before.Details.FatherName));
        var buyer = auction.Persons[1];
        Assert.Equal((PartyRole.Acquirer, "100/100", "ΑΓΟΡΑΣΤΟΠΟΥΛΟΥ ΕΛΕΝΗ", PersonTax), (buyer.Role, buyer.ShareText, buyer.Name, buyer.TaxId));
        Assert.Equal(("ΑΓΟΡΑΣΤΟΠΟΥΛΟΣ ΝΙΚΟΛΑΟΣ", "ΔΟΚΙΜΗ ΜΑΡΙΑ"), (buyer.Details.FatherName, buyer.Details.MotherName));
        Assert.Equal((new DateOnly(1970, 1, 1), "ΑΘΗΝΑ"), (buyer.Details.BirthDate!.Value, buyer.Details.BirthPlace));
        Assert.Equal(("ΧΧ000000", new DateOnly(2001, 3, 2), "Τ.Α. ΔΟΚΙΜΩΝ"), (buyer.Details.IdCardNumber, buyer.Details.IdCardDate!.Value, buyer.Details.IdCardAuthority));
        Assert.Equal("ΟΔΟΣ ΔΟΚΙΜΗΣ 5, 10000 ΑΘΗΝΑ", buyer.Address);
        Assert.False(buyer.EntityLabel);

        var usufruct = x.Entries[2];
        Assert.Equal(("ΕΠΙΚΑΡΠΙΑ", "5414"), (usufruct.RightText, usufruct.RegNo));
        Assert.Equal((PartyRole.Other, "ΔΟΚΙΜΟΠΟΥΛΟΣ ΠΕΤΡΟΣ"), (usufruct.Persons[0].Role, usufruct.Persons[0].Name));

        // The summary to read: the newest later registration that is a sale or an auction.
        Assert.Same(auction, x.LastSale);
    }

    [Fact]
    public void A_flats_extract_reads_the_areas_from_a_table_row_the_OT_from_a_change_and_a_company()
    {
        var x = DescriptiveExtractPdf.Parse(Lines(FlatExtract(1)));

        Assert.Equal(("990000000001/0/1", "ΟΡΙΖΟΝΤΙΟΣ ΙΔΙΟΚΤΗΣΙΑ"), (x.Kaek, x.PropertyKind));
        Assert.Equal((85.20m, 84.90m), (x.TitleAreaSqm!.Value, x.MeasuredAreaSqm!.Value));
        Assert.Equal(("ΔΙΑΜΕΡΙΣΜΑ Α1", "18Α"), (x.RemarksText, x.OtText));
        var sale = Assert.Single(x.Entries);
        Assert.Equal((ActType.Sale, "31", new DateOnly(2024, 3, 28)), (sale.ActTypeCode, sale.RegNo, sale.DeedDate!.Value));
        var company = sale.Persons[1];
        Assert.Equal(("ΔΟΚΙΜΑΣΤΙΚΗ ΑΝΩΝΥΜΗ ΕΤΑΙΡΕΙΑ", true, CompanyTax, "ΑΘΗΝΑ"), (company.Name, company.EntityLabel, company.TaxId, company.Details.BirthPlace));
    }

    [Fact]
    public void An_extract_whose_layout_is_not_the_known_one_counts_as_a_scan()
    {
        Assert.False(DescriptiveExtractPdf.Parse(Lines("ΑΙΙΟΣΙΙΑΣΜΑ ΙΙΕΡΙΓΡΑΦΙΚΗΣ", "KAEK 9 9 0 0")).Readable); // no heading (a scan's OCR-less text)
        Assert.False(DescriptiveExtractPdf.Parse(Lines("ΑΠΟΣΠΑΣΜΑ ΠΕΡΙΓΡΑΦΙΚΗΣ ΒΑΣΗΣ", "κάτι άλλο")).Readable);   // the heading, nothing we know
        Assert.False(DescriptiveExtractPdf.Read(Encoding.ASCII.GetBytes("%PDF-1.4 not really a pdf")).Readable);
    }

    /// <summary>A real PDF (made here, made-up text) through PdfPig: labels and values drawn apart on one line become one line.</summary>
    [Fact]
    public void A_descriptive_extract_PDF_is_read_from_its_text_layer()
    {
        var fontPath = new[] { @"C:\Windows\Fonts\arial.ttf", "/System/Library/Fonts/Supplemental/Arial.ttf", "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf" }
            .FirstOrDefault(File.Exists);
        if (fontPath is null) { return; } // no font with Greek letters on this machine

        var b = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        var f = b.AddTrueTypeFont(File.ReadAllBytes(fontPath));
        var page = b.AddPage(595, 842);
        var y = 800.0;
        void Line(string label, string? value = null)
        {
            page.AddText(label, 10, new UglyToad.PdfPig.Core.PdfPoint(40, y), f);
            if (value is not null) page.AddText(value, 10, new UglyToad.PdfPig.Core.PdfPoint(260, y), f);
            y -= 16;
        }

        Line("ΚΑΕΚ", "99 000 00 00 001 / 0 / 0");
        Line("ΑΠΟΣΠΑΣΜΑ ΠΕΡΙΓΡΑΦΙΚΗΣ ΒΑΣΗΣ");
        Line("ΓΕΩΤΕΜΑΧΙΟ");
        Line("ΕΜΒΑΔΟΝ", "1.198,20");
        Line("ΠΑΡΑΤΗΡΗΣΕΙΣ", "Ο.Τ. 18");
        page.AddText("Ε", 48, new UglyToad.PdfPig.Core.PdfPoint(300, 700), f); // a watermark letter

        var x = DescriptiveExtractPdf.Read(b.Build());

        Assert.True(x.Readable);
        Assert.Equal(("990000000001/0/0", 1198.20m, "18"), (x.Kaek, x.MeasuredAreaSqm!.Value, x.OtText));
    }

    [Theory]
    [InlineData("Ο.Τ. 18", "18")]
    [InlineData("ΕΝΤΟΣ ΟΤ 205 ΤΟΥ ΣΧΕΔΙΟΥ", "205")]
    [InlineData("Ο.Τ.: 18Α", "18Α")]
    [InlineData("O.T. 7", "7")]          // Latin letters
    [InlineData("ΟΤΙ 5 ΜΕΤΡΑ", null)]
    [InlineData("ΚΟΤ 5", null)]
    [InlineData("ΧΩΡΙΣ ΑΡΙΘΜΟ", null)]
    public void The_OT_number_in_the_remarks(string remarks, string? ot)
    {
        Assert.Equal(ot, DescriptiveExtractPdf.OtOf(remarks));
    }

    // ---- the spatial extract (KD) ---------------------------------------------------------------------------------------

    [Fact]
    public void The_diagram_gives_its_header_and_the_neighbours_written_as_text()
    {
        var d = SpatialExtractPdf.Parse(Lines(Diagram));

        Assert.True(d.Readable);
        Assert.Equal((Kaek, "ΔΟΚΙΜΩΝ", "ΔΟΚΙΜΩΝ", "ΔΗΜΟΣ ΔΟΚΙΜΩΝ", "6411/7"), (d.Kaek, d.OfficeText, d.Prefecture, d.Municipality, d.MapSheet));
        Assert.Equal(new[] { "990000000002", "990000000003" }, d.Neighbours); // its own KAEK left out
        Assert.False(SpatialExtractPdf.Parse(Lines("ΟΔΟΣ 38", "990000000002")).Readable); // a drawing without its heading
    }

    // ---- one KAEK through the pipeline (fake site, real allow-list and checks) ------------------------------------------

    /// <summary>The site as the tests need it; a POST to ShowDocs is answered by its body. Records every request it gets.</summary>
    private sealed class FakeSite : ICadastreTransport
    {
        public readonly Dictionary<string, SiteResponse> Replies = new();
        public readonly List<string> Sent = new();

        private static string Key(string method, string path, string? body) => method + " " + path + (path == ShowDocs ? " " + body : "");

        public Task<SiteResponse> SendAsync(string method, string path, string? body, bool expectPdf)
        {
            Sent.Add(Key(method, path, body));
            return Task.FromResult(Replies.TryGetValue(Key(method, path, body), out var r) ? r
                : new SiteResponse(404, "https://ktimatologio.gov.gr" + path, false, "text/html", "<html>not found</html>", null));
        }

        public void Html(string method, string path, string html) =>
            Replies[Key(method, path, null)] = new SiteResponse(200, "https://ktimatologio.gov.gr" + path.Split('?')[0], false, "text/html; charset=utf-8", html, null);

        public void Pdf(string method, string path, string? body, string marker) =>
            Replies[Key(method, path, body)] = new SiteResponse(200, "https://ktimatologio.gov.gr" + path, false, "application/pdf", null,
                Encoding.ASCII.GetBytes("%PDF-" + marker), "attachment; filename=made-up.pdf");
    }

    private sealed class Pacer : IRequestPacer
    {
        public Task WaitTurnAsync(CancellationToken ct) => Task.CompletedTask;
        public void Done() { }
    }

    private const string Search = "POST /Professionals/Inquiry/Main/FetchEstates";
    private static string Record(int h) => $"GET /Professionals/Inquiry/Main/viewResults/{Kaek}-0-{h}?p=1";
    private static string Kf(int h) => $"POST {ShowDocs} ShowKF={Kaek}-0-{h}";
    private static string Kd(int h) => $"POST {ShowDocs} ShowKD={Kaek}-0-{h}";

    /// <summary>A KAEK with the given units, each with its record page, extract form and extract; the plot has the auction's summary.</summary>
    private static (FakeSite Site, CadastreFetcher Fetcher) Kaek_(int[] units, bool readableExtracts = true)
    {
        var site = new FakeSite();
        site.Html("POST", "/Professionals/Inquiry/Main/FetchEstates", Estates(units));
        foreach (var h in units)
        {
            var owners = h == 0
                ? Owner(PersonTax, "ΑΓΟΡΑΣΤΟΠΟΥΛΟΥ", "ΕΛΕΝΗ", "ΝΙΚΟΛΑΟΣ", "1 / 1")
                : Owner(CompanyTax, "ΔΟΚΙΜΑΣΤΙΚΗ ΑΝΩΝΥΜΗ ΕΤΑΙΡΕΙΑ", "", "", "1 / 1");
            var docs = h == 0
                ? Doc("↳ 5413/2024 ΔΟΚΙΜΩΝ (ΠΕΡΙΛΗΨΗ ΕΓΓΡΑΠΤΕΑΣ ΠΡΑΞΗΣ)", "a3", 0) + Doc("↳ 5414/2024 ΔΟΚΙΜΩΝ (ΠΕΡΙΛΗΨΗ ΕΓΓΡΑΠΤΕΑΣ ΠΡΑΞΗΣ)", "a4", 0) +
                  Doc("↳ 5413/2024 ΔΟΚΙΜΩΝ (ΑΙΤΗΣΗ ΚΑΤΑΧΩΡΙΣΗΣ)", "a5", 0) + Doc("ΣΥΜΒΟΛΑΙΟΓΡΑΦΙΚΟ ΕΓΓΡΑΦΟ 9009 - 25-07-1898", "f1", 0)
                : Doc($"↳ 31/2024 ΔΟΚΙΜΩΝ (ΠΕΡΙΛΗΨΗ ΕΓΓΡΑΠΤΕΑΣ ΠΡΑΞΗΣ)", "a1", h);
            site.Html("GET", $"/Professionals/Inquiry/Main/viewResults/{Kaek}-0-{h}?p=1", RecordPage(owners, docs, ExtractForm(h)));
            site.Pdf("POST", ShowDocs, $"ShowKF={Kaek}-0-{h}", "kf" + h);
            site.Pdf("POST", ShowDocs, $"ShowKD={Kaek}-0-{h}", "kd");
        }

        site.Pdf("GET", Scans("a3", 0), null, "auction");
        site.Pdf("GET", Scans("a4", 0), null, "never-read");
        site.Pdf("GET", Scans("a1", 1), null, "flat-sale");
        var fetcher = new CadastreFetcher(new GuardedCadastreSite(site, new Pacer()),
            readSummary: b => Encoding.ASCII.GetString(b) switch
            {
                "%PDF-auction" => SummaryPdf.Parse(Lines(AuctionSummary)),
                "%PDF-flat-sale" => SummaryPdf.Parse(Lines(AuctionSummary.Select(l => l
                    .Replace("1) Περίληψη κατακυρωτικής έκθεσης / 300.000,00 / 318000", "1) Αγοραπωλησία / 60.000,00 / 90000")
                    .Replace("ΚΑΕΚ 990000000001 / 0 / 0", "ΚΑΕΚ 990000000001 / 0 / 1")).ToArray())),
                var other => throw new InvalidOperationException("unexpected summary " + other),
            },
            readExtract: b => !readableExtracts ? DescriptiveExtractData.Unreadable : Encoding.ASCII.GetString(b) switch
            {
                "%PDF-kf0" => DescriptiveExtractPdf.Parse(Lines(PlotExtract)),
                var kf => DescriptiveExtractPdf.Parse(Lines(FlatExtract(int.Parse(kf["%PDF-kf".Length..])))),
            },
            readDiagram: b => Encoding.ASCII.GetString(b) == "%PDF-kd" ? SpatialExtractPdf.Parse(Lines(Diagram)) : throw new InvalidOperationException("unexpected diagram"));
        return (site, fetcher);
    }

    [Fact]
    public async Task A_plot_costs_five_requests_in_this_order_and_keeps_three_documents()
    {
        var (site, fetcher) = Kaek_(new[] { 0 });

        var got = await fetcher.FetchAsync(Kaek, default);

        Assert.Equal(new[] { Search, Record(0), Kf(0), Kd(0), "GET " + Scans("a3", 0) }, site.Sent);
        Assert.Equal(new[] { (DocumentKind.DescriptiveExtract, (string?)null), (DocumentKind.SpatialExtract, null), (DocumentKind.Summary, "5413") },
            got.Documents.Select(d => (d.Kind, d.RegNo)));
        Assert.All(got.Documents, d => Assert.Equal((0, 0), (d.UnitV, d.UnitH)));

        var unit = Assert.Single(got.Record.Units);
        Assert.Equal((1198.20m, "18", ExtractStatus.Read, "ΓΕΩΤΕΜΑΧΙΟ", "6411/7"), (unit.MeasuredAreaSqm!.Value, unit.OtText, unit.ExtractStatus, unit.PropertyKind, unit.MapSheet));
        Assert.Equal("990000000002 990000000003", unit.NeighbourKaeks);
        Assert.Equal(1200.50m, unit.TitleAreaSqm);

        // The owner: the record page's row, filled from the extract (parents, birth, ID card, address) - nothing dropped.
        var owner = Assert.Single(unit.Owners);
        Assert.Equal(("Person", PersonTax, "ΟΔΟΣ ΔΟΚΙΜΗΣ 5, 10000 ΑΘΗΝΑ"), (owner.Kind, owner.TaxId, owner.Address));
        Assert.Equal(("ΝΙΚΟΛΑΟΣ", "ΔΟΚΙΜΗ ΜΑΡΙΑ", new DateOnly(1970, 1, 1), "ΧΧ000000"), (owner.Details.FatherName, owner.Details.MotherName,
            owner.Details.BirthDate!.Value, owner.Details.IdCardNumber));
        Assert.Equal(("ΨΙΛΗ", PersonSource.RecordPage), (owner.Details.RightType, owner.Details.Source));

        // The price and objective value from the summary; the extract's registrations kept as Extract acts.
        var auction = Assert.Single(unit.Acts, a => a.Source == ActSource.Summary && a.ActTypeCode == ActType.Auction);
        Assert.Equal((318000m, 300000m, "5413", 2024), (auction.TransactionValue!.Value, auction.ObjectiveValue!.Value, auction.RegNo, auction.RegYear!.Value));
        var fromExtract = unit.Acts.Where(a => a.Source == ActSource.Extract).ToList();
        Assert.Equal(3, fromExtract.Count);
        Assert.Equal((ExtractSection.Later, new DateOnly(2024, 7, 18)), (fromExtract[1].Section, fromExtract[1].RegDate!.Value));
        Assert.Equal(PersonTax, fromExtract[1].Parties[1].TaxId);
        Assert.Contains(unit.Acts, a => a.Source == ActSource.RecordTitle); // the f item, kind and date
    }

    [Fact]
    public async Task A_building_reads_each_unit_up_to_the_cap_and_the_diagram_once()
    {
        var (site, fetcher) = Kaek_(new[] { 0, 1, 2 });

        var got = await fetcher.FetchAsync(Kaek, 2, default);

        // units × 2 + 3: the search, two units' record and extract, the plot's diagram once, the newest sale's summary.
        Assert.Equal(new[] { Search, Record(0), Kf(0), Record(1), Kf(1), Kd(0), "GET " + Scans("a3", 0) }, site.Sent);
        Assert.Single(site.Sent, s => s.Contains("ShowKD", StringComparison.Ordinal));
        Assert.Equal(new[] { CadastreStatus.Found, CadastreStatus.Found, CadastreStatus.NotRead }, got.Record.Units.Select(u => u.Status));
        var over = got.Record.Units[2];
        Assert.Equal(("2", 85.20m), (over.FloorText, over.TitleAreaSqm!.Value)); // the search row is kept
        Assert.Empty(over.Owners);
        Assert.Equal(1, got.Documents.Count(d => d.Kind == DocumentKind.SpatialExtract));

        // "All" reads every unit (and still one diagram).
        var (all, allFetcher) = Kaek_(new[] { 0, 1, 2 });
        var every = await allFetcher.FetchAsync(Kaek, null, default);
        Assert.Equal(7 + 2, all.Sent.Count);
        Assert.DoesNotContain(every.Record.Units, u => u.Status == CadastreStatus.NotRead);
        Assert.Single(all.Sent, s => s.Contains("ShowKD", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unreadable_extract_falls_back_to_the_newest_summaries_until_a_sale()
    {
        var (site, fetcher) = Kaek_(new[] { 0 }, readableExtracts: false);
        // V1's way: newest first is 5414 (not a sale), then 5413 (the auction).
        site.Pdf("GET", Scans("a4", 0), null, "auction");

        var got = await fetcher.FetchAsync(Kaek, default);

        Assert.Equal(new[] { Search, Record(0), Kf(0), Kd(0), "GET " + Scans("a4", 0) }, site.Sent);
        var unit = Assert.Single(got.Record.Units);
        Assert.Equal(ExtractStatus.NeedsOcr, unit.ExtractStatus);
        Assert.DoesNotContain(unit.Acts, a => a.Source == ActSource.Extract);
        Assert.Equal(318000m, Assert.Single(unit.Acts, a => a.Source == ActSource.Summary).TransactionValue);
        // The unreadable extract is stored all the same (marked unreadable); the summary that gave the price is the evidence.
        Assert.Equal(new[] { (DocumentKind.DescriptiveExtract, false), (DocumentKind.SpatialExtract, true), (DocumentKind.Summary, true) },
            got.Documents.Select(d => (d.Kind, d.TextReadable)));
    }

    [Fact]
    public async Task No_records_means_not_operating_and_no_document_is_requested()
    {
        var site = new FakeSite();
        site.Html("POST", "/Professionals/Inquiry/Main/FetchEstates", "<div id=\"GridEstates\">Δεν βρέθηκαν εγγραφές</div>");

        var got = await new CadastreFetcher(new GuardedCadastreSite(site, new Pacer())).FetchAsync(Kaek, default);

        Assert.Equal((CadastreStatus.NotOperating, 0), (got.Record.Status, got.Record.Units.Count));
        Assert.Equal(new[] { Search }, site.Sent);
        Assert.Empty(got.Documents);
    }

    [Theory]
    [InlineData("html")]
    [InlineData("other-kaek")]
    [InlineData("charging-hook")]
    public async Task Anything_unexpected_about_an_extract_stops_the_run(string trouble)
    {
        var (site, fetcher) = Kaek_(new[] { 0 });
        switch (trouble)
        {
            case "html": // a page instead of the PDF (e.g. a charging notice): its words are shown
                site.Replies[Kf(0)] = new SiteResponse(200, "https://ktimatologio.gov.gr" + ShowDocs, false, "text/html",
                    "<html><body>Δεν έχετε ενεργοποιήσει τη λειτουργία χρέωσης</body></html>", null);
                break;
            case "other-kaek": // the extract is about another property
                fetcher = new CadastreFetcher(new GuardedCadastreSite(site, new Pacer()),
                    readExtract: _ => DescriptiveExtractPdf.Parse(Lines(PlotExtract.Select(l => l.Replace("99 000 00 00 001 / 0 / 0", "99 000 00 00 077 / 0 / 0")).ToArray())));
                break;
            default:
                site.Html("GET", $"/Professionals/Inquiry/Main/viewResults/{Kaek}-0-0?p=1",
                    RecordPage("", "", ExtractForm(0, formAttributes: "method=\"post\" onsubmit=\"return confirm('Θα χρεωθείτε')\"")));
                break;
        }

        var ex = await Assert.ThrowsAsync<CadastreTroubleException>(() => fetcher.FetchAsync(Kaek, default));

        if (trouble == "html") Assert.Contains("Δεν έχετε ενεργοποιήσει τη λειτουργία χρέωσης", ex.Message);
        if (trouble == "other-kaek") Assert.Contains("990000000077/0/0", ex.Message);
        if (trouble == "charging-hook") Assert.DoesNotContain(site.Sent, s => s.Contains("ShowKF", StringComparison.Ordinal)); // never sent
        Assert.DoesNotContain(site.Sent, s => s.Contains("ShowKD", StringComparison.Ordinal));
    }

    // ---- a run: the record first, then its documents --------------------------------------------------------------------

    private sealed class Panel : IRecordsPanel
    {
        public readonly List<string> Lines = new();
        public Task StatusAsync(string text) { Lines.Add("status: " + text); return Task.CompletedTask; }
        public Task LogAsync(string text, string kind = "") { Lines.Add(kind + ": " + text); return Task.CompletedTask; }
        public Task ProgressAsync(RecordsProgress progress) => Task.CompletedTask;
        public Task FinishedAsync(string text) => Task.CompletedTask;
    }

    private static string TempFolder() => Path.Combine(Path.GetTempPath(), "nadlan-records-v2-test-" + Guid.NewGuid().ToString("N")[..8]);

    [Fact]
    public async Task A_run_stores_each_document_against_its_units_row_after_the_record()
    {
        var (site, _) = Kaek_(new[] { 0, 1 });
        var sink = new CadastreRecordsImporterTests.Sink();
        var folder = TempFolder();
        var run = new RecordsRun(new GuardedCadastreSite(site, new Pacer()), sink, new Panel(), TextWriter.Null, folder,
            readSummary: b => Encoding.ASCII.GetString(b) == "%PDF-auction" ? SummaryPdf.Parse(Lines(AuctionSummary)) : throw new InvalidOperationException(),
            readExtract: b => Encoding.ASCII.GetString(b) == "%PDF-kf0" ? DescriptiveExtractPdf.Parse(Lines(PlotExtract)) : DescriptiveExtractPdf.Parse(Lines(FlatExtract(1))),
            readDiagram: _ => SpatialExtractPdf.Parse(Lines(Diagram)));

        var summary = await run.RunAsync(new[] { Kaek }, default);

        Assert.StartsWith("Done: 1 of 1", summary);
        Assert.Contains("4 document(s) stored", summary);
        var saved = Assert.Single(sink.Saved);
        Assert.Equal(new[] { DocumentKind.DescriptiveExtract, DocumentKind.DescriptiveExtract, DocumentKind.SpatialExtract, DocumentKind.Summary },
            sink.Uploaded.Select(u => u.Doc.Kind));
        Assert.Equal(new long[] { 101, 102, 101, 101 }, sink.Uploaded.Select(u => u.FetchId)); // each to its unit's row (the sink numbers them 101, 102)
        var log = await File.ReadAllTextAsync(Directory.GetFiles(folder).Single());
        Assert.Contains("Documents", log);
        Assert.DoesNotContain("ΑΓΟΡΑΣΤΟΠΟΥΛΟΥ", log); // the local log has no names
        Assert.DoesNotContain(PersonTax, log);
        Directory.Delete(folder, true);
    }

    [Fact]
    public async Task A_document_GreekPlot_does_not_store_stops_the_run()
    {
        var (site, _) = Kaek_(new[] { 0 });
        var sink = new CadastreRecordsImporterTests.Sink { RefuseUpload = "HTTP 503: File storage is not configured." };
        var run = new RecordsRun(new GuardedCadastreSite(site, new Pacer()), sink, new Panel(), TextWriter.Null, TempFolder(),
            readSummary: _ => SummaryPdf.Parse(Lines(AuctionSummary)), readExtract: _ => DescriptiveExtractPdf.Parse(Lines(PlotExtract)),
            readDiagram: _ => SpatialExtractPdf.Parse(Lines(Diagram)));

        var summary = await run.RunAsync(new[] { Kaek, "990000000002" }, default);

        Assert.Contains("was not stored", summary);
        Assert.Contains("File storage is not configured", summary);
        Assert.DoesNotContain(site.Sent, s => s.Contains("990000000002", StringComparison.Ordinal)); // the next KAEK is never asked
    }
}
