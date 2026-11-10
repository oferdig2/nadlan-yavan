using System.Net;
using MaxMind.GeoIP2;
using Microsoft.Extensions.Options;
using Nadlan.Core.Analytics;
using Nadlan.Host.Configuration;

namespace Nadlan.Host.Analytics;

/// <summary>
/// The place and network of an IP from MaxMind GeoLite2 files on this machine (Nadlan:Geo:CityDbPath / AsnDbPath) - one
/// in-memory lookup, no call to anyone. A file that is missing or unreadable gives no place (never an error). A file
/// replaced while the app runs (geoipupdate) is picked up within a minute: the new one is opened, the old one closed a
/// little later so lookups in progress finish.
/// </summary>
public sealed class MaxMindGeoLookup : IGeoLookup, IDisposable
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(1);

    private readonly Db _city;
    private readonly Db _asn;

    public MaxMindGeoLookup(IOptions<NadlanOptions> options, ILogger<MaxMindGeoLookup> log)
        : this(options.Value.Geo.CityDbPath, options.Value.Geo.AsnDbPath, log)
    {
    }

    public MaxMindGeoLookup(string? cityPath, string? asnPath, ILogger? log = null)
    {
        _city = new Db("City", cityPath, log);
        _asn = new Db("ASN", asnPath, log);
    }

    public bool HasCity => _city.Reader is not null;

    public bool HasAsn => _asn.Reader is not null;

    public GeoInfo? Lookup(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip) || !IPAddress.TryParse(ip.Trim(), out var address)
            || IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || IsPrivate(address))
        {
            return null;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        string? country = null, region = null, city = null, org = null;
        decimal? lat = null, lng = null;
        int? accuracy = null, asn = null;
        if (_city.Reader is { } c && c.TryCity(address, out var r) && r is not null)
        {
            country = Cut(r.Country.IsoCode ?? r.RegisteredCountry.IsoCode, 2);
            region = Cut(r.MostSpecificSubdivision.Name, 100);
            city = Cut(r.City.Name, 100);
            lat = r.Location.Latitude is double la ? Math.Round((decimal)la, 6) : null;
            lng = r.Location.Longitude is double lo ? Math.Round((decimal)lo, 6) : null;
            accuracy = r.Location.AccuracyRadius;
        }

        if (_asn.Reader is { } a && a.TryAsn(address, out var n) && n is not null)
        {
            asn = n.AutonomousSystemNumber is long number and <= int.MaxValue ? (int)number : null;
            org = Cut(n.AutonomousSystemOrganization, 200);
        }

        var geo = new GeoInfo(country, region, city, lat, lng, accuracy, asn, org);
        return geo.IsEmpty ? null : geo;
    }

    /// <summary>10/8, 172.16/12, 192.168/16, 100.64/10, fc00::/7: no place to find.</summary>
    internal static bool IsPrivate(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6)
        {
            a = a.MapToIPv4();
        }

        var b = a.GetAddressBytes();
        return a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) || b[0] == 0
            : (b[0] & 0xFE) == 0xFC;
    }

    private static string? Cut(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Length <= max ? s : s[..max];

    public void Dispose()
    {
        _city.Dispose();
        _asn.Dispose();
    }

    /// <summary>One database file: opened on first use, re-opened when the file changes.</summary>
    private sealed class Db : IDisposable
    {
        private readonly string _name;
        private readonly string? _path;
        private readonly ILogger? _log;
        private readonly object _gate = new();
        private DatabaseReader? _reader;
        private (DateTime Written, long Length)? _stamp;
        private DateTime _checkedUtc = DateTime.MinValue;
        private bool _warned;

        public Db(string name, string? path, ILogger? log)
        {
            _name = name;
            _path = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
            _log = log;
        }

        public DatabaseReader? Reader
        {
            get
            {
                if (_path is null)
                {
                    return null;
                }

                var now = DateTime.UtcNow;
                if (now - _checkedUtc >= CheckEvery)
                {
                    lock (_gate)
                    {
                        if (now - _checkedUtc >= CheckEvery)
                        {
                            _checkedUtc = now;
                            Refresh();
                        }
                    }
                }

                return _reader;
            }
        }

        private void Refresh()
        {
            try
            {
                var file = new FileInfo(_path!);
                if (!file.Exists)
                {
                    if (!_warned)
                    {
                        _log?.LogWarning("Analytics: the GeoLite2 {Name} database {Path} is missing; visits keep their IP without a place.", _name, _path);
                        _warned = true;
                    }

                    return; // keep what is open (a replace in progress), or nothing
                }

                var stamp = (file.LastWriteTimeUtc, file.Length);
                if (_reader is not null && _stamp == stamp)
                {
                    return;
                }

                // Read into memory (City ~50 MB + ASN ~9 MB): the file stays free to be replaced while the app runs. A mapped
                // file overwritten in place (cp) would crash the process on Linux.
                var fresh = new DatabaseReader(_path!, MaxMind.Db.FileAccessMode.Memory);
                var old = _reader;
                _reader = fresh;
                _stamp = stamp;
                _warned = false;
                _log?.LogInformation("Analytics: GeoLite2 {Name} database opened ({Path}, built {Built:yyyy-MM-dd}).", _name, _path, fresh.Metadata.BuildDate);
                if (old is not null)
                {
                    _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ => old.Dispose(), TaskScheduler.Default);
                }
            }
            catch (Exception ex) // unreadable, or half copied: keep the open one and try again in a minute
            {
                if (!_warned)
                {
                    _log?.LogWarning(ex, "Analytics: the GeoLite2 {Name} database {Path} can't be read; visits keep their IP without a place.", _name, _path);
                    _warned = true;
                }
            }
        }

        public void Dispose() => _reader?.Dispose();
    }
}
