using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace PlaywrightMultiTest
{
    public class StaticFileServer
    {
        WebApplication? app;
        Task? runningTask;
        string WWWRoot;
        string RequestPath;
        public string Url { get; private set; }
        string devcertPath;
        string SolutionRoot;
        public StaticFileServer(string wwwroot, string url, string requestPath = "")
        {
            if (string.IsNullOrEmpty(wwwroot))
            {
                throw new ArgumentNullException(nameof(wwwroot));
            }
            if (!Directory.Exists(wwwroot))
            {
                throw new DirectoryNotFoundException(wwwroot);
            }
            WWWRoot = Path.GetFullPath(wwwroot);
            RequestPath = requestPath;
            Url = url;
            devcertPath = Path.GetFullPath("assets/testcert.pfx");
            if (!File.Exists(devcertPath))
                throw new Exception("testcert.pfx not found. Cannot create static server");
            SolutionRoot = FindSolutionRoot();
        }

        private static string FindSolutionRoot()
        {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10; i++)
            {
                if (Directory.GetFiles(dir, "*.slnx").Length > 0 || Directory.GetFiles(dir, "*.sln").Length > 0)
                    return dir;
                var parent = Directory.GetParent(dir);
                if (parent == null) break;
                dir = parent.FullName;
            }
            return AppContext.BaseDirectory;
        }
        public bool Running => runningTask?.IsCompleted == false;
        public void Start()
        {
            runningTask ??= StartAsync();
        }
        private async Task StartAsync()
        {
            try
            {
                var builder = WebApplication.CreateBuilder();
                var port = new Uri(Url).Port;

                // This wipes out Console, Debug, and any other default providers
                builder.Logging.ClearProviders();

                // Configure static file serving
                builder.WebHost.UseKestrel();
                builder.WebHost.ConfigureKestrel(serverOptions =>
                {
                    serverOptions.Listen(IPAddress.Loopback, port, listenOptions =>
                    {
                        listenOptions.UseHttps(devcertPath, "unittests");
                    });
                });
                // Use the current directory as the web root
                builder.Environment.WebRootPath = WWWRoot;
                builder.WebHost.UseUrls(Url);

                app = builder.Build();

                // WebSocket signaling for cross-platform WebRTC tests
                app.UseWebSockets();
                app.Map("/signal/{roomId}", async (HttpContext context, string roomId) =>
                {
                    if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
                    var ws = await context.WebSockets.AcceptWebSocketAsync();
                    var room = SignalRooms.GetOrAdd(roomId, _ => new SignalRoom());
                    var peerId = Guid.NewGuid().ToString("N")[..8];
                    var peer = new SignalPeer(peerId, ws);
                    room.AddPeer(peer);
                    await room.BroadcastAsync(peerId, new { type = "peer-joined", peerId });
                    var buffer = new byte[64 * 1024];
                    try
                    {
                        await peer.SendAsync(new { type = "welcome", peerId, peers = room.GetPeerIds(peerId) });
                        while (ws.State == WebSocketState.Open)
                        {
                            var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
                            if (result.MessageType == WebSocketMessageType.Close) break;
                            var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                            var msg = JsonSerializer.Deserialize<JsonElement>(json);
                            if (!msg.TryGetProperty("type", out var typeEl)) continue;
                            var targetId = msg.TryGetProperty("targetId", out var tid) ? tid.GetString() : null;
                            var relay = new Dictionary<string, object?> { ["type"] = typeEl.GetString(), ["fromId"] = peerId };
                            foreach (var prop in msg.EnumerateObject())
                                if (prop.Name != "type" && prop.Name != "targetId") relay[prop.Name] = prop.Value;
                            if (targetId != null) await room.SendToPeerAsync(targetId, relay);
                            else await room.BroadcastAsync(peerId, relay);
                        }
                    }
                    catch (WebSocketException) { }
                    finally
                    {
                        room.RemovePeer(peerId);
                        await room.BroadcastAsync(peerId, new { type = "peer-left", peerId });
                        if (room.PeerCount == 0) SignalRooms.TryRemove(roomId, out _);
                    }
                });

                // WebTorrent tracker protocol endpoint (for RTCTrackerClient tests)
                app.Map("/announce", async (HttpContext context) =>
                {
                    if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
                    var ws = await context.WebSockets.AcceptWebSocketAsync();
                    var buffer = new byte[64 * 1024];
                    string? peerId = null;
                    try
                    {
                        while (ws.State == WebSocketState.Open)
                        {
                            var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
                            if (result.MessageType == WebSocketMessageType.Close) break;
                            var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                            var msg = JsonSerializer.Deserialize<JsonElement>(json);
                            if (!msg.TryGetProperty("action", out var actionEl) || actionEl.GetString() != "announce") continue;

                            var infoHash = msg.GetProperty("info_hash").GetString()!;
                            peerId = msg.GetProperty("peer_id").GetString()!;

                            // Handle offers: store them and relay to other peers in the swarm
                            if (msg.TryGetProperty("offers", out var offersEl) && offersEl.ValueKind == JsonValueKind.Array)
                            {
                                var swarm = TrackerSwarms.GetOrAdd(infoHash, _ => new TrackerSwarm());
                                swarm.AddPeer(peerId, ws);

                                // Relay each offer to a different existing peer
                                var otherPeers = swarm.GetOtherPeers(peerId);
                                int peerIdx = 0;
                                foreach (var offerItem in offersEl.EnumerateArray())
                                {
                                    if (peerIdx >= otherPeers.Length) break;
                                    var targetPeer = otherPeers[peerIdx++];
                                    var relay = JsonSerializer.Serialize(new
                                    {
                                        action = "announce",
                                        info_hash = infoHash,
                                        peer_id = peerId,
                                        offer = offerItem.GetProperty("offer"),
                                        offer_id = offerItem.GetProperty("offer_id").GetString(),
                                    }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                                    try { await targetPeer.Socket.SendAsync(Encoding.UTF8.GetBytes(relay), WebSocketMessageType.Text, true, CancellationToken.None); } catch { }
                                }
                            }
                            // Handle answer: relay to the target peer
                            else if (msg.TryGetProperty("answer", out var answerEl) && msg.TryGetProperty("to_peer_id", out var toPeerEl))
                            {
                                var swarm = TrackerSwarms.GetOrAdd(infoHash, _ => new TrackerSwarm());
                                var targetPeerId = toPeerEl.GetString()!;
                                var targetPeer = swarm.GetPeer(targetPeerId);
                                if (targetPeer != null)
                                {
                                    var relay = JsonSerializer.Serialize(new
                                    {
                                        action = "announce",
                                        info_hash = infoHash,
                                        peer_id = peerId,
                                        answer = answerEl,
                                        offer_id = msg.TryGetProperty("offer_id", out var oid) ? oid.GetString() : null,
                                    }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                                    try { await targetPeer.Socket.SendAsync(Encoding.UTF8.GetBytes(relay), WebSocketMessageType.Text, true, CancellationToken.None); } catch { }
                                }
                            }

                            // Send announce response
                            var response = JsonSerializer.Serialize(new { action = "announce", info_hash = infoHash, interval = 120, complete = 0, incomplete = 1 },
                                new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                            await ws.SendAsync(Encoding.UTF8.GetBytes(response), WebSocketMessageType.Text, true, CancellationToken.None);
                        }
                    }
                    catch (WebSocketException) { }
                    finally
                    {
                        if (peerId != null)
                        {
                            foreach (var swarm in TrackerSwarms.Values)
                                swarm.RemovePeer(peerId);
                        }
                    }
                });

                // (optional) add headers that enables: window.crossOriginIsolated == true
                app.Use(async (context, next) =>
                {
                    context.Response.Headers["Cross-Origin-Embedder-Policy"] = "credentialless";
                    context.Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
                    await next();
                });

                // Filesystem API: read/write files relative to solution root.
                // Blazor WASM apps running in PlaywrightMultiTest can use this to
                // write debug dumps, log files, and test artifacts to disk.
                app.Use(async (context, next) =>
                {
                    if (context.Request.Path.StartsWithSegments("/_fs", out var remaining) && remaining.HasValue)
                    {
                        var relativePath = remaining.Value.TrimStart('/');
                        if (string.IsNullOrEmpty(relativePath))
                        {
                            context.Response.StatusCode = 400;
                            await context.Response.WriteAsync("Path required");
                            return;
                        }
                        var fullPath = Path.GetFullPath(Path.Combine(SolutionRoot, relativePath));
                        if (!fullPath.StartsWith(SolutionRoot, StringComparison.OrdinalIgnoreCase))
                        {
                            context.Response.StatusCode = 403;
                            await context.Response.WriteAsync("Path outside solution root");
                            return;
                        }
                        if (context.Request.Method == "GET")
                        {
                            if (!File.Exists(fullPath))
                            {
                                context.Response.StatusCode = 404;
                                await context.Response.WriteAsync("Not found");
                                return;
                            }
                            context.Response.ContentType = "application/octet-stream";
                            await context.Response.SendFileAsync(fullPath);
                        }
                        else if (context.Request.Method == "PUT")
                        {
                            var dir = Path.GetDirectoryName(fullPath);
                            if (dir != null) Directory.CreateDirectory(dir);
                            using var fs = new FileStream(fullPath, FileMode.Create);
                            await context.Request.Body.CopyToAsync(fs);
                            await context.Response.WriteAsync("OK");
                        }
                        else if (context.Request.Method == "POST")
                        {
                            var dir = Path.GetDirectoryName(fullPath);
                            if (dir != null) Directory.CreateDirectory(dir);
                            using var fs = new FileStream(fullPath, FileMode.Append);
                            await context.Request.Body.CopyToAsync(fs);
                            await context.Response.WriteAsync("OK");
                        }
                        else
                        {
                            context.Response.StatusCode = 405;
                            await context.Response.WriteAsync("Method not allowed");
                        }
                        return;
                    }
                    await next();
                });

                // enable 404 fallback to default root
                app.UseStatusCodePagesWithReExecute(string.IsNullOrEmpty(RequestPath) ? "/" : RequestPath);

                // enable index.html fallback
                app.UseDefaultFiles(new DefaultFilesOptions
                {
                    FileProvider = new PhysicalFileProvider(WWWRoot),
                    RequestPath = RequestPath
                });
                // enable unknown file types (required)
                app.UseFileServer(new FileServerOptions
                {
                    FileProvider = new PhysicalFileProvider(WWWRoot),
                    RequestPath = RequestPath,
                    EnableDirectoryBrowsing = false, // Optional: allows browsing directory listings
                    StaticFileOptions = {
                        ServeUnknownFileTypes = true, // Crucial: serves all file types, even those without known MIME types
                        DefaultContentType = "application/octet-stream" // Optional: default MIME type for unknown files
                    }
                });
                // start hosting
                await app.RunAsync();
            }
            finally
            {
                app = null;
                runningTask = null;
            }
        }
        public async Task Stop()
        {
            if (app == null || runningTask == null) return;
            try
            {
                await app.StopAsync();
            }
            catch { }
            await app.DisposeAsync();
            if (runningTask != null)
            {
                try
                {
                    await runningTask;
                }
                catch { }
            }
        }

        // --- Signal server types for WebRTC cross-platform tests ---
        private static readonly ConcurrentDictionary<string, SignalRoom> SignalRooms = new();
        // --- Tracker protocol types ---
        private static readonly ConcurrentDictionary<string, TrackerSwarm> TrackerSwarms = new();
    }

    internal class SignalPeer
    {
        public string Id { get; }
        public WebSocket Socket { get; }
        private readonly SemaphoreSlim _lock = new(1, 1);
        public SignalPeer(string id, WebSocket socket) { Id = id; Socket = socket; }
        public async Task SendAsync(object message)
        {
            if (Socket.State != WebSocketState.Open) return;
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
            await _lock.WaitAsync();
            try { await Socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None); }
            finally { _lock.Release(); }
        }
    }

    internal class TrackerSwarm
    {
        private readonly ConcurrentDictionary<string, TrackerPeer> _peers = new();
        public void AddPeer(string peerId, WebSocket ws) => _peers[peerId] = new TrackerPeer(peerId, ws);
        public void RemovePeer(string peerId) => _peers.TryRemove(peerId, out _);
        public TrackerPeer? GetPeer(string peerId) => _peers.TryGetValue(peerId, out var p) ? p : null;
        public TrackerPeer[] GetOtherPeers(string excludeId) => _peers.Values.Where(p => p.PeerId != excludeId).ToArray();
    }

    internal class TrackerPeer
    {
        public string PeerId { get; }
        public WebSocket Socket { get; }
        public TrackerPeer(string peerId, WebSocket socket) { PeerId = peerId; Socket = socket; }
    }

    internal class SignalRoom
    {
        private readonly ConcurrentDictionary<string, SignalPeer> _peers = new();
        public int PeerCount => _peers.Count;
        public void AddPeer(SignalPeer peer) => _peers[peer.Id] = peer;
        public void RemovePeer(string id) => _peers.TryRemove(id, out _);
        public string[] GetPeerIds(string excludeId) => _peers.Keys.Where(id => id != excludeId).ToArray();
        public async Task BroadcastAsync(string fromId, object message)
        {
            foreach (var peer in _peers.Values)
                if (peer.Id != fromId) try { await peer.SendAsync(message); } catch { }
        }
        public async Task SendToPeerAsync(string targetId, object message)
        {
            if (_peers.TryGetValue(targetId, out var peer)) try { await peer.SendAsync(message); } catch { }
        }
    }
}
