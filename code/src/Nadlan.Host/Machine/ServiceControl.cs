using System.Collections.Concurrent;
using System.Text.Json;
using Nadlan.Core.Validation;

namespace Nadlan.Host.Machine;

public sealed record ControlResult(string Action, bool Ok, string? FinishedUtc, string? Message);

public sealed record ControlState(bool Available, string? Reason, IReadOnlyList<string> Pending, IReadOnlyList<ControlResult> LastResults);

/// <summary>
/// Restarts services on the server. The app runs as the unprivileged "nadlan" user with NoNewPrivileges, so it can't run
/// systemctl itself. It drops an empty file named after a fixed action ("restart-mysqld") into a drop folder; the root-owned
/// nadlan-control.path unit notices, and release/server/control.sh restarts that unit and writes the outcome to results/.
/// The helper acts only on the file NAMES it knows, never on file contents. Installed by deploy.sh.
/// </summary>
public sealed class ServiceControl
{
    public const string ControlDir = "/var/lib/nadlan-control";
    private static readonly string RequestsDir = Path.Combine(ControlDir, "requests");
    private static readonly string ResultsDir = Path.Combine(ControlDir, "results");
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastRequest = new();
    private readonly ILogger<ServiceControl> _log;

    public ServiceControl(ILogger<ServiceControl> log)
    {
        _log = log;
    }

    private static string ActionFor(ServiceDescriptor service) => $"restart-{service.Unit}";

    public ControlState GetState()
    {
        if (!OperatingSystem.IsLinux())
        {
            return new ControlState(false, "Restarts work on the Linux server only, not on a development PC.", [], []);
        }

        if (!Directory.Exists(RequestsDir))
        {
            return new ControlState(false, "The restart helper is not installed on this server yet; the next update (6-update-server.ps1) installs it.", [], []);
        }

        var pending = SafeFiles(RequestsDir).Select(Path.GetFileName).OfType<string>().ToList();
        var results = SafeFiles(ResultsDir).Where(f => f.EndsWith(".json", StringComparison.Ordinal)).Select(ReadResult).OfType<ControlResult>()
            .OrderByDescending(r => r.FinishedUtc, StringComparer.Ordinal).ToList();
        return new ControlState(true, null, pending, results);
    }

    /// <summary>Asks the helper to restart a service. Returns at once; the page follows the outcome via <see cref="GetState"/>.</summary>
    public void RequestRestart(ServiceDescriptor service, string requestedBy)
    {
        var state = GetState();
        if (!state.Available)
        {
            throw new DomainValidationException("RESTART_UNAVAILABLE", state.Reason!);
        }

        var action = ActionFor(service);
        var now = DateTimeOffset.UtcNow;
        if (_lastRequest.TryGetValue(action, out var last) && now - last < Cooldown)
        {
            throw new DomainValidationException("RESTART_TOO_SOON", $"{service.Title} was asked to restart moments ago. Wait half a minute.");
        }

        _lastRequest[action] = now;
        try
        {
            File.WriteAllBytes(Path.Combine(RequestsDir, action), Array.Empty<byte>());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _lastRequest.TryRemove(action, out _);
            throw new DomainValidationException("RESTART_UNAVAILABLE", $"Could not ask the restart helper: {ex.Message}");
        }

        _log.LogWarning("Server page: {User} asked to restart {Unit}", requestedBy, service.Unit);
    }

    private static IEnumerable<string> SafeFiles(string dir)
    {
        try
        {
            return Directory.Exists(dir) ? Directory.GetFiles(dir) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static ControlResult? ReadResult(string file)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            string? Text(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return new ControlResult(Text("action") ?? Path.GetFileNameWithoutExtension(file),
                root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True, Text("finishedUtc"), Text("message"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
