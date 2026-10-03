using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Harness.Server;

/// <summary>Which listener a request arrived on. Each Kestrel listener tags its connections when they open.</summary>
internal static class Listener
{
    public const string Socket = "socket", Api = "api", Webhooks = "webhooks";
    private const string Key = "harness.listener";

    public static ListenOptions Tag(this ListenOptions options, string kind)
    {
        options.Use(next => async connection =>
        {
            connection.Items[Key] = kind;
            await next(connection);
        });
        return options;
    }

    public static string? Kind(HttpContext http) =>
        http.Features.Get<IConnectionItemsFeature>()?.Items.TryGetValue(Key, out object? kind) == true ? kind as string : null;

    public static bool IsLocalSocket(HttpContext http) => Kind(http) == Socket;
}
