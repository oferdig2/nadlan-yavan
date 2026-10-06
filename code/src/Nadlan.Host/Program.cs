using Nadlan.Config.MySql;
using Nadlan.Host.Activity;
using Nadlan.Host.Admin;
using Nadlan.Host.Assets;
using Nadlan.Host.Auth;
using Nadlan.Host.Composition;
using Nadlan.Host.Configuration;
using Nadlan.Host.Contacts;
using Nadlan.Host.Errors;
using Nadlan.Host.Files;
using Nadlan.Host.Health;
using Nadlan.Host.Parcels;
using Nadlan.Host.Portfolios;
using Nadlan.Host.Presentation;
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
builder.Services.AddNadlanAuth(builder.Configuration, db);
// A request the endpoint can't bind (malformed JSON, "abc" for a number) throws, so ApiErrorMiddleware answers with the
// usual { error, message } instead of an empty 400.
builder.Services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);

var app = builder.Build();

app.UseMiddleware<ApiErrorMiddleware>();
app.UseNadlanAuth(); // default files, page guard, static files, authentication, authorization, CSRF, forced password change

app.MapHealthEndpoints();
app.MapAuthEndpoints();
app.MapUserAdminEndpoints();
app.MapImporterEndpoints();
app.MapClientConfigEndpoints();
app.MapReferenceEndpoints();
app.MapParcelEndpoints();
app.MapAssetEndpoints();
app.MapContactEndpoints();
app.MapPortfolioEndpoints();
app.MapFileEndpoints();
app.MapPresentationEndpoints();
app.MapActivityEndpoints();
app.MapAdminEndpoints();

app.Run();
