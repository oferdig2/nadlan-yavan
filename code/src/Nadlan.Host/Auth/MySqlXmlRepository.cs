using System.Xml.Linq;
using Dapper;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Nadlan.Persistence.MySql;

namespace Nadlan.Host.Auth;

/// <summary>
/// Data Protection key ring in MySQL (table data_protection_key). The keys sign the session cookie; sharing them in the DB
/// lets every instance (EC2 / Fargate tasks) read each other's cookies and keeps users signed in across restarts.
/// </summary>
internal sealed class MySqlXmlRepository : IXmlRepository
{
    private readonly MySqlDatabase _db;

    public MySqlXmlRepository(MySqlDatabase db)
    {
        _db = db;
    }

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using var conn = new MySqlConnector.MySqlConnection(_db.ConnectionString);
        conn.Open();
        return conn.Query<string>("SELECT xml FROM data_protection_key ORDER BY created_utc").Select(XElement.Parse).ToList();
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        using var conn = new MySqlConnector.MySqlConnection(_db.ConnectionString);
        conn.Open();
        conn.Execute("""
            INSERT INTO data_protection_key (friendly_name, xml) VALUES (@friendlyName, @xml)
            ON DUPLICATE KEY UPDATE xml = VALUES(xml)
            """, new { friendlyName, xml = element.ToString(SaveOptions.DisableFormatting) });
    }
}
