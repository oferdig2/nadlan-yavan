using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;

namespace Nadlan.Host.Machine;

/// <summary>
/// Serves app.js.br (or .gz) for app.js when the browser accepts it. The publish step already writes these next to every
/// script, page and style (brotli at maximum level - smaller than nginx's on-the-fly gzip), and a hot patch writes its own
/// (WebFiles). Runs just before UseStaticFiles: it only points the request at the compressed copy and sets
/// Content-Encoding; the static file middleware does the rest (ETag, 304, ranges) as for any file.
/// </summary>
public static class PrecompressedStaticFiles
{
    public static IApplicationBuilder UsePrecompressedStaticFiles(this IApplicationBuilder app, IFileProvider files, WebFiles webFiles)
        => app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value;
            if (path is null || !(HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)))
            {
                await next();
                return;
            }

            // The compressed copies are never served on their own: without Content-Encoding they'd arrive as garbage.
            if (WebFiles.CompressedSuffix(path) is not null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if (WebFiles.CompressibleExtensions.Contains(Path.GetExtension(path)))
            {
                context.Response.Headers.Append(HeaderNames.Vary, HeaderNames.AcceptEncoding);
                var original = files.GetFileInfo(path);
                if (original is { Exists: true, IsDirectory: false, PhysicalPath: { } physical } && Choose(context, physical, webFiles) is { } choice)
                {
                    context.Request.Path = path + choice.Suffix;
                    context.Response.Headers.ContentEncoding = choice.Encoding;
                }
            }

            await next();
        });

    private static (string Encoding, string Suffix)? Choose(HttpContext context, string physicalPath, WebFiles webFiles)
    {
        var accepted = context.Request.GetTypedHeaders().AcceptEncoding;
        foreach (var (encoding, suffix) in new[] { ("br", ".br"), ("gzip", ".gz") })
        {
            if (accepted.Any(a => a.Value.Equals(encoding, StringComparison.OrdinalIgnoreCase) && (a.Quality ?? 1) > 0)
                && webFiles.ServedSibling(physicalPath, suffix))
            {
                return (encoding, suffix);
            }
        }

        return null;
    }

    /// <summary>Content type of app.js.br = that of app.js (the request was pointed at the compressed copy above).</summary>
    public sealed class ContentTypes : IContentTypeProvider
    {
        private readonly FileExtensionContentTypeProvider _inner = new();

        public bool TryGetContentType(string subpath, out string contentType)
            => _inner.TryGetContentType(WebFiles.CompressedSuffix(subpath) is { } s ? subpath[..^s.Length] : subpath, out contentType!);
    }
}
