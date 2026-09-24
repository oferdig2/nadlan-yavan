using Nadlan.Core.Assets;
using Nadlan.Core.Contacts;
using Nadlan.Core.Files;
using Nadlan.Core.GeographicAreas;
using Nadlan.Core.Parcels;
using Nadlan.Core.Portfolios;
using Nadlan.Core.Reference;
using Nadlan.Persistence.MySql;
using Nadlan.Persistence.MySql.Assets;
using Nadlan.Persistence.MySql.Contacts;
using Nadlan.Persistence.MySql.Files;
using Nadlan.Persistence.MySql.GeographicAreas;
using Nadlan.Persistence.MySql.Parcels;
using Nadlan.Persistence.MySql.Portfolios;
using Nadlan.Persistence.MySql.Reference;

namespace Nadlan.Host.Composition;

public static class PersistenceRegistration
{
    public static IServiceCollection AddNadlanPersistence(this IServiceCollection services, MySqlDatabase db)
    {
        services.AddSingleton(db);
        services.AddSingleton<IParcelStore, MySqlParcelStore>();
        services.AddSingleton<IGeographicAreaStore, MySqlGeographicAreaStore>();
        services.AddSingleton<ICountryStore, MySqlCountryStore>();
        services.AddSingleton<IAssetStore, MySqlAssetStore>();
        services.AddSingleton<IContactStore, MySqlContactStore>();
        services.AddSingleton<IPortfolioStore, MySqlPortfolioStore>();
        services.AddSingleton<IReferenceDataStore, MySqlReferenceDataStore>();
        services.AddSingleton<IFileAttachmentStore, MySqlFileAttachmentStore>();
        services.AddSingleton<IFileTargetResolver, MySqlFileTargetResolver>();
        return services;
    }

    public static IServiceCollection AddNadlanServices(this IServiceCollection services)
    {
        services.AddSingleton<ParcelService>();
        services.AddSingleton<AssetService>();
        services.AddSingleton<ContactService>();
        services.AddSingleton<PortfolioService>();
        return services;
    }
}
