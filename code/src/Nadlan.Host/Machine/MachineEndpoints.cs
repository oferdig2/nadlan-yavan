using Nadlan.Core.Security;
using Nadlan.Core.Validation;
using Nadlan.Host.Health;

namespace Nadlan.Host.Machine;

/// <summary>
/// The server pages (admin.html tabs Server, Web files, Downloads, Settings) for the Admins in <see cref="MachineAdminAccess"/>:
/// workload and restarts, the web files with hot patches, the importer downloads, and the app_config rows.
/// </summary>
public static class MachineEndpoints
{
    public sealed record TextPatchDto(string? Path, string? Text);

    public sealed record ConfigSaveDto(string? Json, long UpdatedUtcMs);

    public static IServiceCollection AddMachineAdmin(this IServiceCollection services)
    {
        services.AddSingleton<RequestCounter>();
        services.AddSingleton<MachineMonitor>();
        services.AddHostedService(sp => sp.GetRequiredService<MachineMonitor>());
        services.AddSingleton<ServiceControl>();
        services.AddSingleton<WebFiles>();
        services.AddSingleton<ConfigAdmin>();
        services.AddSingleton<ImporterDownloads>();
        return services;
    }

    public static void MapMachineEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapMachineGroup();
        var startedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // ------------------------------------------------------------ Server: workload, services, restarts

        group.MapGet("/status", async (MachineMonitor monitor, ServiceControl control, IWebHostEnvironment env, CancellationToken ct) =>
        {
            var (latest, history) = await monitor.GetAsync(ct);
            double? Mb(double? total, double? free) => total is { } t && free is { } f ? t - f : null;
            return Results.Ok(new
            {
                host = Environment.MachineName,
                os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                cores = Environment.ProcessorCount,
                release = HealthEndpoints.Version,
                environment = env.EnvironmentName,
                appStartedUtcMs = startedUtcMs,
                sampleSeconds = (int)MachineMonitor.Interval.TotalSeconds,
                latest,
                control = control.GetState(),
                services = ServiceDescriptor.All.Select(s => new { s.Key, s.Title, s.Unit }),
                // Column-wise and rounded: an hour of history stays a few KB.
                history = new
                {
                    t = history.Select(h => h.CapturedUtcMs),
                    cpu = history.Select(h => R(h.Machine.CpuPercent)),
                    steal = history.Select(h => R(h.Machine.StealPercent)),
                    memUsedMb = history.Select(h => R(Mb(h.Machine.MemTotalMb, h.Machine.MemAvailableMb))),
                    swapUsedMb = history.Select(h => R(Mb(h.Machine.SwapTotalMb, h.Machine.SwapFreeMb))),
                    qps = history.Select(h => R(h.MySql.QueriesPerSecond)),
                    requestsPerMin = history.Select(h => R(h.App.RequestsPerMinute)),
                    services = ServiceDescriptor.All.ToDictionary(s => s.Key, s => new
                    {
                        cpu = history.Select(h => R(h.Services.FirstOrDefault(x => x.Key == s.Key)?.CpuPercent)),
                        memMb = history.Select(h => R(h.Services.FirstOrDefault(x => x.Key == s.Key) is { } x ? x.CgroupMemoryMb ?? x.MemoryMb : null)),
                    }),
                },
            });
        });

        group.MapGet("/mysql/tables", async (MachineMonitor monitor, CancellationToken ct) => Results.Ok(await monitor.MySql.ReadTablesAsync(ct)));

        group.MapPost("/services/{key}/restart", (string key, UserAccess me, ServiceControl control) =>
        {
            var service = ServiceDescriptor.All.FirstOrDefault(s => s.Key == key)
                          ?? throw new EntityNotFoundException("Service", key);
            control.RequestRestart(service, me.Email);
            return Results.Accepted(value: new { message = $"{service.Title} is restarting." });
        });

        // ------------------------------------------------------------ Web files: browse, download, hot patches

        group.MapGet("/files/roots", (WebFiles files) => Results.Ok(files.Describe()));

        group.MapGet("/files", (string? root, string? path, WebFiles files) => Results.Ok(files.List(root ?? WebFiles.WebRoot, path)));

        group.MapGet("/files/patches", (WebFiles files) => Results.Ok(files.ListPatches()));

        // Always a download (never rendered here), so a patched page can't run as this origin from this link.
        group.MapGet("/files/download", (string? root, string? path, WebFiles files) =>
        {
            var full = files.ResolveDownload(root ?? WebFiles.WebRoot, path);
            return Results.File(full, "application/octet-stream", Path.GetFileName(full));
        });

        group.MapGet("/files/text", (string? path, WebFiles files) =>
        {
            var (text, patched, modified) = files.ReadText(path);
            return Results.Ok(new { path, text, patched, modifiedUtcMs = modified });
        });

        group.MapPut("/files/text", async (TextPatchDto dto, UserAccess me, WebFiles files, CancellationToken ct) =>
        {
            var path = (dto.Path ?? "").Replace('\\', '/').Trim('/');
            var slash = path.LastIndexOf('/');
            using var content = new MemoryStream(new System.Text.UTF8Encoding(false).GetBytes(dto.Text ?? ""));
            return Results.Ok(await files.PatchAsync(slash < 0 ? "" : path[..slash], path[(slash + 1)..], content, me.Email, ct));
        });

        // multipart/form-data: "path" = target folder, "name" = optional target file name (Replace on a row), files.
        // Read by hand, not bound: the CSRF header check covers it like every other write.
        group.MapPost("/files/upload", async (HttpRequest request, UserAccess me, WebFiles files, CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
            {
                throw new DomainValidationException("UPLOAD_NOT_FORM", "Send the files as multipart/form-data.");
            }

            var form = await request.ReadFormAsync(ct);
            var targetName = form["name"].ToString();
            if (form.Files.Count == 0 || (form.Files.Count > 1 && targetName.Length > 0))
            {
                throw new DomainValidationException("UPLOAD_NO_FILE", "Choose a file to upload (one, when replacing a file).");
            }

            var results = new List<WebPatchResult>();
            foreach (var file in form.Files)
            {
                await using var stream = file.OpenReadStream();
                results.Add(await files.PatchAsync(form["path"].ToString(), targetName.Length > 0 ? targetName : file.FileName, stream, me.Email, ct));
            }

            return Results.Ok(results);
        });

        group.MapDelete("/files", async (string? path, UserAccess me, WebFiles files, CancellationToken ct) =>
            Results.Ok(new { releaseFileServed = await files.RevertAsync(path, me.Email, ct) }));

        // ------------------------------------------------------------ Downloads: the KAEK importer (polygon acquisition app)

        group.MapGet("/downloads", async (ImporterDownloads downloads, CancellationToken ct) => Results.Ok(await downloads.ListAsync(ct)));

        // A link on the page: the browser comes here and is sent on to a fresh 10-minute S3 link.
        group.MapGet("/downloads/{version}/{file}", async (string version, string file, UserAccess me, ImporterDownloads downloads, CancellationToken ct) =>
            Results.Redirect(await downloads.GetLinkAsync(version, file, me.Email, ct)));

        // ------------------------------------------------------------ Settings: app_config rows

        group.MapGet("/config", async (ConfigAdmin config, CancellationToken ct) =>
            Results.Ok(new { appStartedUtcMs = startedUtcMs, rows = await config.ListAsync(ct) }));

        group.MapGet("/config/{key}", async (string key, ConfigAdmin config, CancellationToken ct) => Results.Ok(await config.GetAsync(key, ct)));

        group.MapPut("/config/{key}", async (string key, ConfigSaveDto dto, UserAccess me, ConfigAdmin config, CancellationToken ct) =>
        {
            var (updated, changed) = await config.SaveAsync(key, dto.Json, dto.UpdatedUtcMs, me.Email, ct);
            return Results.Ok(new { updatedUtcMs = updated, changed });
        });
    }

    private static double? R(double? value) => value is { } v ? Math.Round(v, 1) : null;
}
