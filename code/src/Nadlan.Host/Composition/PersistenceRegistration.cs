using Nadlan.Core.Activity;
using Nadlan.Core.Assets;
using Nadlan.Core.Contacts;
using Nadlan.Core.Files;
using Nadlan.Core.GeographicAreas;
using Nadlan.Core.Parcels;
using Nadlan.Core.Portfolios;
using Nadlan.Core.Reference;
using Nadlan.Core.Security;
using Nadlan.Persistence.MySql;
using Nadlan.Persistence.MySql.Activity;
using Nadlan.Persistence.MySql.Assets;
using Nadlan.Persistence.MySql.Contacts;
using Nadlan.Persistence.MySql.Files;
using Nadlan.Persistence.MySql.GeographicAreas;
using Nadlan.Persistence.MySql.Parcels;
using Nadlan.Persistence.MySql.Portfolios;
using Nadlan.Persistence.MySql.Reference;
using Nadlan.Persistence.MySql.Security;

namespace Nadlan.Host.Composition;

public static class PersistenceRegistration
{
    public static IServiceCollection AddNadlanPersistence(this IServiceCollection services, MySqlDatabase db)
    {
        services.AddSingleton(db);
        services.AddSingleton<IParcelStore, MySqlParcelStore>();
        services.AddSingleton<Nadlan.Core.Editing.IEditVersionStore, Nadlan.Persistence.MySql.Editing.MySqlEditVersionStore>();
        services.AddSingleton<IGeographicAreaStore, MySqlGeographicAreaStore>();
        services.AddSingleton<ICountryStore, MySqlCountryStore>();
        services.AddSingleton<IAssetStore, MySqlAssetStore>();
        services.AddSingleton<IContactStore, MySqlContactStore>();
        services.AddSingleton<IPortfolioStore, MySqlPortfolioStore>();
        services.AddSingleton<IReferenceDataStore, MySqlReferenceDataStore>();
        services.AddSingleton<IFileAttachmentStore, MySqlFileAttachmentStore>();
        services.AddSingleton<IFileTargetResolver, MySqlFileTargetResolver>();
        services.AddSingleton<MySqlActivityLog>();
        services.AddSingleton<IActivityLog>(sp => new Nadlan.Host.Activity.BestEffortActivityLog(
            sp.GetRequiredService<MySqlActivityLog>(), sp.GetRequiredService<ILogger<Nadlan.Host.Activity.BestEffortActivityLog>>(),
            sp.GetRequiredService<IHttpContextAccessor>()));
        services.AddSingleton<IParcelLegalOwnerStore, MySqlParcelLegalOwnerStore>();
        services.AddSingleton<IAssetContactStore, MySqlAssetContactStore>();
        services.AddSingleton<IReferenceAdminStore, MySqlReferenceAdminStore>();
        services.AddSingleton<IUserStore, MySqlUserStore>();
        services.AddSingleton<IRoleStore, MySqlRoleStore>();
        services.AddSingleton<IResourceAccessStore, MySqlResourceAccessStore>();
        services.AddSingleton<IPasswordTokenStore, MySqlPasswordTokenStore>();
        services.AddSingleton<IAccessStore, MySqlAccessStore>();
        services.AddSingleton<IApiTokenStore, MySqlApiTokenStore>();
        return services;
    }

    public static IServiceCollection AddNadlanServices(this IServiceCollection services)
    {
        services.AddSingleton<ParcelService>();
        services.AddSingleton<AssetService>();
        services.AddSingleton<ContactService>();
        services.AddSingleton<PortfolioService>();
        services.AddSingleton<LegalOwnerService>();
        services.AddSingleton<AssetContactService>();
        services.AddSingleton<ReferenceAdminService>();
        services.AddSingleton<AccessPolicy>();
        services.AddSingleton<ParcelDeletionService>();
        services.AddSingleton<Nadlan.Host.Parcels.ParcelCoverageService>();
        services.AddHostedService(sp => sp.GetRequiredService<Nadlan.Host.Parcels.ParcelCoverageService>());
        services.AddSingleton<AuthService>();
        services.AddSingleton<UserAdminService>();
        return services;
    }
}
