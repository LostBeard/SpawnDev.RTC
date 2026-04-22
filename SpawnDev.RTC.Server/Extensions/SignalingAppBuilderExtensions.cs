using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace SpawnDev.RTC.Server.Extensions;

/// <summary>
/// ASP.NET Core wire-up helpers for <see cref="TrackerSignalingServer"/>.
/// </summary>
public static class SignalingAppBuilderExtensions
{
    /// <summary>
    /// Map a WebRTC signaling endpoint at <paramref name="path"/> backed by
    /// a fresh <see cref="TrackerSignalingServer"/>. One-line consumer wire-up:
    /// <code>
    ///     app.UseWebSockets();
    ///     app.UseRtcSignaling("/announce");
    /// </code>
    /// </summary>
    /// <param name="app">The application builder (e.g. <c>WebApplication</c>).</param>
    /// <param name="path">URL path to listen on. WebTorrent convention is <c>/announce</c>.</param>
    /// <param name="options">Optional server tuning. See <see cref="TrackerServerOptions"/>.</param>
    /// <returns>The underlying <see cref="TrackerSignalingServer"/> so callers can
    /// query <see cref="TrackerSignalingServer.Rooms"/> / <see cref="TrackerSignalingServer.TotalPeers"/>
    /// for health checks and metrics.</returns>
    public static TrackerSignalingServer UseRtcSignaling(
        this IApplicationBuilder app,
        string path = "/announce",
        TrackerServerOptions? options = null)
    {
        var server = new TrackerSignalingServer(options);
        app.Map(path, branch => branch.Run(server.HandleWebSocketAsync));
        return server;
    }

    /// <summary>
    /// Map a WebRTC signaling endpoint at <paramref name="path"/> backed by an
    /// existing <see cref="TrackerSignalingServer"/>. Use when the consumer
    /// needs to construct the server themselves (for DI or per-request options).
    /// </summary>
    public static IApplicationBuilder UseRtcSignaling(
        this IApplicationBuilder app,
        string path,
        TrackerSignalingServer server)
    {
        app.Map(path, branch => branch.Run(server.HandleWebSocketAsync));
        return app;
    }
}
