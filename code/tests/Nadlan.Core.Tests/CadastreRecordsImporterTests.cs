using System.Text;
using Nadlan.KaekImporter.Cadastre;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;

namespace Nadlan.Core.Tests;

/// <summary>
/// The importer's owners &amp; sales module (brief AI/Remarks/CodingAgent-KtimaRecords-2026-11-24.md). Every fixture here is
/// written by hand from the redacted layouts in the brief (§2.2, §2.3, §2.5): the names, KAEKs and tax numbers are made up.
/// </summary>
public class CadastreRecordsImporterTests
{
    // ---- made-up fixtures ----------------------------------------------------------------------------------------------

    private const string Kaek = "990000000001";
    private const string PersonTax = "100000001";   // made up
    private const string CompanyTax = "090000009";  // made up

    private static string EstatesFragment(params (int V, int H, string Floor, string Area)[] units) =>
        "<table class=\"Grid\"><tr class=\"HeaderRow\"><th>ΚΑΕΚ</th><th>Όροφος</th><th>Εμβαδόν</th><th>Αριθμός</th><th>Ποσοστό</th>" +
        "<th>Οδός</th><th>Αριθμός</th><th>ΤΚ</th><th>Θέση</th><th>Δήμος</th><th></th></tr>" +
        string.Concat(units.Select(u =>
            $"<tr class=\"NormalRow\"><td>{Kaek}/{u.V}/ {u.H}</td><td>{u.Floor}</td><td>{u.Area}</td><td>Α{u.H}</td><td>{(u.H == 0 ? "" : "9/1000")}</td>" +
            "<td>ΟΔΟΣ ΔΟΚΙΜΗΣ</td><td>12</td><td>10000</td><td>ΔΟΚΙΜΑΣΤΙΚΗ ΘΕΣΗ</td><td>ΔΗΜΟΣ ΔΟΚΙΜΩΝ</td>" +
            $"<td class=\"RowCommands\"><a class=\"Icon IconMore\" href=\"/Professionals/Inquiry/Main/viewResults/{Kaek}-{u.V}-{u.H}?p=1\"></a></td></tr>")) +
        "</table>";

    private static string Owner(string afm, string surname, string name, string father, string share) =>
        $"<tr class=\"NormalRow\"><td>{afm}</td><td>{surname}</td><td>{name}</td><td>{father}</td><td>{share}</td></tr>";

    private static string Doc(string label, string item, int h = 1) =>
        $"<tr class=\"NormalRow\"><td>{label}</td><td><a class=\"Icon IconMore\" title=\"Επιλέξτε\" " +
        $"href=\"/Professionals/Inquiry/Main/showScans?KAEK={Kaek}-0-{h}&amp;item={item}&amp;pid=77001\"></a></td></tr>";

    private static string RecordPage(string owners, string docs) =>
        "<html><head><title>Αναζήτηση</title></head><body>" +
        "<a href=\"/Professionals/Account/UserProfile\">ΔΟΚΙΜΑΣΤΗΣ (Δικηγόρος)</a> <a href=\"/Professionals/Account/Logout\">Αποσύνδεση</a>" +
        "<table class=\"MasterGrid\"><tr><th colspan=\"5\">Τρέχοντες Κύριοι</th></tr>" +
        "<tr class=\"HeaderRow\"><th>ΑΦΜ</th><th>Επώνυμο</th><th>Όνομα</th><th>Όνομα Πατρός</th><th>Ποσοστό</th></tr>" + owners + "</table>" +
        "<table class=\"MasterGrid\"><tr><th>Τίτλοι και έγγραφα αρχικών εγγραφών</th><th></th></tr>" + docs + "</table>" +
        "<form action=\"/Professionals/Inquiry/Main/ShowDocs\" method=\"post\"><button name=\"ShowKF\" value=\"x\">Απόσπασμα</button></form>" +
        "</body></html>";

    /// <summary>Lines one under the other (16 pt apart) at the label column, as PdfPig gives them.</summary>
    private static List<PdfTextLine> Lines(params string[] texts)
    {
        var y = 800.0;
        return texts.Select(t => new PdfTextLine(1, y -= 16, 40, t)).ToList();
    }

    private static readonly string[] SaleHead =
    {
        "ΠΕΡΙΛΗΨΗ ΚΑΤΑΧΩΡΙΣΗΣ ΕΓΓΡΑΠΤΕΑΣ ΠΡΑΞΗΣ",
        "Άρθρου 14 § 4 ν. 2664/1998",
        "1) ΣΤΟΙΧΕΙΑ ΕΓΓΡΑΦΟΥ",
        "ΕΙΔΟΣ ΕΓΓΡΑΦΟΥ: ΣΥΜΒΟΛΑΙΟΓΡΑΦΙΚΟ ΕΓΓΡΑΦΟ",
        "ΑΡΙΘΜΟΣ ΕΓΓΡΑΦΟΥ: 4321 ΕΚΔΟΥΣΑ ΑΡΧΗ/ΔΙΚΑΣΤΗΡΙΟ: ΣΥΜΒΟΛΑΙΟΓΡΑΦΟΣ",
        "ΗΜΕΡΟΜΗΝΙΑ: 28/03/2024 ΟΝΟΜ/ΜΟ: ΔΟΚΙΜΑΣΤΗ ΜΑΡΙΑ / ΑΘΗΝΑ",
        "2) ΕΙΔΟΣ ΕΓΓΡΑΠΤΕΑΣ ΠΡΑΞΗΣ / ΑΝΤΙΚΕΙΜΕΝΙΚΗ ΑΞΙΑ / ΑΞΙΑ ΣΥΝΑΛΛΑΓΗΣ",
        "1) Αγοραπωλησία / 63.430,03 / 21000",
        "ΧΩΡΙΚΗ ΜΕΤΑΒΟΛΗ",
        "ΠΕΡΙΓΡΑΦΗ ΑΚΙΝΗΤΟΥ /ΩΝ",
        "ΚΑΕΚ 990000000001 / 0 / 1",
        "ΕΙΔΟΣ ΑΚΙΝΗΤΟΥ ΑΠΛΗ ΟΡΙΖΟΝΤΙΟΣ ΙΔΙΟΚΤΗΣΙΑ",
        "ΣΤΟΙΧΕΙΑ ΠΡΟΣΩΠΩΝ",
    };

    private static string[] PersonBlock(string first, string surname, string father, string afm, string address, string right, string share, string role, string comment) =>
        new[]
        {
            "ΦΥΣΙΚΟ ΠΡΟΣΩΠΟ ΝΟΜΙΚΟ ΠΡΟΣΩΠΟ",
            "Όνομα / Είδος Νομικού Προσώπου " + first,
            "Επώνυμο / Επωνυμία " + surname,
            "Όνομα Πατρός " + father,
            "Επώνυμο Πατρός " + (father.Length > 0 ? surname : ""),
            "Όνομα Μητρός " + (father.Length > 0 ? "ΔΟΚΙΜΗ" : ""),
            "Ημ/νία & Τόπος Γέννησης (ή Έδρα ΝΠ) " + (father.Length > 0 ? "01/01/1970 ΑΘΗΝΑ" : ""),
            "ΑΔΤ, Ημ/νία Έκδοσης, Εκδ. Αρχή " + (father.Length > 0 ? "ΧΧ000000, 01/01/0001, Τ.Α. ΔΟΚΙΜΩΝ" : ""),
            "ΑΦΜ-ΔΟΥ " + afm,
            "Διεύθυνση (Οδός, Αριθμός, ΤΚ, Πόλη) " + address,
            "Ημ/νία Σύστασης ΝΠ & Αρ. Πράξεως, Δημοσίευση",
            "Είδος Δικαιώματος " + right + " Ποσοστό Δικαιώματος " + share,
            "Ιδιότητα στην Εγγραπτέα Πράξη " + role,
            "Παρατηρήσεις / Σχόλια " + comment,
        };

    private static readonly string[] TitlesTail =
    {
        "ΤΙΤΛΟΙ ΚΤΗΣΕΩΣ",
        "ΑΑ ΕΙΔΟΣ / ΑΡΙΘΜΟΣ & ΗΜ/ΝΙΑ ΕΓΓΡΑΦΟΥ / ΕΚΔΟΥΣΑ ΑΡΧΗ / ΣΤΟΙΧΕΙΑ ΜΕΤΑΓΡΑΦΗΣ - ΚΑΤΑΧΩΡΙΣΗΣ",
        "1) Σ.Ε. / ΓΟΝΙΚΗ ΠΑΡΟΧΗ / 13042 / 28/04/1986 / ΣΥΜΒΟΛΑΙΟΓΡΑΦΟΣ [ΔΟΚΙΜΑΣΤΗΣ - ΔΟΚΙΜΩΝ] / τ. 261 & α.α. 1",
        "2) Σ.Ε. / ΑΓΟΡΑΠΩΛΗΣΙΑ / 11589 / 26/10/1984 / ΣΥΜΒΟΛΑΙΟΓΡΑΦΟΣ [ΔΟΚΙΜΑΣΤΗΣ - ΔΟΚΙΜΩΝ] / τ. 255 & α.α. 2",
        "ΛΟΙΠΑ ΣΤΟΙΧΕΙΑ ΕΓΓΡΑΠΤΕΑΣ ΠΡΑΞΗΣ",
        "Συνημμένα:",
    };

    private static List<PdfTextLine> SaleSummary() => Lines(SaleHead
        .Concat(PersonBlock("ΕΛΕΝΗ", "ΑΓΟΡΑΣΤΟΠΟΥΛΟΥ", "ΝΙΚΟΛΑΟΣ", PersonTax, "ΟΔΟΣ ΔΟΚΙΜΗΣ, 5, 10000, ΑΘΗΝΑ", "ΚΥΡΙΟΤΗΤΑ-ΠΛΗΡΗΣ", "100 / 100", "ΑΠΟΚΤΩΝ", "ΑΓΟΡΑΣΤΡΙΑ"))
        .Concat(PersonBlock("ΓΕΩΡΓΙΟΣ", "ΠΩΛΗΤΟΠΟΥΛΟΣ", "ΚΩΝΣΤΑΝΤΙΝΟΣ", "100000002", ", , 99000, ΔΟΚΙΜΟΧΩΡΙ", "ΚΥΡΙΟΤΗΤΑ-ΠΛΗΡΗΣ", "100 / 100", "ΜΕΤΑΒΙΒΑΖΩΝ / ΠΑΡΑΧΩΡΩΝ", "ΠΩΛΗΤΗΣ"))
        .Concat(TitlesTail).ToArray());

    // ---- the paste box, the allow-list, the pace ----------------------------------------------------------------------

    [Fact]
    public void Pasted_KAEKs_split_on_commas_semicolons_spaces_and_new_lines_and_only_12_digits_count()
    {
        var parsed = KaekList.Parse("990000000001, 990000000002;990000000003\n990000000004\t 99000000000 990000000001 abc 990000000005/0/1 9900000000061");

        Assert.Equal(new[] { "990000000001", "990000000002", "990000000003", "990000000004" }, parsed.Kaeks);
        Assert.Equal(4, parsed.Ignored);   // 11 digits, letters, a unit, 13 digits
        Assert.Equal(1, parsed.Duplicates);
    }

    [Theory]
    [InlineData("POST", "/Professionals/Inquiry/Main/FetchEstates", true)]
    [InlineData("GET", "/Professionals/Inquiry/Main/viewResults/990000000001-0-1?p=1", false)]
    [InlineData("GET", "/Professionals/Inquiry/Main/viewResults/990000000001-0-1", false)]
    [InlineData("GET", "/Professionals/Inquiry/Main/showScans?KAEK=990000000001-0-1&item=a2&pid=77001", false)]
    [InlineData("GET", "/Professionals/Inquiry/Main/showScans?item=f1&pid=77001&KAEK=990000000001-0-0", false)]
    [InlineData("GET", "/Professionals/Inquiry/Main/SearchByEstate", false)]
    public void The_four_allowed_requests_pass(string method, string path, bool withSearchBody)
    {
        CadastreAllowList.Check(method, path, withSearchBody ? CadastreAllowList.FetchEstatesBody(Kaek) : null);
    }

    [Theory]
    [InlineData("POST", "/Professionals/Contracts/Main/SubmitFile")]              // a filing: a payable "Έντυπο Οφειλής"
    [InlineData("GET", "/Professionals/Contracts/Home/Folders")]
    [InlineData("GET", "/professionals/CONTRACTS/x")]
    [InlineData("GET", "/Professionals/Account/Logout")]
    [InlineData("POST", "/Professionals/Inquiry/Main/ShowDocs")]                // the extracts (certificates)
    [InlineData("GET", "/Professionals/Inquiry/Main/ShowDocs?ShowKF=990000000001-0-0")]
    [InlineData("GET", "/Professionals/Inquiry/Main/SearchByPerson")]           // person search: not in V1
    [InlineData("POST", "/Professionals/Inquiry/Main/FetchPersons")]
    [InlineData("GET", "/Professionals/Inquiry/Main/SearchByAct")]
    [InlineData("GET", "/Professionals/Inquiry/Main/FetchEstates")]             // the search, but not as the form sends it
    [InlineData("POST", "/Professionals/Inquiry/Main/viewResults/990000000001-0-1")]
    [InlineData("GET", "/Professionals/Inquiry/Main/viewResults/99000000000-0-1")]
    [InlineData("GET", "/Professionals/Inquiry/Main/viewResults/990000000001-0-1?p=2")]
    [InlineData("GET", "/Professionals/Inquiry/Main/showScans?KAEK=990000000001-0-1&item=a2&pid=77001&full=1")]
    [InlineData("GET", "/Professionals/Inquiry/Main/showScans?KAEK=990000000001-0-1&item=a2")]
    [InlineData("GET", "https://example.com/Professionals/Inquiry/Main/SearchByEstate")]
    [InlineData("GET", "/Professionals/Inquiry/Main/../../Contracts/Main/SubmitFile")]
    [InlineData("DELETE", "/Professionals/Inquiry/Main/SearchByEstate")]
    public void Anything_else_is_refused_before_it_is_sent(string method, string path)
    {
        Assert.Throws<CadastreBlockedException>(() => CadastreAllowList.Check(method, path, method == "POST" ? CadastreAllowList.FetchEstatesBody(Kaek) : null));
    }

    [Fact]
    public void The_search_is_sent_only_with_the_forms_own_fields()
    {
        Assert.Throws<CadastreBlockedException>(() => CadastreAllowList.Check("POST", "/Professionals/Inquiry/Main/FetchEstates", null));
        Assert.Throws<CadastreBlockedException>(() => CadastreAllowList.Check("POST", "/Professionals/Inquiry/Main/FetchEstates",
            CadastreAllowList.FetchEstatesBody(Kaek).Replace("Street=", "Street=ΟΔΟΣ")));
        Assert.Throws<ArgumentException>(() => CadastreAllowList.FetchEstatesBody("12345"));
        Assert.Equal("KAEK=990000000001&PropVert=&PropHor=&Street=&StreetFrom=&StreetTo=&TK=&Locality=&OTA=&CadoffId=0&X-Requested-With=XMLHttpRequest",
            CadastreAllowList.FetchEstatesBody(Kaek));
    }

    [Fact]
    public void The_page_script_checks_the_same_allow_list_before_fetching()
    {
        var script = CadastreAllowList.RequestScript;
        var guard = script.IndexOf("if (!ok) blocked", StringComparison.Ordinal);
        Assert.True(guard > 0 && guard < script.IndexOf("await fetch(", StringComparison.Ordinal), "the allow-list must run before fetch");
        foreach (var piece in new[] { "'/contracts/'", "'/account/'", "'showdocs'", "fetchestates", "viewresults", "showscans", "searchbyestate", "ktimatologio\\.gov\\.gr" })
        {
            Assert.Contains(piece, script);
        }

        Assert.Equal(1, CountOf(script, "fetch(")); // the one place that sends
    }

    private static int CountOf(string text, string what) => (text.Length - text.Replace(what, "").Length) / what.Length;

    [Fact]
    public async Task One_request_every_5_to_15_seconds_at_random()
    {
        var now = new DateTime(2026, 11, 25, 10, 0, 0, DateTimeKind.Utc);
        var waits = new List<TimeSpan>();
        var randoms = new Queue<double>(new[] { 0.0, 1.0, 0.5 });
        var pacer = new RandomPacer(() => now, () => randoms.Dequeue(), (d, _) => { waits.Add(d); now += d; return Task.CompletedTask; });

        await pacer.WaitTurnAsync(default); // the first request goes at once
        pacer.Done();
        for (var i = 0; i < 3; i++)
        {
            await pacer.WaitTurnAsync(default);
            now += TimeSpan.FromSeconds(1); // the request itself takes a second
            pacer.Done();
        }

        Assert.Equal(new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(10) }, waits);
    }

    [Fact]
    public async Task Only_one_request_at_a_time()
    {
        var pacer = new RandomPacer(() => DateTime.UtcNow, () => 0, (_, _) => Task.CompletedTask);
        await pacer.WaitTurnAsync(default);
        var second = pacer.WaitTurnAsync(default);
        await Task.Delay(50);
        Assert.False(second.IsCompleted);
        pacer.Done();
        await second;
        pacer.Done();
    }

    // ---- company or person --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("ΔΟΚΙΜΑΣΤΙΚΗ Α.Ε.", true)]
    [InlineData("ΔΟΚΙΜΑΣΤΙΚΗ ΑΕ", true)]
    [InlineData("δοκιμαστική ανώνυμη εταιρεία", true)]          // accents and case ignored
    [InlineData("ΔΟΚΙΜΗ Ε.Π.Ε.", true)]
    [InlineData("ΔΟΚΙΜΗ ΙΚΕ", true)]
    [InlineData("ΕΛΛΗΝΙΚΟ ΔΗΜΟΣΙΟ", true)]
    [InlineData("ΔΗΜΟΣ ΔΟΚΙΜΩΝ", true)]
    [InlineData("ΙΕΡΑ ΜΟΝΗ ΔΟΚΙΜΗΣ", true)]
    [InlineData("ΙΕΡΟΣ ΝΑΟΣ ΑΓΙΟΥ ΔΟΚΙΜΟΥ", true)]
    [InlineData("ΙΔΡΥΜΑ ΔΟΚΙΜΩΝ (Ν.Π.Ι.Δ.)", true)]
    [InlineData("ΔΟΚΙΜΑΣΤΙΚΗ AE", true)]                           // Latin A and E
    [InlineData("ΔΗΜΟΣΘΕΝΟΥΣ", false)]                            // whole words only
    [InlineData("ΑΕΡΑΚΗΣ", false)]
    [InlineData("ΠΑΠΑΔΟΠΟΥΛΟΣ", false)]
    public void Entity_names_are_matched_on_whole_words(string name, bool entity)
    {
        Assert.Equal(entity, LegalEntityRule.LooksLikeEntity(name));
    }

    [Fact]
    public void Heirs_are_never_an_entity_and_unknown_owners_are_Unknown()
    {
        Assert.Equal(PartyKind.Person, LegalEntityRule.Decide("ΚΛΗΡΟΝΟΜΟΙ ΔΟΚΙΜΟΥ ΕΤΑΙΡΕΙΑ", noPersonNames: true, markedEntity: true));
        Assert.Equal(PartyKind.Unknown, LegalEntityRule.Decide("ΑΓΝΩΣΤΟΙ ΚΛΗΡΟΝΟΜΟΙ", noPersonNames: true, markedEntity: null));
        Assert.Equal(PartyKind.Unknown, LegalEntityRule.Decide("ΑΓΝΩΣΤΟΣ", noPersonNames: true, markedEntity: null));
        Assert.False(LegalEntityRule.HasNoPersonNames("ΚΛΗΡΟΝΟΜΟΙ ΔΟΚΙΜΟΥ", "", false));
        Assert.False(LegalEntityRule.HasNoPersonNames("ΑΓΝΩΣΤΟΣ", "", false));
    }

    [Fact]
    public void Both_signs_must_agree()
    {
        Assert.Equal(PartyKind.LegalEntity, LegalEntityRule.Decide("ΔΟΚΙΜΑΣΤΙΚΗ Α.Ε.", noPersonNames: true, markedEntity: null));   // names + entity word
        Assert.Equal(PartyKind.LegalEntity, LegalEntityRule.Decide("ΔΟΚΙΜΑΣΤΙΚΗ", noPersonNames: true, markedEntity: true));        // names + marked
        Assert.Equal(PartyKind.Person, LegalEntityRule.Decide("ΔΟΚΙΜΑΣΤΙΚΗ", noPersonNames: true, markedEntity: null));            // one sign only
        Assert.Equal(PartyKind.Person, LegalEntityRule.Decide("ΔΟΚΙΜΑΣΤΙΚΗ Α.Ε.", noPersonNames: false, markedEntity: true));      // a first name / father
    }

    // ---- the search result and the record page ------------------------------------------------------------------------

    [Fact]
    public void The_search_gives_every_unit_with_its_row_data()
    {
        var rows = CadastrePages.ParseEstates(EstatesFragment((0, 0, "", "1.200,50"), (0, 1, "1", "85,20")));

        Assert.Equal(2, rows.Count);
        var flat = rows[1];
        Assert.Equal((Kaek, 0, 1), (flat.Kaek, flat.UnitV, flat.UnitH));
        Assert.Equal("/Professionals/Inquiry/Main/viewResults/990000000001-0-1?p=1", flat.RecordPath);
        Assert.Equal("1", flat.FloorText);
        Assert.Equal(85.20m, flat.TitleAreaSqm);
        Assert.Equal("Α1", flat.TitleNumber);
        Assert.Equal("9/1000", flat.PlotShareText);
        Assert.Equal("ΟΔΟΣ ΔΟΚΙΜΗΣ 12, 10000 ΔΟΚΙΜΑΣΤΙΚΗ ΘΕΣΗ, ΔΗΜΟΣ ΔΟΚΙΜΩΝ", flat.Address);
        Assert.Equal(1200.50m, rows[0].TitleAreaSqm);
        Assert.Empty(CadastrePages.ParseEstates("<div id=\"GridEstates\"><p>Δεν βρέθηκαν αποτελέσματα</p></div>"));
    }

    [Fact]
    public void A_persons_tax_number_is_dropped_while_the_record_page_is_parsed()
    {
        var record = CadastrePages.ParseRecord(RecordPage(
            Owner(PersonTax, "ΔΟΚΙΜΟΠΟΥΛΟΣ", "ΑΛΕΞΑΝΔΡΟΣ", "ΠΕΤΡΟΣ", "1 / 2") +
            Owner("100000003", "ΔΟΚΙΜΟΠΟΥΛΟΥ", "", "ΠΕΤΡΟΣ", "1 / 4") +          // no first name, but a father: a person
            Owner(CompanyTax, "ΔΟΚΙΜΑΣΤΙΚΗ ΑΝΩΝΥΜΗ ΕΤΑΙΡΕΙΑ", "", "", "1 / 4") +
            Owner("", "ΑΓΝΩΣΤΟΣ", "", "", "1 / 1"), ""));

        Assert.True(record.HasOwnersTable);
        Assert.Equal(4, record.Owners.Count);
        Assert.Null(record.Owners[0].CandidateTaxId);
        Assert.Null(record.Owners[1].CandidateTaxId);
        Assert.Equal(CompanyTax, record.Owners[2].CandidateTaxId);
        Assert.Null(record.Owners[3].CandidateTaxId);
        Assert.Equal("ΔΟΚΙΜΟΠΟΥΛΟΣ ΑΛΕΞΑΝΔΡΟΣ", record.Owners[0].Name);
        Assert.Equal("ΔΟΚΙΜΑΣΤΙΚΗ ΑΝΩΝΥΜΗ ΕΤΑΙΡΕΙΑ", record.Owners[2].Name);
        Assert.DoesNotContain(record.Owners, o => o.ToString().Contains(PersonTax) || o.ToString().Contains("ΠΕΤΡΟΣ"));
    }

    [Fact]
    public void The_record_page_lists_its_documents_and_the_two_newest_summaries_are_read()
    {
        var record = CadastrePages.ParseRecord(RecordPage(Owner("", "ΔΟΚΙΜΟΣ", "ΝΙΚΟΣ", "ΓΙΑΝΝΗΣ", "1 / 1"),
            Doc("ΣΥΜΒΟΛΑΙΟΓΡΑΦΙΚΟ ΕΓΓΡΑΦΟ 9009 - 25-07-1898", "f1") +
            Doc("ΑΠΟΦΑΣΗ 40840Σ - 20-06-2006", "f2") +
            Doc("<strong>100/2023 ΔΟΚΙΜΩΝ (Έγγραφο)</strong>", "c1") +
            "<tr class=\"NormalRow\"><td style=\"padding-left:20px\">↳ 100/2023 ΔΟΚΙΜΩΝ (ΠΕΡΙΛΗΨΗ ΕΓΓΡΑΠΤΕΑΣ ΠΡΑΞΗΣ)</td><td><a class=\"Icon IconMore\" " +
              "href=\"/Professionals/Inquiry/Main/showScans?KAEK=990000000001-0-1&amp;item=a1&amp;pid=77001\"></a></td></tr>" +
            Doc("↳ 100/2023 ΔΟΚΙΜΩΝ (ΑΙΤΗΣΗ ΚΑΤΑΧΩΡΙΣΗΣ)", "a2") +
            Doc("↳ 31/2024 ΔΟΚΙΜΩΝ (ΠΕΡΙΛΗΨΗ ΕΓΓΡΑΠΤΕΑΣ ΠΡΑΞΗΣ)", "a3") +
            Doc("↳ 250/2024 ΔΟΚΙΜΩΝ (Περίληψη Εγγραπτέας Πράξης)", "a4") +
            Doc("↳ 250/2024 ΔΟΚΙΜΩΝ (Τοπογραφικό)", "t1")));

        Assert.Equal(8, record.Documents.Count);
        var summaries = CadastrePages.SummariesToRead(record, 2);
        Assert.Equal(new[] { "a4", "a3" }, summaries.Select(s => s.Item));
        Assert.Equal("ΔΟΚΙΜΩΝ", summaries[0].Office);

        var titles = CadastrePages.RecordTitles(record);
        Assert.Equal(2, titles.Count);
        Assert.All(titles, t => Assert.Equal(ActSource.RecordTitle, t.Source));
        Assert.Equal(new DateOnly(1898, 7, 25), titles[0].ActDate); // old dates are kept
        Assert.Equal("9009", titles[0].DeedNo);
        Assert.Equal("ΣΥΜΒΟΛΑΙΟΓΡΑΦΙΚΟ ΕΓΓΡΑΦΟ", titles[0].ActTypeText);
        Assert.Equal("40840Σ", titles[1].DeedNo);
    }

    // ---- summaries -----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_sale_summary_gives_the_act_the_unit_the_parties_and_the_earlier_titles()
    {
        var s = SummaryPdf.Parse(SaleSummary());

        Assert.True(s.Readable);
        Assert.Equal("4321", s.DeedNo);
        Assert.Equal(new DateOnly(2024, 3, 28), s.Date);
        var act = Assert.Single(s.Acts);
        Assert.Equal((ActType.Sale, 63430.03m, 21000m), (act.TypeCode, act.ObjectiveValue!.Value, act.TransactionValue!.Value));
        Assert.Equal(new[] { "990000000001/0/1" }, s.Units);

        Assert.Equal(2, s.Parties.Count);
        var buyer = s.Parties[0];
        Assert.Equal("ΑΓΟΡΑΣΤΟΠΟΥΛΟΥ ΕΛΕΝΗ", buyer.Name);
        Assert.Equal(PartyRole.Acquirer, buyer.Role);
        Assert.Equal("ΑΓΟΡΑΣΤΡΙΑ", buyer.CommentText);
        Assert.Equal("ΚΥΡΙΟΤΗΤΑ-ΠΛΗΡΗΣ", buyer.RightText);
        Assert.Equal("100 / 100", buyer.ShareText);
        Assert.Equal("ΟΔΟΣ ΔΟΚΙΜΗΣ, 5, 10000, ΑΘΗΝΑ", buyer.Address);
        Assert.Null(buyer.CandidateTaxId); // a person's ΑΦΜ never leaves the parser
        var seller = s.Parties[1];
        Assert.Equal(PartyRole.Transferor, seller.Role);
        Assert.Equal("99000, ΔΟΚΙΜΟΧΩΡΙ", seller.Address);
        Assert.Null(seller.CandidateTaxId);
        Assert.DoesNotContain(s.Parties, p => p.ToString().Contains("ΝΙΚΟΛΑΟΣ") || p.ToString().Contains("1970") || p.ToString().Contains("ΧΧ000000"));

        Assert.Equal(2, s.OldTitles.Count);
        Assert.Equal((ActType.ParentalGift, "13042", new DateOnly(1986, 4, 28)), (s.OldTitles[0].TypeCode, s.OldTitles[0].DeedNo, s.OldTitles[0].Date!.Value));
        Assert.Equal("Σ.Ε. / ΓΟΝΙΚΗ ΠΑΡΟΧΗ", s.OldTitles[0].TypeText);
        Assert.Equal(ActType.Sale, s.OldTitles[1].TypeCode);
    }

    [Fact]
    public void An_auction_and_its_highest_bidder()
    {
        var lines = Lines(SaleHead.Select(l => l.Replace("1) Αγοραπωλησία / 63.430,03 / 21000", "1) Περίληψη κατακυρωτικής έκθεσης / 300000,00 / 318000"))
            .Concat(PersonBlock("ΔΟΚΙΜΗ", "ΠΛΕΙΟΔΟΤΟΥ", "ΔΟΚΙΜΟΣ", "100000004", "ΟΔΟΣ ΔΟΚΙΜΗΣ, 9, 10009, ΑΘΗΝΑ", "ΚΥΡΙΟΤΗΤΑ-ΠΛΗΡΗΣ", "100 / 100", "ΑΠΟΚΤΩΝ", "ΥΠΕΡΘΕΜΑΤΙΣΤΡΙΑ"))
            .Concat(PersonBlock("ΔΟΚΙΜΗ", "ΟΦΕΙΛΕΤΟΥ", "ΔΟΚΙΜΟΣ", "100000005", "", "ΔΟΥΛΕΙΕΣ-ΕΠΙΚΑΡΠΙΑ", "100 / 100", "ΜΕΤΑΒΙΒΑΖΩΝ / ΠΑΡΑΧΩΡΩΝ", "ΟΦΕΙΛΕΤΙΔΑ"))
            .ToArray());

        var s = SummaryPdf.Parse(lines);

        var act = Assert.Single(s.Acts);
        Assert.Equal((ActType.Auction, 318000m), (act.TypeCode, act.TransactionValue!.Value));
        Assert.Equal("ΥΠΕΡΘΕΜΑΤΙΣΤΡΙΑ", s.Parties[0].CommentText);
        Assert.Null(s.Parties[1].Address);
        Assert.Empty(s.OldTitles);
    }

    /// <summary>A seizure by a company (marked ΝΟΜΙΚΟ ΠΡΟΣΩΠΟ) against a person; the right wraps over three lines.</summary>
    private static List<PdfTextLine> SeizureSummary()
    {
        var head = Lines(SaleHead.Select(l => l
            .Replace("1) Αγοραπωλησία / 63.430,03 / 21000", "1) Αναγκαστική κατάσχεση / / 20000")
            .Replace("ΗΜΕΡΟΜΗΝΙΑ: 28/03/2024", "ΗΜΕΡΟΜΗΝΙΑ: 05/11/2026")).ToArray());
        var p = 2;
        var lines = new List<PdfTextLine>(head)
        {
            new(p, 524, 38, "ΦΥΣΙΚΟ ΠΡΟΣΩΠΟ ΝΟΜΙΚΟ ΠΡΟΣΩΠΟ"),
            new(p, 504, 40, "Όνομα / Είδος Νομικού Προσώπου ΝΟΜΙΚΟ ΠΡΟΣΩΠΟ ΙΔΙΩΤΙΚΟΥ ΔΙΚΑΙΟΥ"),
            new(p, 488, 40, "Επώνυμο / Επωνυμία ΔΟΚΙΜΑΣΤΙΚΕΣ ΥΠΟΔΟΜΕΣ"),
            new(p, 468, 40, "Όνομα Πατρός"),
            new(p, 446, 40, "Επώνυμο Πατρός"),
            new(p, 338, 40, "ΑΦΜ-ΔΟΥ " + CompanyTax),
            new(p, 322, 40, "Διεύθυνση (Οδός, Αριθμός, ΤΚ, Πόλη) ΟΔΟΣ ΕΤΑΙΡΕΙΑΣ, 1, 10001, ΑΘΗΝΑ"),
            new(p, 306, 40, "Ημ/νία Σύστασης ΝΠ & Αρ. Πράξεως, Δημοσίευση 01/02/2003, , 01/01/0001"),
            new(p, 288, 169, "ΚΑΤΑΣΧΕΣΕΙΣ-"),
            new(p, 278, 38, "Είδος Δικαιώματος ΑΝΑΓΚΑΣΤΙΚΗ Ποσοστό Δικαιώματος"),
            new(p, 268, 169, "ΚΑΤΑΣΧΕΣΗ"),
            new(p, 252, 38, "Ιδιότητα στην Εγγραπτέα Πράξη ΚΑΤΑΣΧΩΝ/ΕΠΙΣΠΕΥΔΩΝ/ΥΠΕΡ'ΟΥ"),
            new(p, 236, 38, "Παρατηρήσεις / Σχόλια"),
            new(p, 200, 38, "ΦΥΣΙΚΟ ΠΡΟΣΩΠΟ ΝΟΜΙΚΟ ΠΡΟΣΩΠΟ"),
            new(3, 792, 40, "Όνομα / Είδος Νομικού Προσώπου ΔΟΚΙΜΗ"),
            new(3, 776, 40, "Επώνυμο / Επωνυμία ΟΦΕΙΛΕΤΟΥ"),
            new(3, 760, 40, "Όνομα Πατρός ΔΟΚΙΜΟΣ"),
            new(3, 662, 40, "ΑΦΜ-ΔΟΥ " + PersonTax),
            new(3, 646, 40, "Διεύθυνση (Οδός, Αριθμός, ΤΚ, Πόλη) ΟΔΟΣ ΔΟΚΙΜΗΣ, 2, 10002, ΑΘΗΝΑ"),
            new(3, 606, 169, "ΚΑΤΑΣΧΕΣΕΙΣ-"),
            new(3, 596, 38, "Είδος Δικαιώματος ΑΝΑΓΚΑΣΤΙΚΗ Ποσοστό Δικαιώματος"),
            new(3, 586, 169, "ΚΑΤΑΣΧΕΣΗ"),
            new(3, 570, 38, "Ιδιότητα στην Εγγραπτέα Πράξη ΚΑΘ' ΟΥ"),
            new(3, 554, 38, "Παρατηρήσεις / Σχόλια 100% ΠΛΗΡΗΣ ΚΥΡΙΟΤΗΤΑ"),
            // The previous titles: each cell's text is centred on its number.
            new(3, 506, 38, "ΤΙΤΛΟΙ ΚΤΗΣΕΩΣ"),
            new(3, 490, 38, "ΑΑ ΕΙΔΟΣ / ΑΡΙΘΜΟΣ & ΗΜ/ΝΙΑ ΕΓΓΡΑΦΟΥ / ΕΚΔΟΥΣΑ ΑΡΧΗ / ΣΤΟΙΧΕΙΑ ΜΕΤΑΓΡΑΦΗΣ - ΚΑΤΑΧΩΡΙΣΗΣ"),
            new(3, 474, 63, "ΣΥΜΒΟΛΑΙΟ / 501 / 03/03/1993 / ΣΥΜΒΟΛΑΙΟΓΡΑΦΟΣ ΔΟΚΙΜΩΝ / ΤΟΜΟΣ"),
            new(3, 468, 38, "1)"),
            new(3, 464, 63, "1111 & ΑΥΞΩΝ ΑΡΙΘΜΟΣ 11"),
            new(3, 448, 63, "ΣΥΜΒΟΛΑΙΟ / 502 / 04/04/1994 / ΣΥΜΒΟΛΑΙΟΓΡΑΦΟΣ ΔΟΚΙΜΩΝ / ΤΟΜΟΣ"),
            new(3, 442, 38, "2)"),
            new(3, 438, 63, "2222 & ΑΥΞΩΝ ΑΡΙΘΜΟΣ 22"),
            new(4, 778, 38, "ΛΟΙΠΑ ΣΤΟΙΧΕΙΑ ΕΓΓΡΑΠΤΕΑΣ ΠΡΑΞΗΣ"),
        };
        return lines;
    }

    [Fact]
    public void A_seizure_by_a_company_keeps_the_companys_tax_number_only()
    {
        var s = SummaryPdf.Parse(SeizureSummary());

        var act = Assert.Single(s.Acts);
        Assert.Equal((ActType.Seizure, (decimal?)null, 20000m), (act.TypeCode, act.ObjectiveValue, act.TransactionValue!.Value));
        Assert.Equal(new DateOnly(2026, 11, 5), s.Date);

        var company = s.Parties[0];
        Assert.Equal("ΔΟΚΙΜΑΣΤΙΚΕΣ ΥΠΟΔΟΜΕΣ", company.Name);          // the company name alone
        Assert.Equal("ΝΟΜΙΚΟ ΠΡΟΣΩΠΟ ΙΔΙΩΤΙΚΟΥ ΔΙΚΑΙΟΥ", company.EntityTypeText);
        Assert.True(company.MarkedEntity && company.NoPersonNames);
        Assert.Equal(CompanyTax, company.CandidateTaxId);              // marked + no person's names: both signs
        Assert.Equal("ΚΑΤΑΣΧΕΣΕΙΣ-ΑΝΑΓΚΑΣΤΙΚΗ ΚΑΤΑΣΧΕΣΗ", company.RightText); // the wrapped value, joined
        Assert.Equal(PartyRole.Other, company.Role);
        Assert.Equal("ΟΔΟΣ ΕΤΑΙΡΕΙΑΣ, 1, 10001, ΑΘΗΝΑ", company.Address);

        var debtor = s.Parties[1];
        Assert.Equal("ΟΦΕΙΛΕΤΟΥ ΔΟΚΙΜΗ", debtor.Name);
        Assert.Null(debtor.CandidateTaxId);
        Assert.Equal("ΚΑΘ' ΟΥ", debtor.RoleText);

        Assert.Equal(2, s.OldTitles.Count);
        Assert.Equal(("ΣΥΜΒΟΛΑΙΟ", "501", new DateOnly(1993, 3, 3)), (s.OldTitles[0].TypeText, s.OldTitles[0].DeedNo, s.OldTitles[0].Date!.Value));
        Assert.Equal("502", s.OldTitles[1].DeedNo);
    }

    [Fact]
    public void A_ticked_natural_person_box_wins_over_an_entity_looking_name()
    {
        var lines = Lines(SaleHead.Concat(new[]
        {
            "ΦΥΣΙΚΟ ΠΡΟΣΩΠΟ X ΝΟΜΙΚΟ ΠΡΟΣΩΠΟ",
            "Όνομα / Είδος Νομικού Προσώπου",
            "Επώνυμο / Επωνυμία ΜΟΝΗ ΔΟΚΙΜΗ",
            "Όνομα Πατρός",
            "ΑΦΜ-ΔΟΥ " + PersonTax,
            "Ιδιότητα στην Εγγραπτέα Πράξη ΑΠΟΚΤΩΝ",
        }).ToArray());

        var party = Assert.Single(SummaryPdf.Parse(lines).Parties);
        Assert.Null(party.CandidateTaxId);
        Assert.False(party.MarkedEntity);
    }

    [Fact]
    public void A_scan_without_the_summary_heading_needs_OCR()
    {
        var scan = Lines("HEPIAHTO KATAXQPISHS EITPAnTEAS IIP ASH", "1) rrOIXEIA EITPAcDOY", "E1AOS ErrPAWY:", "KAEK 0 5 0 0 9 7 5 0 8 0 0 5");
        Assert.False(SummaryPdf.Parse(scan).Readable);
        Assert.False(SummaryPdf.Read(Encoding.ASCII.GetBytes("%PDF-1.4 not really a pdf")).Readable);
    }

    [Fact]
    public void The_watermark_line_is_ignored_and_a_summary_without_an_act_line_counts_as_unreadable()
    {
        var withWatermark = SaleSummary();
        withWatermark.Insert(9, new PdfTextLine(1, 500, 86, "ΕΝΗΜΕΡΩΤΙΚΟ ΥΛΙΚΟ"));
        Assert.Single(SummaryPdf.Parse(withWatermark).Acts);

        var noAct = Lines(SaleHead.Where(l => !l.StartsWith("1) Αγορα", StringComparison.Ordinal)).ToArray());
        Assert.False(SummaryPdf.Parse(noAct).Readable);
    }

    [Fact]
    public void A_summary_covering_two_units_lists_both()
    {
        var lines = SaleSummary();
        lines.Insert(11, new PdfTextLine(1, 600, 38, "ΚΑΕΚ 990000000001 / 0 / 14"));
        Assert.Equal(new[] { "990000000001/0/1", "990000000001/0/14" }, SummaryPdf.Parse(lines).Units);
    }

    [Fact]
    public void Numbers_and_dates_as_the_Cadastre_writes_them()
    {
        Assert.Equal(63430.03m, CadastreText.Number("63430,03"));
        Assert.Equal(63430.03m, CadastreText.Number("63.430,03"));
        Assert.Equal(1250000m, CadastreText.Number("1.250.000"));
        Assert.Null(CadastreText.Number(""));
        Assert.Null(CadastreText.Date("01/01/0001"));
        Assert.Equal(new DateOnly(1898, 7, 25), CadastreText.Date("25-07-1898"));
        Assert.Null(CadastreText.Date("31/02/2024"));
        Assert.Equal("099999990", CadastreText.TaxDigits("099999990 ΔΟΥ ΔΟΚΙΜΩΝ"));
        Assert.Null(CadastreText.TaxDigits("ΔΟΥ"));
    }

    /// <summary>A real PDF (made here) through PdfPig: lines rebuilt from letters, 48 pt watermark letters left out.</summary>
    [Fact]
    public void A_summary_PDF_is_read_from_its_text_layer()
    {
        var fontPath = new[] { @"C:\Windows\Fonts\arial.ttf", "/System/Library/Fonts/Supplemental/Arial.ttf", "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf" }
            .FirstOrDefault(File.Exists);
        if (fontPath is null) { return; } // no font with Greek letters on this machine

        var pdf = SummaryPdfBuilder(File.ReadAllBytes(fontPath));
        var lines = SummaryPdf.ReadLines(pdf);
        Assert.Contains(lines, l => l.Text == "ΠΕΡΙΛΗΨΗ ΚΑΤΑΧΩΡΙΣΗΣ ΕΓΓΡΑΠΤΕΑΣ ΠΡΑΞΗΣ");
        Assert.Contains(lines, l => l.Text == "Επώνυμο / Επωνυμία ΑΓΟΡΑΣΤΟΠΟΥΛΟΥ"); // label and value: one line, watermark letter gone
        Assert.DoesNotContain(lines, l => l.Text.Contains('Ω') && l.Text.Length == 1);

        var s = SummaryPdf.Read(pdf);
        Assert.True(s.Readable);
        Assert.Equal(21000m, Assert.Single(s.Acts).TransactionValue);
        Assert.Equal(new[] { "990000000001/0/1" }, s.Units);
        Assert.Equal("ΑΓΟΡΑΣΤΟΠΟΥΛΟΥ ΕΛΕΝΗ", Assert.Single(s.Parties).Name);
    }

    private static byte[] SummaryPdfBuilder(byte[] font)
    {
        var b = new PdfDocumentBuilder();
        var f = b.AddTrueTypeFont(font);
        var page = b.AddPage(595, 842);
        var y = 794.0;
        void Line(double x, string text, double? value = null, string? valueText = null)
        {
            page.AddText(text, 10, new PdfPoint(x, y), f);
            if (valueText is not null) page.AddText(valueText, 10, new PdfPoint(value ?? 260, y), f);
            y -= 16;
        }

        Line(159, "ΠΕΡΙΛΗΨΗ ΚΑΤΑΧΩΡΙΣΗΣ ΕΓΓΡΑΠΤΕΑΣ ΠΡΑΞΗΣ");
        Line(38, "ΗΜΕΡΟΜΗΝΙΑ: 28/03/2024", 300, "ΟΝΟΜ/ΜΟ: ΔΟΚΙΜΑΣΤΗ ΜΑΡΙΑ / ΑΘΗΝΑ");
        Line(38, "2) ΕΙΔΟΣ ΕΓΓΡΑΠΤΕΑΣ ΠΡΑΞΗΣ / ΑΝΤΙΚΕΙΜΕΝΙΚΗ ΑΞΙΑ / ΑΞΙΑ ΣΥΝΑΛΛΑΓΗΣ");
        Line(38, "1) Αγοραπωλησία / 63430,03 / 21000");
        Line(36, "ΧΩΡΙΚΗ ΜΕΤΑΒΟΛΗ");
        Line(38, "ΚΑΕΚ", 120, "990000000001 / 0 / 1");
        Line(36, "ΣΤΟΙΧΕΙΑ ΠΡΟΣΩΠΩΝ");
        Line(38, "ΦΥΣΙΚΟ ΠΡΟΣΩΠΟ", 300, "ΝΟΜΙΚΟ ΠΡΟΣΩΠΟ");
        Line(40, "Όνομα / Είδος Νομικού Προσώπου", 260, "ΕΛΕΝΗ");
        var surnameY = y;
        Line(40, "Επώνυμο / Επωνυμία", 260, "ΑΓΟΡΑΣΤΟΠΟΥΛΟΥ");
        Line(40, "Όνομα Πατρός", 260, "ΔΟΚΙΜΟΣ");
        Line(40, "ΑΦΜ-ΔΟΥ", 260, PersonTax);
        Line(38, "Ιδιότητα στην Εγγραπτέα Πράξη", 260, "ΑΠΟΚΤΩΝ");
        // The watermark: big letters across the page, one of them on the surname's line.
        page.AddText("Ω", 48, new PdfPoint(200, surnameY), f);
        page.AddText("Ε", 48, new PdfPoint(420, 300), f);
        return b.Build();
    }

    // ---- one KAEK through the pipeline (fake site, real allow-list and checks) ----------------------------------------

    private sealed class FakeTransport : ICadastreTransport
    {
        public readonly Dictionary<string, SiteResponse> Replies = new();
        public readonly List<string> Sent = new();

        public Task<SiteResponse> SendAsync(string method, string path, string? body, bool expectPdf)
        {
            Sent.Add(method + " " + path);
            return Task.FromResult(Replies.TryGetValue(method + " " + path, out var r) ? r
                : new SiteResponse(404, "https://ktimatologio.gov.gr" + path, false, "text/html", "<html>not found</html>", null));
        }

        public void Html(string method, string path, string html) =>
            Replies[method + " " + path] = new SiteResponse(200, "https://ktimatologio.gov.gr" + path.Split('?')[0], false, "text/html; charset=utf-8", html, null);

        public void Pdf(string path, string marker) =>
            Replies["GET " + path] = new SiteResponse(200, "https://ktimatologio.gov.gr" + path, false, "application/.pdf", null, Encoding.ASCII.GetBytes("%PDF-" + marker));
    }

    private sealed class CountingPacer : IRequestPacer
    {
        public int Turns;
        public Task WaitTurnAsync(CancellationToken ct) { Turns++; return Task.CompletedTask; }
        public void Done() { }
    }

    private const string FetchEstates = "POST /Professionals/Inquiry/Main/FetchEstates";

    private static string Scans(string item, int h = 1) => $"/Professionals/Inquiry/Main/showScans?KAEK={Kaek}-0-{h}&item={item}&pid=77001";

    /// <summary>A KAEK with the plot and one flat; the flat has a sale summary, an older one, and a third that is a scan.</summary>
    private static (FakeTransport Site, Func<byte[], SummaryData> Reader) Building()
    {
        var site = new FakeTransport();
        site.Replies[FetchEstates] = new SiteResponse(200, "https://ktimatologio.gov.gr/Professionals/Inquiry/Main/FetchEstates", false, "text/html",
            EstatesFragment((0, 0, "", "1.200,50"), (0, 1, "1", "85,20")), null);
        site.Html("GET", $"/Professionals/Inquiry/Main/viewResults/{Kaek}-0-0?p=1",
            RecordPage(Owner(CompanyTax, "ΔΟΚΙΜΑΣΤΙΚΗ ΑΝΩΝΥΜΗ ΕΤΑΙΡΕΙΑ", "", "", "1 / 1"), Doc("ΣΥΜΒΟΛΑΙΟΓΡΑΦΙΚΟ ΕΓΓΡΑΦΟ 9009 - 25-07-1898", "f1", 0)));
        site.Html("GET", $"/Professionals/Inquiry/Main/viewResults/{Kaek}-0-1?p=1",
            RecordPage(Owner(PersonTax, "ΑΓΟΡΑΣΤΟΠΟΥΛΟΥ", "ΕΛΕΝΗ", "ΝΙΚΟΛΑΟΣ", "1 / 1"),
                Doc("↳ 31/2024 ΔΟΚΙΜΩΝ (ΠΕΡΙΛΗΨΗ ΕΓΓΡΑΠΤΕΑΣ ΠΡΑΞΗΣ)", "a3") +
                Doc("↳ 9/2023 ΔΟΚΙΜΩΝ (ΠΕΡΙΛΗΨΗ ΕΓΓΡΑΠΤΕΑΣ ΠΡΑΞΗΣ)", "a1") +
                Doc("↳ 2/2021 ΔΟΚΙΜΩΝ (ΠΕΡΙΛΗΨΗ ΕΓΓΡΑΠΤΕΑΣ ΠΡΑΞΗΣ)", "a0")));
        site.Pdf(Scans("a3"), "sale");
        site.Pdf(Scans("a1"), "scan");
        site.Pdf(Scans("a0"), "never-read");
        SummaryData Reader(byte[] bytes) => Encoding.ASCII.GetString(bytes) switch
        {
            "%PDF-sale" => SummaryPdf.Parse(SaleSummary()),
            "%PDF-scan" => SummaryData.Unreadable,
            var other => throw new InvalidOperationException("unexpected PDF " + other),
        };
        return (site, Reader);
    }

    [Fact]
    public async Task One_KAEK_gives_each_units_owners_and_acts_with_the_AFM_rule_applied()
    {
        var (transport, reader) = Building();
        var pacer = new CountingPacer();
        var record = await new CadastreFetcher(new GuardedCadastreSite(transport, pacer), readSummary: reader).FetchAsync(Kaek, default);

        Assert.Equal(new[]
        {
            FetchEstates,
            $"GET /Professionals/Inquiry/Main/viewResults/{Kaek}-0-0?p=1",
            $"GET /Professionals/Inquiry/Main/viewResults/{Kaek}-0-1?p=1",
            "GET " + Scans("a3"), "GET " + Scans("a1"), // the two newest summaries only
        }, transport.Sent);
        Assert.Equal(5, pacer.Turns); // every request waited for its turn

        Assert.Equal(CadastreStatus.Found, record.Status);
        Assert.Equal(2, record.Units.Count);
        var plot = record.Units[0];
        var owner = Assert.Single(plot.Owners);
        Assert.Equal(("LegalEntity", CompanyTax), (owner.Kind, owner.EntityTaxId));
        Assert.Equal(1200.50m, plot.TitleAreaSqm);
        Assert.Equal(ActSource.RecordTitle, Assert.Single(plot.Acts).Source);

        var flat = record.Units[1];
        Assert.Equal(CadastreStatus.NeedsOcr, flat.Status); // one of its summaries is a scan
        var person = Assert.Single(flat.Owners);
        Assert.Equal(("Person", (string?)null), (person.Kind, person.EntityTaxId));
        Assert.Equal("ΟΔΟΣ ΔΟΚΙΜΗΣ, 5, 10000, ΑΘΗΝΑ", person.Address); // the buyer in the summary is this owner
        var sale = Assert.Single(flat.Acts, a => a.Source == ActSource.Summary);
        Assert.Equal(("31", 2024, "ΔΟΚΙΜΩΝ", ActType.Sale, 21000m), (sale.RegNo, sale.RegYear!.Value, sale.Office, sale.ActTypeCode, sale.TransactionValue!.Value));
        Assert.Equal("990000000001/0/1", sale.CoversUnits);
        Assert.Equal(2, sale.Parties.Count);
        Assert.All(sale.Parties, p => Assert.Null(p.EntityTaxId));
        Assert.Equal(2, flat.Acts.Count(a => a.Source == ActSource.OldTitle));

        // Nothing a person's: no tax number, father, ID card or birth date anywhere in what is sent.
        var json = System.Text.Json.JsonSerializer.Serialize(record);
        foreach (var never in new[] { PersonTax, "100000002", "ΝΙΚΟΛΑΟΣ", "ΚΩΝΣΤΑΝΤΙΝΟΣ", "ΧΧ000000", "1970" })
        {
            Assert.DoesNotContain(never, json);
        }
    }

    [Fact]
    public void An_entity_on_the_record_page_that_a_summary_shows_as_a_person_loses_its_AFM()
    {
        var estate = new EstateRow(Kaek, 0, 1, "/x", null, null, null, null, null);
        var record = CadastrePages.ParseRecord(RecordPage(
            Owner(CompanyTax, "ΔΟΚΙΜΑΣΤΙΚΗ", "", "", "1 / 2") +              // no names, but no entity word and no summary mark
            Owner("090000010", "ΔΟΚΙΜΗ ΕΤΑΙΡΕΙΑ", "", "", "1 / 2"), ""));    // an entity word, but the summary shows a father
        var summary = SummaryPdf.Parse(Lines(SaleHead
            .Concat(PersonBlock("", "ΔΟΚΙΜΗ ΕΤΑΙΡΕΙΑ", "ΠΑΤΡΩΝΥΜΟ", "090000010", "", "ΚΥΡΙΟΤΗΤΑ-ΠΛΗΡΗΣ", "1 / 2", "ΑΠΟΚΤΩΝ", "ΑΓΟΡΑΣΤΗΣ"))
            .ToArray()));
        var doc = new RecordDocument("/d", "a1", "↳ 31/2024 ΔΟΚΙΜΩΝ (ΠΕΡΙΛΗΨΗ ΕΓΓΡΑΠΤΕΑΣ ΠΡΑΞΗΣ)", "31", 2024, "ΔΟΚΙΜΩΝ", "ΠΕΡΙΛΗΨΗ ΕΓΓΡΑΠΤΕΑΣ ΠΡΑΞΗΣ");
        Assert.Equal("090000010", CadastreFetcher.BuildUnit(estate, CadastrePages.ParseRecord(RecordPage(Owner("090000010", "ΔΟΚΙΜΗ ΕΤΑΙΡΕΙΑ", "", "", "1 / 2"), "")),
            Array.Empty<(RecordDocument, SummaryData)>()).Owners[0].EntityTaxId); // without the summary it would be kept

        var unit = CadastreFetcher.BuildUnit(estate, record, new[] { (doc, summary) });

        Assert.All(unit.Owners, o => Assert.Equal(("Person", (string?)null), (o.Kind, o.EntityTaxId)));
        Assert.All(unit.Acts.SelectMany(a => a.Parties), p => Assert.Null(p.EntityTaxId));
    }

    [Fact]
    public void A_record_page_entity_confirmed_by_a_summary_mark_keeps_its_AFM_and_type()
    {
        var estate = new EstateRow(Kaek, 0, 3, "/x", null, null, null, null, null);
        var record = CadastrePages.ParseRecord(RecordPage(Owner(CompanyTax, "ΔΟΚΙΜΑΣΤΙΚΕΣ ΥΠΟΔΟΜΕΣ", "", "", "1 / 1"), ""));
        var doc = new RecordDocument("/d", "a1", "↳ 777/2026 ΔΟΚΙΜΩΝ (ΠΕΡΙΛΗΨΗ ΕΓΓΡΑΠΤΕΑΣ ΠΡΑΞΗΣ)", "777", 2026, "ΔΟΚΙΜΩΝ", "ΠΕΡΙΛΗΨΗ ΕΓΓΡΑΠΤΕΑΣ ΠΡΑΞΗΣ");

        var unit = CadastreFetcher.BuildUnit(estate, record, new[] { (doc, SummaryPdf.Parse(SeizureSummary())) });

        var owner = Assert.Single(unit.Owners);
        Assert.Equal(("LegalEntity", CompanyTax, "ΝΟΜΙΚΟ ΠΡΟΣΩΠΟ ΙΔΙΩΤΙΚΟΥ ΔΙΚΑΙΟΥ"), (owner.Kind, owner.EntityTaxId, owner.EntityTypeText));
        Assert.Equal("ΟΔΟΣ ΕΤΑΙΡΕΙΑΣ, 1, 10001, ΑΘΗΝΑ", owner.Address);
        var seizure = Assert.Single(unit.Acts, a => a.Source == ActSource.Summary);
        Assert.Equal(ActType.Seizure, seizure.ActTypeCode);
        Assert.Equal(CompanyTax, seizure.Parties[0].EntityTaxId);
        Assert.Null(seizure.Parties[1].EntityTaxId);
    }

    [Fact]
    public async Task No_rows_means_the_Cadastre_is_not_operating_there()
    {
        var transport = new FakeTransport();
        transport.Replies[FetchEstates] = new SiteResponse(200, "https://ktimatologio.gov.gr/Professionals/Inquiry/Main/FetchEstates", false, "text/html",
            "<table class=\"Grid\"><tr class=\"HeaderRow\"><th>ΚΑΕΚ</th></tr></table>", null);

        var record = await new CadastreFetcher(new GuardedCadastreSite(transport, new CountingPacer())).FetchAsync(Kaek, default);

        Assert.Equal((CadastreStatus.NotOperating, 0), (record.Status, record.Units.Count));
        Assert.Single(transport.Sent);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("signin")]
    [InlineData("redirect")]
    [InlineData("html-not-pdf")]
    public async Task Any_sign_of_trouble_stops(string trouble)
    {
        var (transport, reader) = Building();
        var record = $"GET /Professionals/Inquiry/Main/viewResults/{Kaek}-0-1?p=1";
        switch (trouble)
        {
            case "status":
                transport.Replies[record] = transport.Replies[record] with { Status = 500 };
                break;
            case "signin":
                transport.Html("GET", $"/Professionals/Inquiry/Main/viewResults/{Kaek}-0-1?p=1",
                    "<html><body><form><input name=\"u\"><input type=\"password\" name=\"p\"></form></body></html>");
                break;
            case "redirect":
                transport.Replies[record] = transport.Replies[record] with { Redirected = true, Url = "https://ktimatologio.gov.gr/Professionals/Account/Login?ReturnUrl=x" };
                break;
            default:
                transport.Replies["GET " + Scans("a3")] = new SiteResponse(200, "https://ktimatologio.gov.gr/x", false, "text/html", "<html>Σφάλμα</html>", Encoding.UTF8.GetBytes("<html>"));
                break;
        }

        await Assert.ThrowsAsync<CadastreTroubleException>(() =>
            new CadastreFetcher(new GuardedCadastreSite(transport, new CountingPacer()), readSummary: reader).FetchAsync(Kaek, default));
    }

    [Fact]
    public async Task A_link_off_the_allow_list_is_never_sent()
    {
        var (transport, reader) = Building();
        transport.Html("GET", $"/Professionals/Inquiry/Main/viewResults/{Kaek}-0-1?p=1",
            RecordPage(Owner("", "ΔΟΚΙΜΟΣ", "ΝΙΚΟΣ", "ΓΙΑΝΝΗΣ", "1 / 1"),
                $"<tr class=\"NormalRow\"><td>↳ 31/2024 ΔΟΚΙΜΩΝ (ΠΕΡΙΛΗΨΗ ΕΓΓΡΑΠΤΕΑΣ ΠΡΑΞΗΣ)</td><td><a href=\"/Professionals/Contracts/showScans?KAEK={Kaek}-0-1&amp;item=a3&amp;pid=1\"></a></td></tr>"));

        await Assert.ThrowsAsync<CadastreBlockedException>(() =>
            new CadastreFetcher(new GuardedCadastreSite(transport, new CountingPacer()), readSummary: reader).FetchAsync(Kaek, default));
        Assert.DoesNotContain(transport.Sent, s => s.Contains("Contracts", StringComparison.OrdinalIgnoreCase));
    }

    // ---- a run ---------------------------------------------------------------------------------------------------------

    private sealed class Sink : IRecordsSink
    {
        public readonly List<CadastreRecordDto> Saved = new();
        public string? Refuse;
        public Task<string?> SaveAsync(CadastreRecordDto record, CancellationToken ct)
        {
            if (Refuse is not null && record.Status != CadastreStatus.Error) return Task.FromResult<string?>(Refuse);
            Saved.Add(record);
            return Task.FromResult<string?>(null);
        }
    }

    private sealed class Panel : IRecordsPanel
    {
        public readonly List<string> Lines = new();
        public string? Finished;
        public Task StatusAsync(string text) { Lines.Add("status: " + text); return Task.CompletedTask; }
        public Task LogAsync(string text, string kind = "") { Lines.Add(kind + ": " + text); return Task.CompletedTask; }
        public Task ProgressAsync(RecordsProgress progress) => Task.CompletedTask;
        public Task FinishedAsync(string text) { Finished = text; return Task.CompletedTask; }
    }

    private static string TempFolder() => Path.Combine(Path.GetTempPath(), "nadlan-records-test-" + Guid.NewGuid().ToString("N")[..8]);

    [Fact]
    public async Task A_run_saves_each_KAEK_and_stops_at_the_first_sign_of_trouble()
    {
        var (transport, reader) = Building();
        const string second = "990000000002";
        var sink = new Sink();
        var panel = new Panel();
        var folder = TempFolder();
        var calls = 0;
        var site = new GuardedCadastreSite(new DelegatingTransport(transport, (m, p) =>
        {
            // The second KAEK's search: the session has ended.
            if (p.EndsWith("FetchEstates", StringComparison.Ordinal) && ++calls == 2)
                return new SiteResponse(302, "https://ktimatologio.gov.gr/Professionals/Account/Login", true, "text/html", "<html><input type=password></html>", null);
            return null;
        }), new CountingPacer());

        var summary = await new RecordsRun(site, sink, panel, TextWriter.Null, folder, reader).RunAsync(new[] { Kaek, second, "990000000003" }, default);

        Assert.StartsWith("Stopped after 1 of 3", summary);
        Assert.Equal(2, sink.Saved.Count);
        Assert.Equal(CadastreStatus.Found, sink.Saved[0].Status);
        Assert.Equal((second, CadastreStatus.Error), (sink.Saved[1].Kaek, sink.Saved[1].Status)); // the failed KAEK is retried by an area run
        Assert.DoesNotContain(transport.Sent, s => s.Contains("990000000003"));
        var log = Directory.GetFiles(folder).Single();
        var text = await File.ReadAllTextAsync(log);
        Assert.Contains("\"990000000001\",Found", text);
        Assert.DoesNotContain("ΑΓΟΡΑΣΤΟΠΟΥΛΟΥ", text); // the local log has no names
        Directory.Delete(folder, true);
    }

    [Fact]
    public async Task A_run_refuses_more_than_300_KAEKs_and_stops_when_GreekPlot_refuses_a_record()
    {
        var (transport, reader) = Building();
        var sink = new Sink { Refuse = "HTTP 400: An ΑΦΜ came with a Person" };
        var run = new RecordsRun(new GuardedCadastreSite(transport, new CountingPacer()), sink, new Panel(), TextWriter.Null, TempFolder(), reader);

        await Assert.ThrowsAsync<ArgumentException>(() => run.RunAsync(Enumerable.Range(0, 301).Select(i => (990000000000L + i).ToString()).ToList(), default));
        var summary = await run.RunAsync(new[] { Kaek, "990000000002" }, default);
        Assert.Contains("GreekPlot did not save the record", summary);
        Assert.Single(transport.Sent, s => s.StartsWith("POST", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stop_ends_the_run_between_requests()
    {
        var (transport, reader) = Building();
        using var cts = new CancellationTokenSource();
        var site = new GuardedCadastreSite(new DelegatingTransport(transport, (_, _) => { cts.Cancel(); return null; }), new CountingPacer());
        var sink = new Sink();

        var summary = await new RecordsRun(site, sink, new Panel(), TextWriter.Null, TempFolder(), reader).RunAsync(new[] { Kaek, "990000000002" }, cts.Token);

        Assert.StartsWith("Stopped:", summary);
        Assert.Single(transport.Sent); // the request in flight finished; nothing after it
        Assert.Empty(sink.Saved);
    }

    private sealed class DelegatingTransport(FakeTransport inner, Func<string, string, SiteResponse?> before) : ICadastreTransport
    {
        public Task<SiteResponse> SendAsync(string method, string path, string? body, bool expectPdf)
        {
            var r = before(method, path);
            if (r is not null) { inner.Sent.Add(method + " " + path); return Task.FromResult(r); }
            return inner.SendAsync(method, path, body, expectPdf);
        }
    }
}
