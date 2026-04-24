using SpawnDev.RTC.Server;
using SpawnDev.UnitTesting;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace SpawnDev.RTC.DemoConsole.UnitTests
{
    /// <summary>
    /// Post-deployment smoke tests that run against a real deployed hub
    /// (hub.spawndev.com or any consumer host running SpawnDev.RTC.ServerApp
    /// / SpawnDev.WebTorrent.ServerApp with STUN/TURN + tracker-gated ephemeral
    /// credentials enabled). Exercise the deployed configuration end-to-end
    /// from a real external client's perspective - DNS, firewall, NAT
    /// port-forwarding, tracker + TURN process all included.
    ///
    /// <para><strong>Gating:</strong> These tests look for a set of
    /// <c>HUB_*</c> environment variables and throw
    /// <see cref="UnsupportedTestException"/> when any are missing, so normal
    /// CI runs skip them without noise. Set the vars to opt in.</para>
    ///
    /// <para><strong>Required env vars:</strong></para>
    /// <list type="bullet">
    ///   <item><c>HUB_TRACKER_WS</c> - WebSocket URL of the signaling tracker,
    ///         e.g. <c>wss://hub.spawndev.com:44365/announce</c></item>
    ///   <item><c>HUB_TURN_HOST</c> - DNS name or IP of the TURN server,
    ///         e.g. <c>hub.spawndev.com</c></item>
    ///   <item><c>HUB_SHARED_SECRET</c> - same value configured in
    ///         <c>RTC__StunTurn__EphemeralCredentialSharedSecret</c> on the
    ///         hub host (so this client can mint credentials the hub will
    ///         accept)</item>
    /// </list>
    ///
    /// <para><strong>Optional env vars:</strong></para>
    /// <list type="bullet">
    ///   <item><c>HUB_TURN_PORT</c> - TURN UDP port, default 3478</item>
    ///   <item><c>HUB_REALM</c> - TURN realm, default <c>spawndev-rtc</c></item>
    /// </list>
    ///
    /// <para><strong>How to run:</strong></para>
    /// <code>
    /// set HUB_TRACKER_WS=wss://hub.spawndev.com:44365/announce
    /// set HUB_TURN_HOST=hub.spawndev.com
    /// set HUB_SHARED_SECRET=&lt;same secret configured on the hub&gt;
    /// dotnet run --no-build -c Release -- HubDeploymentSmokeTests.Smoke_StunBindingRequest_HubRespondsOnUdp
    /// dotnet run --no-build -c Release -- HubDeploymentSmokeTests.Smoke_TrackerGatedTurn_DenyThenAllowThenDeny
    /// </code>
    /// </summary>
    public class HubDeploymentSmokeTests
    {
        /// <summary>
        /// Verify the hub's TURN UDP port is reachable and answers STUN binding
        /// requests. Prereq for any TURN functionality - if this fails, port
        /// forwarding / firewall is the problem, not the server.
        /// </summary>
        [TestMethod(Timeout = 15000)]
        public async Task Smoke_StunBindingRequest_HubRespondsOnUdp()
        {
            var cfg = HubConfig.ReadOrSkip();

            var mapped = await SendBindingRequestAsync(cfg.TurnEndpoint, TimeSpan.FromSeconds(5));
            if (mapped == null)
                throw new Exception(
                    $"No STUN binding response from {cfg.TurnEndpoint} within 5s - port forwarding / firewall issue, or TURN not running");

            // The mapped address is this machine's public IP from the server's
            // perspective. We can't predict its exact value (NAT), but it must
            // parse and be non-zero.
            if (mapped.Address.Equals(IPAddress.Any) || mapped.Address.Equals(IPAddress.None))
                throw new Exception($"STUN response mapped address invalid: {mapped.Address}");
            if (mapped.Port <= 0 || mapped.Port > 65535)
                throw new Exception($"STUN response mapped port out of range: {mapped.Port}");
        }

        /// <summary>
        /// Full tracker-gated TURN cycle against the deployed hub:
        /// 1. Try Allocate with a valid ephemeral credential BEFORE announcing - must be rejected.
        /// 2. WebSocket-announce to the hub's tracker.
        /// 3. Try Allocate again with a fresh credential - must succeed.
        /// 4. Close the WebSocket (tracker drops the peer).
        /// 5. Try Allocate once more - must be rejected again.
        ///
        /// Proves the deployed hub enforces tracker-gated TURN correctly and
        /// that the full flow (DNS / UDP / TCP WebSocket / HMAC verification)
        /// works from an external client's perspective.
        /// </summary>
        [TestMethod(Timeout = 60000)]
        public async Task Smoke_TrackerGatedTurn_DenyThenAllowThenDeny()
        {
            var cfg = HubConfig.ReadOrSkip();

            // Unique peer id per run - 20 bytes rendered as lowercase hex = 40 chars.
            // Tracker wire protocol expects a 20-byte latin1 string; hex representation
            // stays within that byte budget as long as we use 20 bytes of random + ascii.
            var rand = new byte[20];
            RandomNumberGenerator.Fill(rand);
            // Keep the peerId printable so it reads well in logs. Base64 encodes 20 bytes
            // into 28 chars which is fine for our username use.
            var peerId = Convert.ToBase64String(rand).TrimEnd('=');

            // === Step 1: Deny scan - no announce yet ===
            var (user1, pass1) = EphemeralTurnCredentials.Generate(cfg.SharedSecret, peerId, TimeSpan.FromMinutes(5));
            var relayBefore = await DoTurnAllocateAsync(cfg.TurnEndpoint, user1, cfg.Realm, pass1);
            if (relayBefore != null)
                throw new Exception(
                    $"DENY SCAN FAILED - hub issued a TURN relay (port {relayBefore.Port}) to a peer that had not announced to the tracker. " +
                    "TrackerGated should be true on the hub; check RTC__StunTurn__TrackerGated=true and that the shared secret matches.");

            // === Step 2: Announce via WebSocket ===
            using var ws = new ClientWebSocket();
            // Accept self-signed / untrusted certs for hub deployments using non-public TLS.
            // Remote cert validation can still be tightened by leaving the default in place
            // when the hub uses a real cert; this smoke test is meant to work in both cases.
            ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await ws.ConnectAsync(new Uri(cfg.TrackerWsUrl), connectCts.Token);
            }
            catch (Exception ex)
            {
                throw new Exception(
                    $"Failed to connect WebSocket to tracker at {cfg.TrackerWsUrl}: {ex.Message}. " +
                    "Check HUB_TRACKER_WS env var, DNS resolution, and tracker WebSocket reverse-proxy config.", ex);
            }

            // Announce with a random 20-byte info_hash (room key); tracker doesn't care about the value.
            var infoHashBytes = new byte[20];
            RandomNumberGenerator.Fill(infoHashBytes);
            var infoHash = Convert.ToHexString(infoHashBytes).ToLowerInvariant();
            var announceJson = $"{{\"action\":\"announce\",\"info_hash\":\"{infoHash}\",\"peer_id\":\"{peerId}\",\"numwant\":0}}";
            var announceBytes = Encoding.UTF8.GetBytes(announceJson);
            using var sendCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ws.SendAsync(announceBytes, WebSocketMessageType.Text, true, sendCts.Token);

            // Give the tracker a moment to process + register, then drain any response.
            using var drainCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var drainBuf = new byte[16384];
            try { await ws.ReceiveAsync(drainBuf, drainCts.Token); }
            catch (OperationCanceledException) { /* no response needed for gate to apply */ }

            // Small additional settle - tracker dispatch + gate apply are sub-100ms in tests,
            // but network round-trip to a real deployed hub can be 100-300ms. Give it 500ms.
            await Task.Delay(500);

            // === Step 3: Allocate while announced ===
            var (user2, pass2) = EphemeralTurnCredentials.Generate(cfg.SharedSecret, peerId, TimeSpan.FromMinutes(5));
            var relayWhile = await DoTurnAllocateAsync(cfg.TurnEndpoint, user2, cfg.Realm, pass2);
            if (relayWhile == null)
                throw new Exception(
                    $"POSITIVE PATH FAILED - announced peer '{peerId}' could not get a TURN allocation from the hub. " +
                    "Check: shared secret matches on hub + client; realm matches; hub's RelayAddress is a reachable public IP; " +
                    "UDP port is forwarded; if RelayPortRange is set, the range is also forwarded.");

            // === Step 4: Disconnect, wait for tracker to drop the peer ===
            try
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "smoke-test-done", closeCts.Token);
            }
            catch { /* best-effort close - the disconnect-side effect is what matters */ }
            ws.Dispose();

            // Tracker cleans up on receive-loop exit. Real network + async cleanup give it a moment.
            await Task.Delay(1000);

            // === Step 5: Allocate after disconnect - must be rejected again ===
            var (user3, pass3) = EphemeralTurnCredentials.Generate(cfg.SharedSecret, peerId, TimeSpan.FromMinutes(5));
            var relayAfter = await DoTurnAllocateAsync(cfg.TurnEndpoint, user3, cfg.Realm, pass3);
            if (relayAfter != null)
                throw new Exception(
                    "GATE-CLOSE FAILED - after the WebSocket closed, the hub still issued a TURN relay. " +
                    "Tracker cleanup on disconnect is not firing; check TrackerSignalingServer finally-block peer removal logic.");
        }

        // --- Config + helpers ---

        private sealed record HubConfig(string TrackerWsUrl, IPEndPoint TurnEndpoint, string SharedSecret, string Realm)
        {
            public static HubConfig ReadOrSkip()
            {
                var trackerWs = Environment.GetEnvironmentVariable("HUB_TRACKER_WS");
                var turnHost = Environment.GetEnvironmentVariable("HUB_TURN_HOST");
                var sharedSecret = Environment.GetEnvironmentVariable("HUB_SHARED_SECRET");

                if (string.IsNullOrWhiteSpace(trackerWs)
                    || string.IsNullOrWhiteSpace(turnHost)
                    || string.IsNullOrWhiteSpace(sharedSecret))
                {
                    throw new UnsupportedTestException(
                        "Hub deployment smoke test skipped. Set HUB_TRACKER_WS, HUB_TURN_HOST, HUB_SHARED_SECRET env vars to enable.");
                }

                var turnPortStr = Environment.GetEnvironmentVariable("HUB_TURN_PORT");
                var turnPort = int.TryParse(turnPortStr, out var p) && p > 0 ? p : 3478;
                var realm = Environment.GetEnvironmentVariable("HUB_REALM") ?? "spawndev-rtc";

                // Resolve HUB_TURN_HOST: accept DNS name or literal IP.
                IPAddress turnIp;
                if (!IPAddress.TryParse(turnHost, out turnIp!))
                {
                    var addrs = Dns.GetHostAddresses(turnHost);
                    turnIp = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                        ?? addrs.FirstOrDefault()
                        ?? throw new Exception($"Could not resolve HUB_TURN_HOST='{turnHost}' to an IP address");
                }

                return new HubConfig(trackerWs, new IPEndPoint(turnIp, turnPort), sharedSecret, realm);
            }
        }

        /// <summary>
        /// Minimal RFC 5389 Binding Request. No auth, no MESSAGE-INTEGRITY. Returns
        /// the XOR-MAPPED-ADDRESS from the response, or null on timeout / parse error.
        /// </summary>
        private static async Task<IPEndPoint?> SendBindingRequestAsync(IPEndPoint target, TimeSpan timeout)
        {
            using var udp = new UdpClient(0, AddressFamily.InterNetwork);

            var req = new byte[20];
            req[0] = 0x00; req[1] = 0x01;
            req[2] = 0x00; req[3] = 0x00;
            req[4] = 0x21; req[5] = 0x12; req[6] = 0xA4; req[7] = 0x42;
            RandomNumberGenerator.Fill(req.AsSpan(8, 12));

            await udp.SendAsync(req, req.Length, target);

            using var cts = new CancellationTokenSource(timeout);
            try
            {
                var r = await udp.ReceiveAsync(cts.Token);
                return ParseXorMappedAddress(r.Buffer);
            }
            catch (OperationCanceledException) { return null; }
        }

        private static IPEndPoint? ParseXorMappedAddress(byte[] resp)
        {
            if (resp.Length < 20) return null;
            if (resp[0] != 0x01 || resp[1] != 0x01) return null;

            int attrStart = 20;
            int msgLen = (resp[2] << 8) | resp[3];
            int end = Math.Min(attrStart + msgLen, resp.Length);
            int i = attrStart;
            while (i + 4 <= end)
            {
                int attrType = (resp[i] << 8) | resp[i + 1];
                int attrLen = (resp[i + 2] << 8) | resp[i + 3];
                int dataStart = i + 4;
                if (dataStart + attrLen > end) break;

                if (attrType == 0x0020 && attrLen >= 8)
                {
                    int xorPort = ((resp[dataStart + 2] << 8) | resp[dataStart + 3]) ^ 0x2112;
                    int a0 = resp[dataStart + 4] ^ 0x21;
                    int a1 = resp[dataStart + 5] ^ 0x12;
                    int a2 = resp[dataStart + 6] ^ 0xA4;
                    int a3 = resp[dataStart + 7] ^ 0x42;
                    return new IPEndPoint(new IPAddress(new[] { (byte)a0, (byte)a1, (byte)a2, (byte)a3 }), xorPort);
                }

                i = dataStart + ((attrLen + 3) & ~3);
            }
            return null;
        }

        /// <summary>
        /// RFC 5766 TURN Allocate round-trip against a remote endpoint. Returns the
        /// relay address on success, or null on any failure path (rejection, timeout,
        /// malformed response).
        /// </summary>
        private static async Task<IPEndPoint?> DoTurnAllocateAsync(
            IPEndPoint target, string username, string realm, string password)
        {
            using var udp = new UdpClient(0, AddressFamily.InterNetwork);

            var initialTxId = new byte[12];
            RandomNumberGenerator.Fill(initialTxId);
            var reqMsg = new SIPSorcery.Net.STUNMessage(SIPSorcery.Net.STUNMessageTypesEnum.Allocate);
            reqMsg.Header.TransactionId = initialTxId;
            reqMsg.Attributes.Add(new SIPSorcery.Net.STUNAttribute(
                SIPSorcery.Net.STUNAttributeTypesEnum.RequestedTransport,
                new byte[] { 17, 0, 0, 0 }));
            var initialBytes = reqMsg.ToByteBuffer(null, false);

            await udp.SendAsync(initialBytes, initialBytes.Length, target);

            using var cts1 = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            byte[] challengeBuf;
            try { var r = await udp.ReceiveAsync(cts1.Token); challengeBuf = r.Buffer; }
            catch (OperationCanceledException) { return null; }

            var challenge = SIPSorcery.Net.STUNMessage.ParseSTUNMessage(challengeBuf, challengeBuf.Length);
            if (challenge == null) return null;
            var realmAttr = challenge.Attributes.FirstOrDefault(a => a.AttributeType == SIPSorcery.Net.STUNAttributeTypesEnum.Realm);
            var nonceAttr = challenge.Attributes.FirstOrDefault(a => a.AttributeType == SIPSorcery.Net.STUNAttributeTypesEnum.Nonce);
            if (realmAttr?.Value == null || nonceAttr?.Value == null) return null;

            var authTxId = new byte[12];
            RandomNumberGenerator.Fill(authTxId);
            var authMsg = new SIPSorcery.Net.STUNMessage(SIPSorcery.Net.STUNMessageTypesEnum.Allocate);
            authMsg.Header.TransactionId = authTxId;
            authMsg.Attributes.Add(new SIPSorcery.Net.STUNAttribute(
                SIPSorcery.Net.STUNAttributeTypesEnum.RequestedTransport,
                new byte[] { 17, 0, 0, 0 }));
            authMsg.Attributes.Add(new SIPSorcery.Net.STUNAttribute(
                SIPSorcery.Net.STUNAttributeTypesEnum.Username, Encoding.UTF8.GetBytes(username)));
            authMsg.Attributes.Add(new SIPSorcery.Net.STUNAttribute(SIPSorcery.Net.STUNAttributeTypesEnum.Realm, realmAttr.Value));
            authMsg.Attributes.Add(new SIPSorcery.Net.STUNAttribute(SIPSorcery.Net.STUNAttributeTypesEnum.Nonce, nonceAttr.Value));

            var key = MD5.HashData(Encoding.UTF8.GetBytes($"{username}:{realm}:{password}"));
            var authBytes = authMsg.ToByteBuffer(key, false);

            await udp.SendAsync(authBytes, authBytes.Length, target);

            using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            byte[] finalBuf;
            try { var r = await udp.ReceiveAsync(cts2.Token); finalBuf = r.Buffer; }
            catch (OperationCanceledException) { return null; }

            var finalMsg = SIPSorcery.Net.STUNMessage.ParseSTUNMessage(finalBuf, finalBuf.Length);
            if (finalMsg == null) return null;
            if (finalMsg.Header.MessageType != SIPSorcery.Net.STUNMessageTypesEnum.AllocateSuccessResponse) return null;

            foreach (var attr in finalMsg.Attributes)
            {
                if (attr.AttributeType != SIPSorcery.Net.STUNAttributeTypesEnum.XORRelayedAddress) continue;
                if (attr.Value == null || attr.Value.Length < 8) continue;
                int xorPort = ((attr.Value[2] << 8) | attr.Value[3]) ^ 0x2112;
                int a0 = attr.Value[4] ^ 0x21;
                int a1 = attr.Value[5] ^ 0x12;
                int a2 = attr.Value[6] ^ 0xA4;
                int a3 = attr.Value[7] ^ 0x42;
                return new IPEndPoint(new IPAddress(new[] { (byte)a0, (byte)a1, (byte)a2, (byte)a3 }), xorPort);
            }
            return null;
        }
    }
}
