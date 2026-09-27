using Nadlan.Config.MySql;
using Nadlan.Core.Activity;
using Nadlan.Core.Security;
using Nadlan.Persistence.MySql;
using Nadlan.Persistence.MySql.Activity;
using Nadlan.Persistence.MySql.Security;

// Nadlan DB tool - the only thing that changes the database structure. Called by update-db.ps1 / start-nadlan.ps1.
//
//   migrate                               create the schema if needed and apply pending Sql/NNN_*.sql scripts
//   reset <databaseName>                  DEV ONLY: drop the schema (all data) and rebuild it; name = confirmation
//   status                                show current vs latest schema version (read-only)
//   config list                           list app_config rows
//   config show <configKey>               print one row, e.g. ms:host
//   config set <configKey> <path> <value> set one value, e.g. ms:host Nadlan:Maps:GoogleApiKey AIza...  (--empty = "")
//   config remove <configKey> <path>      delete an obsolete key, e.g. ms:host Nadlan:Storage:KeyPrefix
//   user list                             list users (email, role, active, password yes/no)
//   user set-password <email>             set a password from env NADLAN_NEW_PASSWORD (user-password.ps1 prompts for it)

const string EmptyValueToken = "--empty";

const string Usage = """
    Usage:
      Nadlan.DbTool migrate
      Nadlan.DbTool reset <databaseName>
      Nadlan.DbTool status
      Nadlan.DbTool config list
      Nadlan.DbTool config show <configKey>
      Nadlan.DbTool config set <configKey> <path> <value>     (use --empty for an empty value)
      Nadlan.DbTool config remove <configKey> <path>
      Nadlan.DbTool user list
      Nadlan.DbTool user set-password <email>                (password in env NADLAN_NEW_PASSWORD)
    """;

try
{
    var db = MySqlDatabase.FromEnvironment();
    var migrator = new SchemaMigrator(db);

    switch (args)
    {
        case ["migrate"]:
        {
            var applied = await migrator.MigrateAsync();
            Console.WriteLine(applied.Count == 0
                ? $"Schema '{db.DatabaseName}' is up to date."
                : $"Schema '{db.DatabaseName}': applied {string.Join(", ", applied)}.");
            return 0;
        }

        case ["reset", var confirm]:
        {
            var applied = await migrator.ResetAsync(confirm);
            Console.WriteLine($"Schema '{db.DatabaseName}' dropped and rebuilt: {string.Join(", ", applied)}.");
            return 0;
        }

        case ["status"]:
        {
            var s = await migrator.GetStatusAsync();
            Console.WriteLine(!s.DatabaseExists
                ? $"Schema '{db.DatabaseName}' does not exist. Pending: {string.Join(", ", s.Pending)}"
                : $"Schema '{db.DatabaseName}': version {s.CurrentVersion} of {s.LatestVersion}" +
                  (s.Pending.Count == 0 ? " (up to date)" : $"; pending: {string.Join(", ", s.Pending)}"));
            return s.IsUpToDate ? 0 : 1;
        }

        case ["config", "list"]:
        {
            await migrator.EnsureUpToDateAsync();
            foreach (var (key, updated) in await new AppConfigMySqlStore(db.ConnectionString).ListAsync())
            {
                Console.WriteLine($"{key,-30} updated {DateTimeOffset.FromUnixTimeMilliseconds(updated).UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC");
            }

            return 0;
        }

        case ["config", "show", var key]:
        {
            await migrator.EnsureUpToDateAsync();
            var (found, json, _) = await new AppConfigMySqlStore(db.ConnectionString).TryGetAsync(key);
            Console.WriteLine(found ? json : $"No app_config row '{key}'.");
            return found ? 0 : 1;
        }

        case ["config", "set", var key, var path, var value]:
        {
            // Windows PowerShell drops "" when calling a program, so an empty value is spelled --empty.
            var actual = value == EmptyValueToken ? "" : value;
            await migrator.EnsureUpToDateAsync();
            var store = new AppConfigMySqlStore(db.ConnectionString);
            var (found, json, _) = await store.TryGetAsync(key);
            if (found && !AppConfigJson.IsValidObject(json))
            {
                // Same rule as the app: never replace a row we can't read, it would wipe every other setting.
                Console.Error.WriteLine($"ERROR: app_config row '{key}' is not valid JSON; fix it first (e.g. in MySQL Workbench). Nothing was changed.");
                return 1;
            }

            await store.UpsertAsync(key, AppConfigJson.SetValue(json, path, actual));
            Console.WriteLine($"Set {key} -> {path} = {(actual.Length == 0 ? "(empty)" : "(value)")}. Restart the app to pick it up.");
            return 0;
        }

        case ["config", "remove", var key, var path]:
        {
            await migrator.EnsureUpToDateAsync();
            var store = new AppConfigMySqlStore(db.ConnectionString);
            var (found, json, _) = await store.TryGetAsync(key);
            if (found && !AppConfigJson.IsValidObject(json))
            {
                Console.Error.WriteLine($"ERROR: app_config row '{key}' is not valid JSON; fix it first (e.g. in MySQL Workbench). Nothing was changed.");
                return 1;
            }

            var (updated, removed) = found ? AppConfigJson.RemoveValue(json, path) : (json, false);
            if (removed)
            {
                await store.UpsertAsync(key, updated);
            }

            Console.WriteLine(removed
                ? $"Removed {key} -> {path}. Restart the app. (If appsettings.json still has it, the default is added back.)"
                : $"{key} has no {path}.");
            return 0;
        }

        case ["user", "list"]:
        {
            await migrator.EnsureUpToDateAsync();
            foreach (var u in await new MySqlUserStore(db).SearchAsync(null, includeInactive: true, limit: 1000))
            {
                Console.WriteLine($"{u.Email,-40} {u.RoleCode,-14} {(u.IsActive ? "active" : "DISABLED"),-9} password: {(u.HasPassword ? "yes" : "no ")}  last login: {u.LastLoginUtc?.ToString("yyyy-MM-dd HH:mm") ?? "never"}");
            }

            return 0;
        }

        case ["user", "set-password", var email]:
        {
            // Bootstrap / lock-out rescue: works without Google or another Admin. The password comes from the environment
            // (user-password.ps1 prompts for it), never from the command line where it would end up in the shell history.
            await migrator.EnsureUpToDateAsync();
            var password = Environment.GetEnvironmentVariable("NADLAN_NEW_PASSWORD");
            var users = new MySqlUserStore(db);
            var user = await users.GetByEmailAsync(AuthService.NormalizeEmail(email));
            if (user is null)
            {
                Console.Error.WriteLine($"ERROR: no user {email}. Existing users: Nadlan.DbTool user list");
                return 1;
            }

            PasswordPolicy.Validate(password, user.Email);
            await users.SetPasswordAsync(user.UserId, PasswordHasher.Hash(password!), mustChangePassword: false);
            await new MySqlActivityLog(db).RecordAsync(new ActivityEntry("User", user.UserId, ActivityActions.PasswordSetByAdmin,
                "Password set with the DB tool (user-password.ps1)."));
            Console.WriteLine($"Password set for {user.Email} ({user.RoleName}{(user.IsActive ? "" : ", DISABLED")}). Other sessions were signed out; the account is unlocked.");
            return 0;
        }

        default:
            Console.Error.WriteLine(Usage);
            return 2;
    }
}
catch (Exception ex) when (ex is InvalidOperationException or MySqlConnector.MySqlException or Nadlan.Core.Validation.DomainValidationException)
{
    Console.Error.WriteLine($"ERROR: {ex.Message}");
    return 1;
}
