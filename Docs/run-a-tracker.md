# Run a tracker

`SpawnDev.RTC.ServerApp` is a single executable that speaks the WebTorrent tracker wire protocol over WebSocket. Any WebTorrent client - JS, .NET, Rust, Go - can announce against it. Any `SpawnDev.RTC.Signaling.TrackerSignalingClient` can also announce against it with a room key that has nothing to do with a torrent.

There are three common deploy shapes. Pick the one that matches your ops. All three front the server with TLS via a reverse proxy - browsers will refuse WebSocket connections that aren't either same-origin or TLS.

> **Heads up:** the same binary can also host an embedded STUN/TURN server on the same box (no coturn needed) - just set a couple of env vars. See [stun-turn-server.md](stun-turn-server.md) for credentials, NAT port-range setup, and the env-var turnkey config.

## Shape 1: Docker

Build from source (a published image is planned but not yet on a public registry):

```bash
git clone https://github.com/LostBeard/SpawnDev.RTC.git
cd SpawnDev.RTC
docker build -t spawndev/rtc-signaling -f SpawnDev.RTC/SpawnDev.RTC.ServerApp/Dockerfile SpawnDev.RTC
docker run -d -p 8080:8080 --restart unless-stopped \
  --name rtc-signaling \
  spawndev/rtc-signaling
```

The container listens on `0.0.0.0:8080`. Front it with your reverse proxy of choice (examples below). The container exposes `/announce` for WebSocket, plus `/health` and `/stats` for ops.

## Shape 2: Single binary on bare metal

Self-contained executables are produced by `dotnet publish` with a target RID. Pick the one your server runs:

```bash
cd SpawnDev.RTC/SpawnDev.RTC.ServerApp

# Linux x64 server
dotnet publish -c Release -r linux-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true

# Linux ARM64 (Raspberry Pi 4/5, AWS Graviton, Oracle ARM)
dotnet publish -c Release -r linux-arm64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true

# Windows x64
dotnet publish -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true

# macOS Apple Silicon
dotnet publish -c Release -r osx-arm64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

The resulting binary lives at `bin/Release/net10.0/<rid>/publish/SpawnDev.RTC.ServerApp` (or `.exe` on Windows). Copy it to the server. Run it:

```bash
ASPNETCORE_URLS=http://0.0.0.0:5590 ./SpawnDev.RTC.ServerApp
```

## Shape 3: systemd on Linux

`/etc/systemd/system/rtc-signaling.service`:

```ini
[Unit]
Description=SpawnDev.RTC signaling server
After=network.target

[Service]
Type=simple
User=rtc
Group=rtc
WorkingDirectory=/opt/rtc-signaling
ExecStart=/opt/rtc-signaling/SpawnDev.RTC.ServerApp
Restart=on-failure
RestartSec=5
Environment=ASPNETCORE_URLS=http://0.0.0.0:5590
# Optional: tune the tracker
Environment=RTC__AnnounceIntervalSeconds=120
Environment=RTC__MaxPeersPerAnnounce=50

# Hardening
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=/var/log/rtc-signaling

[Install]
WantedBy=multi-user.target
```

Then:

```bash
sudo useradd --system --no-create-home --shell /usr/sbin/nologin rtc
sudo mkdir -p /opt/rtc-signaling /var/log/rtc-signaling
sudo cp SpawnDev.RTC.ServerApp /opt/rtc-signaling/
sudo chown -R rtc:rtc /opt/rtc-signaling /var/log/rtc-signaling
sudo systemctl daemon-reload
sudo systemctl enable --now rtc-signaling
sudo systemctl status rtc-signaling
```

## Reverse proxy configs

Browsers require TLS for WebSocket connections to any origin that isn't `localhost`. Do not expose the raw `http://:5590` port to the public internet. Always front it with one of these.

### Caddy (simplest)

```caddy
tracker.example.com {
    reverse_proxy localhost:5590
}
```

Caddy auto-provisions a Let's Encrypt cert on first request. That is the whole config.

### nginx

```nginx
server {
    listen 443 ssl http2;
    server_name tracker.example.com;

    ssl_certificate     /etc/letsencrypt/live/tracker.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/tracker.example.com/privkey.pem;

    location / {
        proxy_pass http://127.0.0.1:5590;
        proxy_http_version 1.1;

        # WebSocket upgrade - required for /announce
        proxy_set_header Upgrade           $http_upgrade;
        proxy_set_header Connection        "upgrade";

        proxy_set_header Host              $host;
        proxy_set_header X-Real-IP         $remote_addr;
        proxy_set_header X-Forwarded-For   $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;

        # WebSocket connections can be long-lived
        proxy_read_timeout  24h;
        proxy_send_timeout  24h;
    }
}
```

### haproxy

```haproxy
frontend fe_tracker
    bind *:443 ssl crt /etc/haproxy/certs/tracker.pem alpn h2,http/1.1
    default_backend be_tracker

backend be_tracker
    option forwardfor
    server rtc 127.0.0.1:5590 check
```

### Cloudflare

Cloudflare's free tier proxies WebSockets for any paid Cloudflare domain. Set the orange cloud on your DNS record, enable "WebSockets" in Cloudflare dashboard (default on). That's the whole story. The server sees `X-Forwarded-For` with the real client IP and `X-Forwarded-Proto: https`. `ForwardedHeaders` middleware picks these up automatically.

## Configuration reference

All settings read from `appsettings.json`, environment variables (double underscore for nesting), or command-line args. Later sources override earlier ones.

| Setting | Env var | Default | Purpose |
|---------|---------|---------|---------|
| `RTC:Path` | `RTC__Path` | `/announce` | Endpoint path for the WebSocket |
| `RTC:AnnounceIntervalSeconds` | `RTC__AnnounceIntervalSeconds` | `120` | Re-announce interval returned to clients |
| `RTC:MaxPeersPerAnnounce` | `RTC__MaxPeersPerAnnounce` | `50` | Ceiling on peer list per announce response |
| `RTC:MaxMessageBytes` | `RTC__MaxMessageBytes` | `1000000` | Drop WebSocket frames above this size |
| `RTC:SendTimeoutMs` | `RTC__SendTimeoutMs` | `10000` | Per-send timeout when forwarding relays |
| `ASPNETCORE_URLS` | `ASPNETCORE_URLS` | `http://0.0.0.0:5590` | Listen address(es). Override for TLS or a different port. |

## Verifying your deployment

`/health` returns JSON including current room count:

```bash
curl https://tracker.example.com/health
# {"status":"ok","rooms":0,"peers":0}
```

`/stats` returns a live snapshot:

```bash
curl https://tracker.example.com/stats
# {"rooms":2,"totalPeers":4,"roomDetails":[{"roomKey":"…","peers":2,"seeders":1,"leechers":1},…]}
```

Point a WebTorrent client at your tracker:

```javascript
// Browser JS (using the webtorrent npm package)
const client = new WebTorrent({
    announce: ['wss://tracker.example.com/announce']
});
client.add(magnetURI, torrent => { /* torrent using your tracker */ });
```

Or point a SpawnDev.RTC consumer at it:

```csharp
using SpawnDev.RTC.Signaling;

var peerId = new byte[20];
System.Security.Cryptography.RandomNumberGenerator.Fill(peerId);
await using var client = new TrackerSignalingClient("wss://tracker.example.com/announce", peerId);

var room = RoomKey.FromString("my-app-lobby-42");
client.Subscribe(room, new RtcPeerConnectionRoomHandler(new() { /* ICE config */ }));
await client.AnnounceAsync(room, new AnnounceOptions { Event = "started", NumWant = 5 });
```

Both consumers produce room entries on the same tracker; neither knows or cares what the other is doing.

## Operational notes

- **One tracker is plenty** for most apps. The WebSocket is long-lived and signaling happens off-path once peers meet. A single tracker on modest hardware handles thousands of concurrent rooms.
- **No database.** All state lives in memory. Restart loses the peer list; clients re-announce within `AnnounceIntervalSeconds` and the swarm rebuilds itself. This is by design - trackers are meeting points, not sources of truth.
- **No persistence configuration.** There is nothing to back up. You are a coordinator, not a data store.
- **Logging.** Default level is `Information` for `Microsoft.Hosting.*` and `Warning` for `Microsoft.AspNetCore.*`. Tune via `appsettings.json` `Logging:LogLevel`.
- **Memory baseline.** Empty server sits around 40-50 MB RAM on linux-x64. Per-connection overhead is a single WebSocket plus a 16 KB receive buffer.
- **Scaling.** If you outgrow one box, run multiple trackers. WebTorrent clients natively announce to a list of trackers and merge peer lists. The SpawnDev.RTC client does the same via multiple `TrackerSignalingClient` instances.

## Why host one

- **You get signaling that Google/Amazon/Microsoft don't proxy** for your users. Self-hosted infra for self-hosted apps.
- **WebTorrent compatibility is a bonus.** Every instance you run helps every WebTorrent client on the public network find peers. You strengthen a decentralized commons by pursuing your own product's needs. The two goals aren't opposed; they're the same action.
- **Zero operational cost beyond a small VPS.** 1 GB RAM is 10x what you need.
