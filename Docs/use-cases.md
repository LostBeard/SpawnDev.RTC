# Use cases

Concrete example architectures for `SpawnDev.RTC.Signaling` + `SpawnDev.RTC.Server`. Each shows how the room abstraction maps to an application domain. Code is illustrative - production consumers would add auth, presence, error handling.

## Multiplayer game lobby

**Shape**: short-lived rooms (one per match), 2-8 peers per room, full-mesh data channels for state sync, voice chat over a media channel.

```csharp
// Shared code - client side
public class LobbyClient
{
    private readonly TrackerSignalingClient _signal;
    private readonly RtcPeerConnectionRoomHandler _handler;

    public LobbyClient(string trackerUrl, byte[] peerId)
    {
        _signal = new TrackerSignalingClient(trackerUrl, peerId);
        _handler = new RtcPeerConnectionRoomHandler(new RTCPeerConnectionConfig
        {
            IceServers = new[] { new RTCIceServerConfig { Urls = new[] { "stun:stun.l.google.com:19302" } } }
        });

        _handler.OnDataChannel += (channel, peerId) =>
        {
            channel.OnStringMessage += m => HandleGameMessage(peerId, m);
        };
    }

    public async Task JoinMatch(string matchCode)
    {
        var room = RoomKey.FromString($"match:{matchCode}");
        _signal.Subscribe(room, _handler);
        await _signal.AnnounceAsync(room, new AnnounceOptions { Event = "started", NumWant = 7 });
    }

    private void HandleGameMessage(string peerId, string json) { /* parse + apply */ }
}
```

Room keys are derived from match codes the matchmaking service hands out. Once peers meet, the tracker is out of the loop - the data channel carries all the state sync. Voice chat adds `RTCTrackEvent` wiring in `OnPeerConnectionCreated` before the SDP negotiation finishes.

## Collaborative document editor

**Shape**: long-lived rooms (one per document), 2-30 peers per room, CRDT state over a single data channel per peer pair, presence on that same channel.

```csharp
var room = RoomKey.FromString($"doc:{documentId}:{documentVersion}");
signal.Subscribe(room, new CrdtDocumentHandler(document));
await signal.AnnounceAsync(room, new AnnounceOptions { Event = "started", NumWant = 10 });
```

Include `documentVersion` in the key so a doc's peers rendezvous on the same CRDT state root. When a doc is rebased / snapshotted to a new root, bump the version - peers naturally re-meet in the new room. You never have to write room-migration logic.

## Voice / video chat room

**Shape**: long-lived rooms (meeting or channel), 2-50 peers, mesh (small rooms) or SFU (large rooms), audio + optional video tracks.

```csharp
_handler.OnPeerConnectionCreated += (pc, peerId) =>
{
    // Add our outbound audio track BEFORE the offer/answer exchange starts.
    pc.AddTrack(_localMicTrack);
    if (_videoEnabled) pc.AddTrack(_localCamTrack);
};

_handler.OnPeerConnection += (pc, peerId) =>
{
    // Inbound tracks land here.
    pc.OnTrack += e => AttachRemoteTrack(peerId, e.Track);
};
```

Full mesh scales to ~12 peers before the math gets ugly (each peer sends video to every other peer). Beyond that, route through an SFU that acts as a signaling-layer peer itself - the SFU is just another participant that happens to forward tracks.

## Distributed compute / agent swarm

**Shape**: worker pool joins a coordinator's room, coordinator distributes tasks over data channels, workers return results. Arbitrary numbers of workers, coordinator is a single peer.

```csharp
// Coordinator
var jobRoom = RoomKey.Random();
await PublishJobCodeAsync(jobCode, jobRoom);  // post the room key + job spec somewhere workers can fetch

// Workers (scale-out)
var room = await FetchJobRoomAsync(jobCode);
signal.Subscribe(room, new WorkerHandler(jobSpec));
await signal.AnnounceAsync(room, new AnnounceOptions { Event = "started", NumWant = 1 });
// Worker only wants to meet the coordinator - NumWant=1 is intentional.
```

`NumWant=1` is a protocol hint to the tracker: "I only need one peer." With a coordinator-workers shape, each worker just needs the coordinator, not every other worker. Bandwidth savings scale with worker count.

## IoT / edge mesh

**Shape**: devices in a physical location (a building, a farm, a lab) form a mesh for redundancy. Each device announces to a location-derived room; anyone else in the same place can reach it.

```csharp
// Location is a deterministic key all devices in the same place can compute.
// e.g. the geohash of the site, or a preshared label.
var room = RoomKey.FromString($"site:{siteCode}:sensors");
signal.Subscribe(room, sensorHandler);
await signal.AnnounceAsync(room, new AnnounceOptions { Event = "started", NumWant = 20 });
```

Mesh resilience: if the tracker is unreachable, peers that were already connected stay connected. Data channels survive tracker outage. When the tracker comes back, new devices rejoin the mesh.

## Cross-origin dev tools (debugger <-> app)

**Shape**: a dev inspector browser tab and the app it's inspecting connect peer-to-peer without the developer wiring up WebSocket proxies or ngrok tunnels.

```csharp
// App side - advertise a stable room per session
var room = RoomKey.FromString($"devtool:{Environment.UserName}:{AppInstanceId}");
signal.Subscribe(room, new DevToolsHandler());
await signal.AnnounceAsync(room, new AnnounceOptions { Event = "started", NumWant = 1 });
```

The inspector uses the same room key. The app exposes a debug data channel, the inspector drives it. Neither side needs a public IP or a tunnel because WebRTC handles NAT traversal natively.

## Key pattern across all of these

**Derive the room key from application identity, not from a registration.** `$"match:{matchCode}"`, `$"doc:{documentId}:{version}"`, `$"site:{siteCode}:sensors"`. The tracker doesn't validate these keys - it just groups announcers by key and relays offers/answers within the group. If two consumers compute the same key independently, they meet. If they compute different keys, they don't. This is the only property the tracker guarantees.

Everything else - auth, presence, state, media, recovery - you do above this layer.
