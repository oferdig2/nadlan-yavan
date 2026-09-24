using Nadlan.Config.MySql;
using Nadlan.Host.Assets;
using Nadlan.Host.Composition;
using Nadlan.Host.Configuration;
using Nadlan.Host.Contacts;
using Nadlan.Host.Errors;
using Nadlan.Host.Files;
using Nadlan.Host.Parcels;
using Nadlan.Host.Portfolios;
using Nadlan.Host.Reference;
using Nadlan.Persistence.MySql;

var builder = WebApplication.CreateBuilder(args);

// Bootstrap: the connection string is the only secret. The schema is owned by Nadlan.DbTool (update-db.ps1);
// the app only refuses to start against an out-of-date schema. Then DB-backed config, env vars last so they win.
var db = MySqlDatabase.FromEnvironment();
await new SchemaMigrator(db).EnsureUpToDateAsync();
await MySqlAppConfigLoader.AddDbBackedConfigAsync(builder.Configuration, db.ConnectionString,
    new AppConfigLoaderOptions(MsKey: "host", MsRootSectionName: NadlanOptions.SectionName, SeedCommonWhenMissing: true));
builder.Configuration.AddEnvironmentVariables();

builder.Services.Configure<NadlanOptions>(builder.Configuration.GetSection(NadlanOptions.SectionName));
builder.Services.AddNadlanPersistence(db);
builder.Services.AddNadlanServices();
builder.Services.AddNadlanFileStorage(builder.Configuration);

var app = builder.Build();

app.UseMiddleware<ApiErrorMiddleware>();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapClientConfigEndpoints();
app.MapReferenceEndpoints();
app.MapParcelEndpoints();
app.MapAssetEndpoints();
app.MapContactEndpoints();
app.MapPortfolioEndpoints();
app.MapFileEndpoints();

app.Run();
