using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Harness.Server;

/// <summary>
/// Serves the PWA from resources embedded in the binary. Pages get a strict content security policy: scripts, styles and
/// connections only from the daemon's own origin, no framing.
/// </summary>
internal static class WebApp
{
    private const string Csp = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; " +
        "manifest-src 'self'; worker-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8", [".js"] = "text/javascript; charset=utf-8", [".css"] = "text/css; charset=utf-8",
        [".svg"] = "image/svg+xml", [".webmanifest"] = "application/manifest+json",
    };

    private static readonly Lazy<Dictionary<string, byte[]>> Files = new(() =>
    {
        Assembly asm = typeof(WebApp).Assembly;
        Dictionary<string, byte[]> files = [];
        foreach (string name in asm.GetManifestResourceNames().Where(n => n.StartsWith("wwwroot/", StringComparison.Ordinal)))
        {
            using Stream s = asm.GetManifestResourceStream(name)!;
            using MemoryStream m = new();
            s.CopyTo(m);
            files["/" + name["wwwroot/".Length..].Replace('\\', '/')] = m.ToArray();
        }
        return files;
    });

    public static void Map(IEndpointRouteBuilder app)
    {
        // The setup link printed on first start opens the app, which reads its ?code=.
        app.MapGet("/", (HttpContext http) => Serve(http, "/index.html"));
        app.MapGet("/setup", (HttpContext http) => Serve(http, "/index.html"));
        app.MapGet("/{file}", (string file, HttpContext http) => Serve(http, "/" + file));
    }

    private static IResult Serve(HttpContext http, string path)
    {
        if (!Files.Value.TryGetValue(path, out byte[]? bytes)) return Results.NotFound();
        IHeaderDictionary headers = http.Response.Headers;
        headers.ContentSecurityPolicy = Csp;
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        // Always revalidate, so a daemon upgrade reaches the app; the service worker keeps the offline copy.
        headers.CacheControl = "no-cache";
        if (path == "/sw.js") headers["Service-Worker-Allowed"] = "/";
        return Results.Bytes(bytes, Types.GetValueOrDefault(Path.GetExtension(path), "application/octet-stream"));
    }
}
