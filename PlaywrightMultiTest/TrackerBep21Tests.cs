using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SpawnDev.RTC.Server;
using SpawnDev.RTC.Signaling;

namespace PlaywrightMultiTest;

/// <summary>Real tracker-WebSocket tests for BEP 21 partial-seed accounting.
/// https://www.bittorrent.org/beps/bep_0021.html</summary>
[TestFixture]
public class TrackerBep21Tests
{
    private const string RoomA = "bbbbbbbbbbbbbbbbbbbb";
    private const string RoomB = "cccccccccccccccccccc";

    [Test]
    public void StateTransitions_ReplaceStickySeederStateAndHandleMissingLeft()
    {
        var seed = TrackerPeerState.FromAnnounce("completed", 0);
        Assert.That(seed.IsSeeder, Is.True);
        Assert.That(seed.IsPartialSeed, Is.False);

        var active = TrackerPeerState.FromAnnounce(null, 4, seed);
        Assert.That(active.IsSeeder, Is.False);
        Assert.That(active.IsPartialSeed, Is.False);

        var paused = TrackerPeerState.FromAnnounce("paused", 4, active);
        Assert.That(paused.IsSeeder, Is.False);
        Assert.That(paused.IsPartialSeed, Is.True);

        var resumed = TrackerPeerState.FromAnnounce(null, 4, paused);
        Assert.That(resumed.IsPartialSeed, Is.False);

        // Event has priority: never count a peer explicitly saying paused as a full seed.
        var inconsistent = TrackerPeerState.FromAnnounce("paused", 0);
        Assert.That(inconsistent.IsPartialSeed, Is.True);
        Assert.That(inconsistent.IsSeeder, Is.False);

        Assert.That(TrackerPeerState.FromAnnounce(null, null, seed).IsSeeder, Is.True);
        Assert.That(TrackerPeerState.FromAnnounce("started", 0).IsSeeder, Is.True);
    }

    [Test]
    public async Task WebSocketTracker_TracksPartialSeedsAndTransitionsInAnnounceResponses()
    {
        await using var host = await TrackerHost.StartAsync();
        using var seed = await host.ConnectAsync();
        using var partial = await host.ConnectAsync();
        using var downloader = await host.ConnectAsync();

        await host.AnnounceAsync(seed, RoomA, "aaaaaaaaaaaaaaaaaaaa", "started", 0);
        await host.AnnounceAsync(partial, RoomA, "dddddddddddddddddddd", "paused", 1024);
        var totals = await host.AnnounceAsync(downloader, RoomA, "eeeeeeeeeeeeeeeeeeee", "started", 8192);
        CheckCounts(totals, complete: 1, incomplete: 2, downloaders: 1);
        var room = host.Server.Rooms[RoomA];
        Assert.That(room.PartialSeedCount, Is.EqualTo(1));

        // Repeated paused announces must NOT increment downloaders.
        totals = await host.AnnounceAsync(partial, RoomA, "dddddddddddddddddddd", "paused", 1024);
        CheckCounts(totals, 1, 2, 1);

        totals = await host.AnnounceAsync(partial, RoomA, "dddddddddddddddddddd", null, 1024);
        CheckCounts(totals, 1, 2, 2);

        // Former full seed can become incomplete; the seeder bit is not sticky.
        totals = await host.AnnounceAsync(seed, RoomA, "aaaaaaaaaaaaaaaaaaaa", null, 64);
        CheckCounts(totals, 0, 3, 3);

        totals = await host.AnnounceAsync(downloader, RoomA, "eeeeeeeeeeeeeeeeeeee", "stopped", 8192);
        CheckCounts(totals, 0, 2, 2);
        Assert.That(room.Peers.Count, Is.EqualTo(2));
    }

    [Test]
    public async Task SharedWebSocket_TracksIndependentCompletionStatePerRoom()
    {
        await using var host = await TrackerHost.StartAsync();
        using var peer = await host.ConnectAsync();

        var seed = await host.AnnounceAsync(peer, RoomA, "aaaaaaaaaaaaaaaaaaaa", "completed", 0);
        CheckCounts(seed, 1, 0, 0);
        var paused = await host.AnnounceAsync(peer, RoomB, "aaaaaaaaaaaaaaaaaaaa", "paused", 17);
        CheckCounts(paused, 0, 1, 0);

        var first = await host.AnnounceAsync(peer, RoomA, "aaaaaaaaaaaaaaaaaaaa", null, 0);
        CheckCounts(first, 1, 0, 0);
        var second = await host.AnnounceAsync(peer, RoomB, "aaaaaaaaaaaaaaaaaaaa", "paused", 17);
        CheckCounts(second, 0, 1, 0);
        Assert.That(host.Server.Rooms[RoomA].SeederCount, Is.EqualTo(1));
        Assert.That(host.Server.Rooms[RoomB].PartialSeedCount, Is.EqualTo(1));
    }

    [Test]
    public async Task PartialSeed_RemainsDiscoverableAndReceivesForwardedOffers()
    {
        await using var host = await TrackerHost.StartAsync();
        using var partial = await host.ConnectAsync();
        using var active = await host.ConnectAsync();

        await host.AnnounceAsync(partial, RoomA, "pppppppppppppppppppp", "paused", 123);
        var announce = new
        {
            action = "announce", info_hash = RoomA, peer_id = "aaaaaaaaaaaaaaaaaaaa",
            @event = "started", left = 321, numwant = 1,
            offers = new[] { new { offer_id = "offer-01", offer = new { type = "offer", sdp = "test-sdp" } } }
        };
        await TrackerHost.SendAsync(active, JsonSerializer.Serialize(announce));
        using var response = await TrackerHost.ReceiveAsync(active);
        CheckCounts(response.RootElement, 0, 2, 1);

        using var relay = await TrackerHost.ReceiveAsync(partial);
        var frame = relay.RootElement;
        Assert.That(frame.GetProperty("action").GetString(), Is.EqualTo("announce"));
        Assert.That(frame.GetProperty("peer_id").GetString(), Is.EqualTo("aaaaaaaaaaaaaaaaaaaa"));
        Assert.That(frame.GetProperty("offer_id").GetString(), Is.EqualTo("offer-01"));
        Assert.That(frame.GetProperty("offer").GetProperty("sdp").GetString(), Is.EqualTo("test-sdp"));
    }


    [Test]
    public async Task RtcSignalingClient_ReannouncePreservesPausedAndReportsDownloaderStats()
    {
        await using var host = await TrackerHost.StartAsync(intervalSeconds: 1);
        var room = RoomKey.FromBytes(Encoding.ASCII.GetBytes(RoomA));
        await using var client = new TrackerSignalingClient(host.Endpoint.ToString(),
            Enumerable.Repeat((byte)'c', 20).ToArray());
        var stats = new System.Collections.Concurrent.ConcurrentQueue<SignalingSwarmStats>();
        client.OnSwarmStats += (key, value) =>
        {
            if (key == room) stats.Enqueue(value);
        };
        for (int i = 0; i < 50 && !client.IsConnected; i++)
            await Task.Delay(100);
        Assert.That(client.IsConnected, Is.True, "RTC signaling socket did not connect");

        await client.AnnounceAsync(room, new AnnounceOptions
        {
            Event = "paused", Left = 77, NumWant = 0
        });

        for (int i = 0; i < 50 &&
            (!host.Server.Rooms.TryGetValue(RoomA, out var r) || r.PartialSeedCount != 1); i++)
            await Task.Delay(100);
        Assert.That(host.Server.Rooms[RoomA].PartialSeedCount, Is.EqualTo(1));
        // The tracker now reannounces every second. Verify the first periodic
        // announce did not clear paused and reclassify the peer as downloader.
        await Task.Delay(2200);
        Assert.That(host.Server.Rooms[RoomA].PartialSeedCount, Is.EqualTo(1));
        Assert.That(host.Server.Rooms[RoomA].DownloaderCount, Is.EqualTo(0));
        Assert.That(stats.Any(s => s.Downloaders == 0), Is.True);

        await client.AnnounceAsync(room, new AnnounceOptions { Left = 77, NumWant = 0 });
        for (int i = 0; i < 50 && host.Server.Rooms[RoomA].DownloaderCount != 1; i++)
            await Task.Delay(100);
        Assert.That(host.Server.Rooms[RoomA].PartialSeedCount, Is.Zero);
        Assert.That(host.Server.Rooms[RoomA].DownloaderCount, Is.EqualTo(1));
        for (int i = 0; i < 50 && !stats.Any(s => s.Downloaders == 1); i++)
            await Task.Delay(100);
        Assert.That(stats.Any(s => s.Downloaders == 1), Is.True);
    }

    private static void CheckCounts(JsonElement e, int complete, int incomplete, int downloaders)
    {
        Assert.That(e.GetProperty("complete").GetInt32(), Is.EqualTo(complete), "complete");
        Assert.That(e.GetProperty("incomplete").GetInt32(), Is.EqualTo(incomplete), "incomplete");
        Assert.That(e.GetProperty("downloaders").GetInt32(), Is.EqualTo(downloaders), "downloaders");
    }

    private sealed class TrackerHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        public TrackerSignalingServer Server { get; }
        private readonly Uri _endpoint;
        public Uri Endpoint => _endpoint;

        private TrackerHost(WebApplication app, TrackerSignalingServer server, Uri endpoint)
        {
            _app = app;
            Server = server;
            _endpoint = endpoint;
        }

        public static async Task<TrackerHost> StartAsync(int intervalSeconds = 600)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            var server = new TrackerSignalingServer(new TrackerServerOptions
            {
                AnnounceIntervalSeconds = intervalSeconds, PeerTimeoutSeconds = 120
            });
            app.UseWebSockets();
            app.Map("/announce", server.HandleWebSocketAsync);
            await app.StartAsync();

            var addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses;
            var address = addresses?.FirstOrDefault(a => a.StartsWith("http://127.0.0.1:"))
                ?? throw new Exception("Local Kestrel listener did not bind");
            return new TrackerHost(app, server, new Uri(address.Replace("http:", "ws:") + "/announce"));
        }

        public async Task<ClientWebSocket> ConnectAsync()
        {
            var ws = new ClientWebSocket();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await ws.ConnectAsync(_endpoint, timeout.Token);
            return ws;
        }

        public async Task<JsonElement> AnnounceAsync(ClientWebSocket ws, string infoHash,
            string peerId, string? announceEvent, long left)
        {
            await SendAsync(ws, JsonSerializer.Serialize(new
            {
                action = "announce", info_hash = infoHash, peer_id = peerId,
                @event = announceEvent, left, numwant = 0
            }));
            using var document = await ReceiveAsync(ws);
            return document.RootElement.Clone();
        }

        public static async Task SendAsync(ClientWebSocket ws, string json)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var bytes = Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, timeout.Token);
        }

        public static async Task<JsonDocument> ReceiveAsync(ClientWebSocket ws)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var stream = new MemoryStream();
            var buffer = new byte[4096];
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new Exception("Tracker closed the WebSocket without a response");
                stream.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);
            return JsonDocument.Parse(stream.ToArray());
        }

        public async ValueTask DisposeAsync() => await _app.DisposeAsync();
    }
}
