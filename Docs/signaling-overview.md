# Signaling overview

WebRTC lets two endpoints talk peer-to-peer over UDP with DTLS encryption and SCTP data channels. Before they can talk, each endpoint has to exchange a small piece of setup information (an **SDP offer/answer** plus **ICE candidates**) with the other. WebRTC does not specify how that exchange happens. You bring your own **signaling**.

`SpawnDev.RTC.Signaling` is a signaling implementation. It speaks the WebTorrent tracker wire protocol - because that protocol is already a generic room-based WebRTC signaling relay hiding under a torrent-flavored name, and because speaking it means you automatically interop with the public WebTorrent tracker fleet.

## Two namespaces, two sides of the wire

- **`SpawnDev.RTC.Signaling`** (ships in `SpawnDev.RTC` package): the **client**. Consumers use this to connect to a tracker, join rooms, and exchange offers/answers.
- **`SpawnDev.RTC.Server`** (ships in `SpawnDev.RTC.Server` package): the **server**. Host it in any ASP.NET Core app with a one-line extension method (`app.UseRtcSignaling("/announce")`), or run the standalone `SpawnDev.RTC.ServerApp` exe / Docker image.

Both sides speak the same wire format. A `SpawnDev.RTC.Signaling.TrackerSignalingClient` can connect to `wss://tracker.openwebtorrent.com/announce` and meet other WebTorrent peers. A stock JS WebTorrent client can connect to your `SpawnDev.RTC.ServerApp` and torrent. The protocol is symmetric.

## The three public types you'll touch

### `RoomKey`

A 20-byte opaque value that identifies a room. On the wire it matches the WebTorrent `info_hash` byte format exactly. Construct from:

```csharp
var a = RoomKey.FromString("my-lobby-42");           // SHA-1 of UTF-8, no normalization
var b = RoomKey.FromHex("9fa3...20-byte-hex");       // explicit hex
var c = RoomKey.FromBytes(20-byte-array);            // torrent info_hash literal
var d = RoomKey.Random();                            // app-generated unique room
```

`FromString` hashes the literal UTF-8 bytes with SHA-1. **No trim, no lowercase, no normalization.** A consumer that changes `"my-lobby"` to `"My-Lobby"` or `" my-lobby "` before hashing silently lands in a different room. Be consistent.

### `TrackerSignalingClient`

The wire-level client. One instance per `(announceUrl, peerId)` pair - multiple rooms can multiplex on a single connection.

```csharp
var peerId = new byte[20];
System.Security.Cryptography.RandomNumberGenerator.Fill(peerId);

await using var client = new TrackerSignalingClient(
    "wss://tracker.openwebtorrent.com/announce",
    peerId);

client.OnConnected += () => Console.WriteLine("connected");
client.OnWarning   += w  => Console.WriteLine($"warning: {w}");
```

The client by itself doesn't know about `IRTCPeerConnection`. It just shuttles bytes. To actually create peer connections, pair it with a handler.

### `RtcPeerConnectionRoomHandler`

Default room handler. Given an ICE configuration, it owns a pool of `IRTCPeerConnection` instances - one per remote peer - and wires them up to the signaling client.

```csharp
var handler = new RtcPeerConnectionRoomHandler(new RTCPeerConnectionConfig
{
    IceServers = new[]
    {
        new RTCIceServerConfig { Urls = new[] { "stun:stun.l.google.com:19302" } }
    }
});

handler.OnPeerConnection += (pc, peerId) => { /* pc is ready; add tracks / transceivers */ };
handler.OnDataChannel    += (channel, peerId) => { /* browser peer opened a DC */ };
handler.OnPeerConnectionCreated += (pc, peerId) => { /* pre-SDP hook for media wiring */ };

var room = RoomKey.FromString("my-lobby");
client.Subscribe(room, handler);
await client.AnnounceAsync(room, new AnnounceOptions { Event = "started", NumWant = 5 });
```

That's the whole end-to-end client-side flow. Anything more specific - custom peer selection, custom data-channel lifecycle, media-only peers - is a matter of writing your own `ISignalingRoomHandler` and subscribing it the same way.

## Why the WebTorrent wire format

It's the only widely-deployed WebRTC signaling protocol with a public tracker fleet. Every other option is proprietary (PeerJS server, matrix.org, custom per-app) or rolls its own (most WebRTC tutorials). By speaking the WebTorrent wire, consumers of `SpawnDev.RTC.Signaling`:

1. **Get the public fleet for free** - `tracker.openwebtorrent.com` et al work out of the box with no account, no API key, no rate limit beyond the tracker's own norms.
2. **Can run their own tracker with one command** (see [run-a-tracker.md](run-a-tracker.md)) and that tracker simultaneously serves the public WebTorrent network. Running your own infra strengthens the public commons.
3. **Interop across ecosystems** - a WebTorrent client and a SpawnDev.RTC consumer can share a room if they use the same `RoomKey` bytes.

The tradeoff: the wire format dictates a 20-byte room identifier. If your app has user-friendly names like `"lobby-42"`, you hash them into `RoomKey.FromString(...)`. There is no registration, no namespace, and no verification. Collisions are cosmic-ray-unlikely with SHA-1 and not a security concern - rooms aren't a trust boundary. For that, use `RoomKey.Random()` and pass the handle through your own auth'd channel.

## What `SpawnDev.RTC.Signaling` does not handle

- **Authentication / access control**: rooms are public to anyone who knows the key. If you need auth, do it above this layer (e.g. sign the room key, include a signed handshake in the first data channel message).
- **Presence / member list**: the tracker's announce response includes a peer list, but that list is a snapshot, not a subscription. For real-time presence, maintain it over the data channel once connected.
- **NAT traversal fallback**: STUN works through most NATs. For TURN fallback when STUN fails, add TURN URLs to your `RTCIceServerConfig`. `SpawnDev.RTC.Signaling` doesn't change how WebRTC does NAT traversal - it just delivers offers/answers.

## Where to go next

- [run-a-tracker.md](run-a-tracker.md) - host your own signaling server
- [use-cases.md](use-cases.md) - concrete example architectures
- `SpawnDev.RTC.Demo/Pages/ChatRoom.razor` - working Blazor WASM consumer
- `SpawnDev.RTC.DemoConsole/ChatMode.cs` - desktop console consumer
- `SpawnDev.RTC.WpfDemo/MainWindow.xaml.cs` - desktop WPF consumer with media
