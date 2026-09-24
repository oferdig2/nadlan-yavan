using Nadlan.Import.Csv;
using Nadlan.Import.Legacy;
using Nadlan.Import.Parcels;
using Nadlan.Persistence.MySql;
using Nadlan.Persistence.MySql.GeographicAreas;
using Nadlan.Persistence.MySql.Import;
using Nadlan.Persistence.MySql.Parcels;

// Usage: Nadlan.Import parcels <Inventory-Grid view.csv> [--dry-run]
if (args.Length < 2 || args[0] != "parcels")
{
    Console.Error.WriteLine("Usage: Nadlan.Import parcels <path to Inventory-Grid view.csv> [--dry-run]");
    return 2;
}

var csvPath = Path.GetFullPath(args[1]);
var dryRun = args.Contains("--dry-run");

var rows = LegacyInventoryRow.ReadAll(CsvTable.Load(csvPath));
var plan = InventoryParcelPlanner.Plan(rows);

ParcelImportResult? result = null;
if (!dryRun)
{
    var db = MySqlDatabase.FromEnvironment();
    await new SchemaMigrator(db).EnsureUpToDateAsync();

    var importer = new InventoryParcelImporter(
        new MySqlParcelStore(db), new MySqlGeographicAreaStore(db), new MySqlCountryStore(db), new MySqlImportStore(db));
    result = await importer.ImportAsync(plan, csvPath);
}

var report = ParcelImportReport.Build(plan, result);
Console.WriteLine(report);

var reportDir = Path.Combine(AppContext.BaseDirectory, "import-reports");
Directory.CreateDirectory(reportDir);
var reportPath = Path.Combine(reportDir, $"parcels-{(dryRun ? "dryrun" : $"batch{result!.ImportBatchId}")}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.txt");
await File.WriteAllTextAsync(reportPath, report);
Console.WriteLine($"Report saved: {reportPath}");
return 0;
