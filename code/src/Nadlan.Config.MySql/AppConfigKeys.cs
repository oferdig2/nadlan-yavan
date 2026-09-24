namespace Nadlan.Config.MySql;

/// <summary>app_config keys, same scheme as Futuristic SaaS.</summary>
public static class AppConfigKeys
{
    /// <summary>Settings shared by every Nadlan process.</summary>
    public const string Common = "common:application";

    /// <summary>Settings for one process, e.g. ForMs("host") = "ms:host".</summary>
    public static string ForMs(string msKey) => $"ms:{msKey.Trim().ToLowerInvariant()}";
}
