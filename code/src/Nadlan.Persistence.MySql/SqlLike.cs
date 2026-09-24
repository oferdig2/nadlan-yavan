namespace Nadlan.Persistence.MySql;

internal static class SqlLike
{
    /// <summary>Escapes LIKE wildcards so user text is matched literally (MySQL's default escape char is '\').</summary>
    public static string Escape(string text)
        => text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
