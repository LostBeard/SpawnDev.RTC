using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
using SpawnDev.RTC.Server;
using SpawnDev.RTC.Server.Extensions;
using SpawnDev.UnitTesting;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace SpawnDev.RTC.DemoConsole.UnitTests
{
    /// <summary>
    /// Integration tests for SpawnDev.RTC.Server's STUN/TURN pipeline:
    ///   - <see cref="EphemeralTurnCredentials"/> helper round-trip / expiry / tamper tests.
    ///   - <see cref="StunTurnServerHostedService"/> DI registration + start/stop lifecycle.
    ///   - <c>TurnServerConfig.ResolveHmacKey</c> per-request key resolver (our fork extension)
    ///     end-to-end against a real TurnServer, including classic long-term Allocate
    ///     sign/verify and ephemeral-credential-based Allocate sign/verify.
    ///   - Expired-credential rejection end-to-end.
    ///
    /// Desktop-only (UDP sockets, ASP.NET Core hosted service). Exercises real production
    /// code paths - no mocks. Each test picks a unique high port to avoid collision.
    /// </summary>
    public class DesktopTurnAuthTests
    {
        // --- EphemeralTurnCredentials unit tests -------------------------------

        [TestMethod]
        public Task Ephemeral_Generate_ProducesParseableUsernameAndHmacPassword()
        {
            var (username, password) = EphemeralTurnCredentials.Generate(
                "test-shared-secret-that-is-at-least-32-bytes-long", "alice", TimeSpan.FromMinutes(5));

            var colonIdx = username.IndexOf(':');
            if (colonIdx <= 0) throw new Exception($"Username missing expiry prefix: {username}");
            var expiryStr = username.Substring(0, colonIdx);
            if (!long.TryParse(expiryStr, out var expiry)) throw new Exception($"Expiry not parseable: {expiryStr}");
            var expiryDt = DateTimeOffset.FromUnixTimeSeconds(expiry);
            if (expiryDt < DateTimeOffset.UtcNow) throw new Exception("Expiry should be in the future");
            if (expiryDt > DateTimeOffset.UtcNow.AddMinutes(6)) throw new Exception("Expiry should be within the lifetime we requested");
            if (username.Substring(colonIdx + 1) != "alice") throw new Exception("userId segment not preserved");

            // Password should be Base64-SHA1 = exactly 28 chars (20 bytes -> 28 Base64).
            if (password.Length != 28) throw new Exception($"Password length {password.Length}, expected 28");
            if (!password.EndsWith("=")) throw new Exception("Base64 SHA1 should end with '='");

            return Task.CompletedTask;
        }

        [TestMethod]
        public Task Ephemeral_Validate_AcceptsFreshCredentials()
        {
            const string secret = "validate-test-shared-secret-32-bytes-ok";
            var (user, pass) = EphemeralTurnCredentials.Generate(secret, "alice", TimeSpan.FromMinutes(5));

            if (!EphemeralTurnCredentials.Validate(secret, user, pass))
                throw new Exception("Fresh credentials should validate against their issuing secret");

            return Task.CompletedTask;
        }

        [TestMethod]
        public Task Ephemeral_Validate_RejectsTamperedPassword()
        {
            const string secret = "tamper-test-shared-secret-32-bytes-ok-ok";
            var (user, pass) = EphemeralTurnCredentials.Generate(secret, "alice", TimeSpan.FromMinutes(5));

            // Flip one character of the base64 password.
            var tamperedPass = pass[0] == 'A'
                ? "B" + pass.Substring(1)
                : "A" + pass.Substring(1);

            if (EphemeralTurnCredentials.Validate(secret, user, tamperedPass))
                throw new Exception("Tampered password should not validate");

            return Task.CompletedTask;
        }

        [TestMethod]
        public Task Ephemeral_Validate_RejectsWrongSecret()
        {
            const string issuingSecret = "issuing-secret-is-32-bytes-long-here-ok";
            const string attackerSecret = "attacker-has-different-secret-also-32bt";
            var (user, pass) = EphemeralTurnCredentials.Generate(issuingSecret, "alice", TimeSpan.FromMinutes(5));

            if (EphemeralTurnCredentials.Validate(attackerSecret, user, pass))
                throw new Exception("Credential issued with one secret must not validate with another");

            return Task.CompletedTask;
        }

        [TestMethod]
        public Task Ephemeral_Validate_RejectsExpiredCredential()
        {
            const string secret = "expired-test-shared-secret-32-bytes-ok-";

            // Hand-build an already-expired credential (expiry = 1 second ago).
            var pastExpiry = DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeSeconds();
            var username = $"{pastExpiry}:alice";
            // Password valid for this username/secret (would validate if not expired).
            using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(secret));
            var pass = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(username)));

            if (EphemeralTurnCredentials.Validate(secret, username, pass))
                throw new Exception("Expired credential must be rejected regardless of HMAC correctness");

            return Task.CompletedTask;
        }

        [TestMethod]
        public Task Ephemeral_ResolveLongTermKey_ReturnsSameKeyAsClientComputes()
        {
            // The server's resolver and the client's ToByteBuffer both compute
            // `MD5(username:realm:password)` - proves they compute byte-identical keys.
            const string secret = "resolve-test-shared-secret-32-bytes-ok-";
            const string realm = "spawndev-rtc-test";
            var (username, password) = EphemeralTurnCredentials.Generate(secret, "alice", TimeSpan.FromMinutes(5));

            var serverKey = EphemeralTurnCredentials.ResolveLongTermKey(secret, realm, username);
            if (serverKey == null) throw new Exception("Server resolver returned null for a fresh credential");

            var clientKey = MD5.HashData(Encoding.UTF8.GetBytes($"{username}:{realm}:{password}"));

            if (!serverKey.SequenceEqual(clientKey))
                throw new Exception($"Server-derived key {Convert.ToHexString(serverKey)} != client-derived key {Convert.ToHexString(clientKey)}");

            return Task.CompletedTask;
        }

        [TestMethod]
        public Task Ephemeral_ResolveLongTermKey_RejectsExpired()
        {
            const string secret = "resolve-expired-secret-32-bytes-padding";
            const string realm = "spawndev-rtc-test";

            var pastExpiry = DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeSeconds();
            var username = $"{pastExpiry}:alice";

            var key = EphemeralTurnCredentials.ResolveLongTermKey(secret, realm, username);
            if (key != null) throw new Exception("Resolver should return null for expired username");

            return Task.CompletedTask;
        }

        // --- Period-rotating secret resolver -----------------------------------

        [TestMethod]
        public Task PeriodRotating_GenerateAndResolve_RoundTrip()
        {
            const string master = "master-secret-for-period-rotation-padding";
            const string realm = "spawndev-rtc-test";
            const int periodSeconds = 3600;

            var (user, pass) = EphemeralTurnCredentials.GeneratePeriodic(
                master, "alice", TimeSpan.FromMinutes(5), periodSeconds);

            var resolver = EphemeralTurnCredentials.PeriodRotatingResolver(master, realm, periodSeconds);
            var serverKey = resolver(user);
            if (serverKey == null) throw new Exception("Period-rotating resolver returned null for fresh credential");

            // Client computes the same key from username+realm+password.
            var clientKey = MD5.HashData(Encoding.UTF8.GetBytes($"{user}:{realm}:{pass}"));
            if (!serverKey.SequenceEqual(clientKey))
                throw new Exception($"server={Convert.ToHexString(serverKey)} != client={Convert.ToHexString(clientKey)} (period-rotating keys diverged)");

            return Task.CompletedTask;
        }

        [TestMethod]
        public Task PeriodRotating_RejectsExpired()
        {
            const string master = "master-secret-for-expired-period-pad-pad";
            const string realm = "spawndev-rtc-test";
            const int periodSeconds = 3600;

            // Hand-craft a credential with past expiry, using the correct sub-secret it would have had.
            var pastExpiry = DateTimeOffset.UtcNow.AddSeconds(-30).ToUnixTimeSeconds();
            var username = $"{pastExpiry}:alice";

            var resolver = EphemeralTurnCredentials.PeriodRotatingResolver(master, realm, periodSeconds);
            var key = resolver(username);
            if (key != null) throw new Exception("Period-rotating resolver must reject expired credentials");

            return Task.CompletedTask;
        }

        [TestMethod]
        public Task PeriodRotating_RejectsWrongMasterSecret()
        {
            const string issuingMaster = "issuing-master-secret-for-period-pad-ok";
            const string attackerMaster = "attacker-master-secret-different-pad-ok";
            const string realm = "spawndev-rtc-test";
            const int periodSeconds = 3600;

            var (user, pass) = EphemeralTurnCredentials.GeneratePeriodic(
                issuingMaster, "alice", TimeSpan.FromMinutes(5), periodSeconds);

            var resolver = EphemeralTurnCredentials.PeriodRotatingResolver(attackerMaster, realm, periodSeconds);
            var serverKey = resolver(user);
            if (serverKey == null)
            {
                // Good path: server derived a different sub-secret and reported null... but
                // actually the resolver returns null only on malformed/expired. For wrong
                // master it returns a key that just doesn't match MESSAGE-INTEGRITY. So
                // the proof is: client-computed key differs from server-computed key.
                return Task.CompletedTask;
            }

            var clientKey = MD5.HashData(Encoding.UTF8.GetBytes($"{user}:{realm}:{pass}"));
            if (serverKey.SequenceEqual(clientKey))
                throw new Exception("Credential issued with one master secret must not produce a matching key under another master secret");

            return Task.CompletedTask;
        }

        // --- Tracker-gated resolver --------------------------------------------

        [TestMethod]
        public Task TrackerGated_Resolver_RejectsWhenPeerNotConnected()
        {
            const string secret = "tracker-gated-secret-32-bytes-padding-ok";
            const string realm = "spawndev-rtc-test";
            var tracker = new TrackerSignalingServer();
            var resolver = EphemeralTurnCredentials.TrackerGatedResolver(secret, realm, tracker);

            // Generate a fresh ephemeral credential for "alice". The tracker has no peers,
            // so the resolver should return null even though the HMAC itself is valid.
            var (user, _) = EphemeralTurnCredentials.Generate(secret, "alice", TimeSpan.FromMinutes(5));
            var key = resolver(user);
            if (key != null) throw new Exception("Tracker-gated resolver must reject when userId is not announced to the tracker");

            return Task.CompletedTask;
        }

        [TestMethod]
        public Task TrackerGated_Resolver_RejectsMalformedUsername()
        {
            const string secret = "tracker-gated-malformed-test-padding-byt";
            const string realm = "spawndev-rtc-test";
            var tracker = new TrackerSignalingServer();
            var resolver = EphemeralTurnCredentials.TrackerGatedResolver(secret, realm, tracker);

            foreach (var bad in new[] { "", "no-colon", ":no-userid-before-colon", "no-expiry-after-colon:", ":" })
            {
                if (resolver(bad) != null)
                    throw new Exception($"Tracker-gated resolver must reject malformed username: '{bad}'");
            }
            return Task.CompletedTask;
        }

        [TestMethod]
        public Task TrackerGated_IsPeerConnected_EmptyTracker_ReturnsFalse()
        {
            var tracker = new TrackerSignalingServer();
            if (tracker.IsPeerConnected("any-peer-id"))
                throw new Exception("Empty tracker should report no peers connected");
            if (tracker.ConnectedPeerIds.Count != 0)
                throw new Exception("Empty tracker should have empty ConnectedPeerIds");
            return Task.CompletedTask;
        }

        // --- Origin allowlist (unit) -------------------------------------------

        [TestMethod]
        public Task OriginAllowlist_ExactMatch_AcceptsAndRejects()
        {
            var list = new[] { "https://app.example.com", "https://hub.spawndev.com" };

            // Accepted.
            if (!TrackerSignalingServer.IsOriginAllowed("https://app.example.com", list))
                throw new Exception("Exact match should be accepted");
            if (!TrackerSignalingServer.IsOriginAllowed("HTTPS://APP.EXAMPLE.COM", list))
                throw new Exception("Exact match should be case-insensitive");

            // Rejected.
            if (TrackerSignalingServer.IsOriginAllowed("https://evil.com", list))
                throw new Exception("Non-matching origin should be rejected");
            if (TrackerSignalingServer.IsOriginAllowed("http://app.example.com", list))
                throw new Exception("Different scheme should be rejected");
            if (TrackerSignalingServer.IsOriginAllowed("", list))
                throw new Exception("Empty origin should be rejected");

            return Task.CompletedTask;
        }

        [TestMethod]
        public Task OriginAllowlist_WildcardSubdomain_MatchesAllSubdomainsButNotBare()
        {
            var list = new[] { "https://*.spawndev.com" };

            if (!TrackerSignalingServer.IsOriginAllowed("https://hub.spawndev.com", list))
                throw new Exception("Subdomain should match wildcard");
            if (!TrackerSignalingServer.IsOriginAllowed("https://deep.sub.spawndev.com", list))
                throw new Exception("Deep subdomain should match wildcard");

            if (TrackerSignalingServer.IsOriginAllowed("https://spawndev.com", list))
                throw new Exception("Wildcard should NOT match bare domain");
            if (TrackerSignalingServer.IsOriginAllowed("https://spawndevzcom.com", list))
                throw new Exception("Wildcard must not false-match on coincidental suffix");

            return Task.CompletedTask;
        }

        [TestMethod]
        public Task OriginAllowlist_EmptyList_IsPurelyAllowlistSemantic()
        {
            // Empty list means "nothing allowed" - it does NOT mean "anything allowed".
            // The "anything allowed" behavior is keyed on AllowedOrigins==null/empty at
            // the call site, which skips the check entirely. Once the list is populated,
            // every origin must match something.
            if (TrackerSignalingServer.IsOriginAllowed("https://anything.example.com", Array.Empty<string>()))
                throw new Exception("IsOriginAllowed must reject with an empty allowlist");
            return Task.CompletedTask;
        }

        // --- StunTurnServerHostedService DI lifecycle --------------------------

        [TestMethod]
        public async Task HostedService_DisabledByDefault_DoesNotOpenListener()
        {
            var port = GetFreeUdpPort();
            using var host = BuildHost(opts =>
            {
                // Don't set Enabled => default false. Port is irrelevant, but set
                // it anyway so if the listener accidentally binds we'd see it
                // bound to our sentinel port.
                opts.Port = port;
                opts.ListenAddress = IPAddress.Loopback;
            });

            await host.StartAsync();
            try
            {
                // Port should be free even though the hosted service is running.
                using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
                probe.Close();
            }
            finally
            {
                await host.StopAsync();
            }
        }

        [TestMethod]
        public async Task HostedService_Enabled_BindingRequest_GetsSuccessResponse()
        {
            var port = GetFreeUdpPort();
            using var host = BuildHost(opts =>
            {
                opts.Enabled = true;
                opts.Port = port;
                opts.ListenAddress = IPAddress.Loopback;
                opts.EnableUdp = true;
                opts.EnableTcp = false;
                opts.RelayAddress = IPAddress.Loopback;
                opts.Username = "test-user";
                opts.Password = "test-pass";
                opts.Realm = "spawndev-rtc-test";
            });

            await host.StartAsync();
            try
            {
                await Task.Delay(150); // listener bind settle

                var mapped = await SendBindingRequestAsync(IPAddress.Loopback, port, TimeSpan.FromSeconds(3));
                if (mapped == null)
                    throw new Exception("No STUN binding response from hosted service within 3s");
                if (!mapped.Address.Equals(IPAddress.Loopback))
                    throw new Exception($"Expected mapped address to be loopback, got {mapped.Address}");
            }
            finally
            {
                await host.StopAsync();
            }
        }

        // --- TURN Allocate round-trip: classic long-term credentials -----------

        [TestMethod]
        public async Task HostedService_LongTermAuth_AllocateReturnsRelayAddress()
        {
            var port = GetFreeUdpPort();
            const string username = "alice";
            const string password = "s3cret";
            const string realm = "spawndev-rtc-test";

            using var host = BuildHost(opts =>
            {
                opts.Enabled = true;
                opts.Port = port;
                opts.ListenAddress = IPAddress.Loopback;
                opts.EnableUdp = true;
                opts.EnableTcp = false;
                opts.RelayAddress = IPAddress.Loopback;
                opts.Username = username;
                opts.Password = password;
                opts.Realm = realm;
            });

            await host.StartAsync();
            try
            {
                await Task.Delay(150);
                var relay = await DoTurnAllocateAsync(IPAddress.Loopback, port, username, realm, password);
                if (relay == null) throw new Exception("TURN Allocate did not complete with long-term credentials");
                if (relay.Port <= 0) throw new Exception($"Relay port {relay.Port} invalid");
            }
            finally
            {
                await host.StopAsync();
            }
        }

        // --- TURN Allocate round-trip: ephemeral credentials via ResolveHmacKey -

        [TestMethod]
        public async Task HostedService_EphemeralAuth_AllocateSucceedsWithGeneratedCreds()
        {
            var port = GetFreeUdpPort();
            const string sharedSecret = "ephemeral-e2e-secret-32-bytes-padding-ok";
            const string realm = "spawndev-rtc-test";

            using var host = BuildHost(opts =>
            {
                opts.Enabled = true;
                opts.Port = port;
                opts.ListenAddress = IPAddress.Loopback;
                opts.EnableUdp = true;
                opts.EnableTcp = false;
                opts.RelayAddress = IPAddress.Loopback;
                opts.Realm = realm;
                opts.EphemeralCredentialSharedSecret = sharedSecret;
            });

            await host.StartAsync();
            try
            {
                await Task.Delay(150);

                var (ephemeralUser, ephemeralPass) = EphemeralTurnCredentials.Generate(
                    sharedSecret, "alice", TimeSpan.FromMinutes(5));
                var relay = await DoTurnAllocateAsync(IPAddress.Loopback, port, ephemeralUser, realm, ephemeralPass);
                if (relay == null) throw new Exception("TURN Allocate did not complete with ephemeral credentials");
                if (relay.Port <= 0) throw new Exception($"Relay port {relay.Port} invalid");
            }
            finally
            {
                await host.StopAsync();
            }
        }

        [TestMethod]
        public async Task HostedService_EphemeralAuth_AllocateRejectsExpiredCreds()
        {
            var port = GetFreeUdpPort();
            const string sharedSecret = "ephemeral-expired-secret-32-bytes-paddin";
            const string realm = "spawndev-rtc-test";

            using var host = BuildHost(opts =>
            {
                opts.Enabled = true;
                opts.Port = port;
                opts.ListenAddress = IPAddress.Loopback;
                opts.EnableUdp = true;
                opts.EnableTcp = false;
                opts.RelayAddress = IPAddress.Loopback;
                opts.Realm = realm;
                opts.EphemeralCredentialSharedSecret = sharedSecret;
            });

            await host.StartAsync();
            try
            {
                await Task.Delay(150);

                // Hand-build an expired credential (expiry = 1 second ago) with a valid HMAC,
                // then confirm the server rejects it.
                var pastExpiry = DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeSeconds();
                var expiredUser = $"{pastExpiry}:alice";
                using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(sharedSecret));
                var expiredPass = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(expiredUser)));

                var relay = await DoTurnAllocateAsync(IPAddress.Loopback, port, expiredUser, realm, expiredPass);
                if (relay != null)
                    throw new Exception("Expired credential should have been rejected, but Allocate returned a relay address");
            }
            finally
            {
                await host.StopAsync();
            }
        }

        // --- Origin allowlist end-to-end via real WebSocket upgrade ------------

        [TestMethod]
        public async Task OriginAllowlist_E2E_AcceptsListedRejectsOthers()
        {
            // Real ASP.NET Core host, real WebSocket client, real Origin header.
            // Proves the allowlist short-circuits upgrade with HTTP 403 before
            // reaching HandleWebSocketAsync.
            var httpPort = GetFreeTcpPort();
            TrackerSignalingServer? tracker = null;
            using var app = BuildSignalingApp(httpPort,
                new[] { "https://app.example.com", "https://*.spawndev.com" },
                t => tracker = t);
            await app.StartAsync();
            try
            {
                // Allowed exact origin.
                using (var ws = new System.Net.WebSockets.ClientWebSocket())
                {
                    ws.Options.SetRequestHeader("Origin", "https://app.example.com");
                    await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{httpPort}/announce"), default);
                    if (ws.State != System.Net.WebSockets.WebSocketState.Open)
                        throw new Exception($"Expected Open for allowed Origin, got {ws.State}");
                    await ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "", default);
                }

                // Allowed wildcard subdomain.
                using (var ws = new System.Net.WebSockets.ClientWebSocket())
                {
                    ws.Options.SetRequestHeader("Origin", "https://hub.spawndev.com");
                    await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{httpPort}/announce"), default);
                    if (ws.State != System.Net.WebSockets.WebSocketState.Open)
                        throw new Exception($"Expected Open for wildcard-matched Origin, got {ws.State}");
                    await ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "", default);
                }

                // Disallowed origin - expect handshake exception.
                using (var ws = new System.Net.WebSockets.ClientWebSocket())
                {
                    ws.Options.SetRequestHeader("Origin", "https://evil.example.org");
                    var threw = false;
                    try
                    {
                        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{httpPort}/announce"), default);
                    }
                    catch (System.Net.WebSockets.WebSocketException) { threw = true; }
                    if (!threw)
                        throw new Exception("Disallowed Origin should have been rejected by 403 before upgrade");
                }
            }
            finally
            {
                await app.StopAsync();
            }
        }

        // --- Tracker-gated TURN Allocate end-to-end ----------------------------

        [TestMethod]
        public async Task TrackerGated_E2E_AllocateRequiresActiveTrackerSession()
        {
            // Real tracker + real TURN server on the same host. A peer must
            // announce via WebSocket before its ephemeral TURN credential is
            // accepted. Disconnecting the WebSocket flips the gate shut.
            var httpPort = GetFreeTcpPort();
            var turnPort = GetFreeUdpPort();
            const string sharedSecret = "tracker-gated-e2e-secret-32-bytes-ok-oko";
            const string realm = "spawndev-rtc-test";
            const string peerId = "alice-peer-id-32-bytes-wirehex-padded!";

            TrackerSignalingServer tracker = null!;
            using var app = BuildSignalingApp(httpPort, allowedOrigins: null, t => tracker = t);
            await app.StartAsync();

            // Start the TURN server with a tracker-gated resolver.
            var turnCfg = new TurnServerConfig
            {
                ListenAddress = IPAddress.Loopback,
                Port = turnPort,
                EnableUdp = true,
                EnableTcp = false,
                RelayAddress = IPAddress.Loopback,
                Realm = realm,
                ResolveHmacKey = EphemeralTurnCredentials.TrackerGatedResolver(sharedSecret, realm, tracker),
            };
            using var turn = new TurnServer(turnCfg);
            turn.Start();

            try
            {
                await Task.Delay(150);

                // Step 1: Allocate BEFORE announcing - tracker gate is closed, resolver
                // returns null, server responds 401 to the authed message. Our helper
                // returns null on rejected Allocate.
                var (user1, pass1) = EphemeralTurnCredentials.Generate(sharedSecret, peerId, TimeSpan.FromMinutes(5));
                var relayBefore = await DoTurnAllocateAsync(IPAddress.Loopback, turnPort, user1, realm, pass1);
                if (relayBefore != null)
                    throw new Exception("TURN Allocate must be rejected when peer has not announced to the tracker");

                // Step 2: Connect WebSocket + announce.
                using var ws = new System.Net.WebSockets.ClientWebSocket();
                await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{httpPort}/announce"), default);
                // Announce message - room/info_hash can be anything; the tracker learns the peerId on announce.
                var announce = "{\"action\":\"announce\",\"info_hash\":\"xxxxxxxxxxxxxxxxxxxx\",\"peer_id\":\"" + peerId + "\",\"numwant\":0}";
                var buf = Encoding.UTF8.GetBytes(announce);
                await ws.SendAsync(buf, System.Net.WebSockets.WebSocketMessageType.Text, true, default);

                // Wait for the tracker to process + register the peer.
                for (int i = 0; i < 50; i++)
                {
                    if (tracker.IsPeerConnected(peerId)) break;
                    await Task.Delay(20);
                }
                if (!tracker.IsPeerConnected(peerId))
                    throw new Exception("Tracker never registered the announced peer");

                // Step 3: Allocate WHILE announced - should succeed.
                var (user2, pass2) = EphemeralTurnCredentials.Generate(sharedSecret, peerId, TimeSpan.FromMinutes(5));
                var relayWhile = await DoTurnAllocateAsync(IPAddress.Loopback, turnPort, user2, realm, pass2);
                if (relayWhile == null)
                    throw new Exception("TURN Allocate must succeed while peer is announced to the tracker");

                // Step 4: Disconnect the WebSocket; tracker cleans up; gate re-closes.
                await ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "", default);
                ws.Dispose();
                for (int i = 0; i < 50; i++)
                {
                    if (!tracker.IsPeerConnected(peerId)) break;
                    await Task.Delay(20);
                }
                if (tracker.IsPeerConnected(peerId))
                    throw new Exception("Tracker should have dropped the peer after WebSocket close");

                // Step 5: Allocate AFTER disconnect - should be rejected again.
                var (user3, pass3) = EphemeralTurnCredentials.Generate(sharedSecret, peerId, TimeSpan.FromMinutes(5));
                var relayAfter = await DoTurnAllocateAsync(IPAddress.Loopback, turnPort, user3, realm, pass3);
                if (relayAfter != null)
                    throw new Exception("TURN Allocate must be rejected after peer disconnects from the tracker");
            }
            finally
            {
                turn.Stop();
                await app.StopAsync();
            }
        }

        // --- TURN end-to-end relay forwarding ----------------------------------

        [TestMethod(Timeout = 15000)]
        public async Task TurnRelay_E2E_SendIndicationForwardsDataToRawPeer()
        {
            // Full RFC 5766 §10 round-trip through the TURN server:
            //   1. Client A allocates a relay on the TURN server (gets relay endpoint).
            //   2. Client A creates permission for raw peer B's address.
            //   3. Client A sends a SendIndication containing XORPeerAddress=B + Data=payload.
            //   4. TURN server's HandleSendIndication forwards the payload from A's
            //      relay-socket to B's real UDP endpoint (the actual wire relay).
            //   5. Raw peer B (not a TURN client - just a UdpClient) receives the payload.
            //
            // Proves the relay-forward path works end-to-end, not just Allocate.
            // Closes the Plans/PLAN-Full-WebRTC-Coverage.md "TURN relay E2E test" gap.

            var controlPort = GetFreeUdpPort();
            const string realm = "spawndev-rtc-test";
            const string user = "alice";
            const string pass = "s3cret";

            var turnCfg = new TurnServerConfig
            {
                ListenAddress = IPAddress.Loopback,
                Port = controlPort,
                EnableUdp = true,
                EnableTcp = false,
                RelayAddress = IPAddress.Loopback,
                Username = user,
                Password = pass,
                Realm = realm,
            };

            using var turn = new TurnServer(turnCfg);
            turn.Start();
            try
            {
                await Task.Delay(150);

                // Raw peer B: bare UdpClient, NOT a TURN client. Listens for the relayed bytes.
                using var peerB = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                var peerBEndpoint = (IPEndPoint)peerB.Client.LocalEndPoint!;

                // Client A: a real TURN client with its own allocation.
                using var clientA = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                var turnEp = new IPEndPoint(IPAddress.Loopback, controlPort);

                // --- Step 1: Allocate via standard 401-challenge-then-authed sequence ---
                var allocateAuthState = await AllocateAndCaptureAuthState(clientA, turnEp, user, realm, pass);
                if (allocateAuthState == null)
                    throw new Exception("TURN Allocate did not complete");

                // --- Step 2: CreatePermission for peerB's endpoint ---
                var cpOk = await CreatePermissionAsync(clientA, turnEp, user, realm, pass,
                    allocateAuthState.Value.Nonce, allocateAuthState.Value.RealmBytes, peerBEndpoint);
                if (!cpOk) throw new Exception("CreatePermission request failed");

                // --- Step 3: SendIndication with payload targeted at peerB ---
                var payload = Encoding.UTF8.GetBytes("turn-e2e-test-payload-" + Guid.NewGuid().ToString("N"));
                await SendSendIndicationAsync(clientA, turnEp, peerBEndpoint, payload);

                // --- Step 4: Raw peerB should receive the payload via the TURN relay socket ---
                using var recvCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var result = await peerB.ReceiveAsync(recvCts.Token);
                if (!result.Buffer.AsSpan().SequenceEqual(payload))
                    throw new Exception(
                        $"Payload mismatch: sent {payload.Length} bytes, received {result.Buffer.Length} bytes. " +
                        "The TURN relay-forward path did not forward the exact bytes we sent.");
            }
            finally
            {
                turn.Stop();
            }
        }

        /// <summary>
        /// Allocate round-trip + capture the NONCE + REALM bytes the server gave us.
        /// Subsequent requests (CreatePermission, SendIndication if MESSAGE-INTEGRITY'd)
        /// need to echo the same realm + nonce back.
        /// </summary>
        private static async Task<(byte[] Nonce, byte[] RealmBytes)?> AllocateAndCaptureAuthState(
            UdpClient udp, IPEndPoint turn, string username, string realm, string password)
        {
            // Unauth Allocate -> expect 401 with REALM + NONCE
            var txId = new byte[12];
            RandomNumberGenerator.Fill(txId);
            var req = new SIPSorcery.Net.STUNMessage(SIPSorcery.Net.STUNMessageTypesEnum.Allocate);
            req.Header.TransactionId = txId;
            req.Attributes.Add(new SIPSorcery.Net.STUNAttribute(
                SIPSorcery.Net.STUNAttributeTypesEnum.RequestedTransport, new byte[] { 17, 0, 0, 0 }));
            var bytes = req.ToByteBuffer(null, false);
            await udp.SendAsync(bytes, bytes.Length, turn);

            using var cts1 = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var challenge = await udp.ReceiveAsync(cts1.Token);
            var challengeMsg = SIPSorcery.Net.STUNMessage.ParseSTUNMessage(challenge.Buffer, challenge.Buffer.Length);
            if (challengeMsg == null) return null;

            var realmAttr = challengeMsg.Attributes.FirstOrDefault(a => a.AttributeType == SIPSorcery.Net.STUNAttributeTypesEnum.Realm);
            var nonceAttr = challengeMsg.Attributes.FirstOrDefault(a => a.AttributeType == SIPSorcery.Net.STUNAttributeTypesEnum.Nonce);
            if (realmAttr?.Value == null || nonceAttr?.Value == null) return null;

            // Authed Allocate
            var txId2 = new byte[12];
            RandomNumberGenerator.Fill(txId2);
            var req2 = new SIPSorcery.Net.STUNMessage(SIPSorcery.Net.STUNMessageTypesEnum.Allocate);
            req2.Header.TransactionId = txId2;
            req2.Attributes.Add(new SIPSorcery.Net.STUNAttribute(
                SIPSorcery.Net.STUNAttributeTypesEnum.RequestedTransport, new byte[] { 17, 0, 0, 0 }));
            req2.Attributes.Add(new SIPSorcery.Net.STUNAttribute(
                SIPSorcery.Net.STUNAttributeTypesEnum.Username, Encoding.UTF8.GetBytes(username)));
            req2.Attributes.Add(new SIPSorcery.Net.STUNAttribute(SIPSorcery.Net.STUNAttributeTypesEnum.Realm, realmAttr.Value));
            req2.Attributes.Add(new SIPSorcery.Net.STUNAttribute(SIPSorcery.Net.STUNAttributeTypesEnum.Nonce, nonceAttr.Value));

            var key = MD5.HashData(Encoding.UTF8.GetBytes($"{username}:{realm}:{password}"));
            var authBytes = req2.ToByteBuffer(key, false);
            await udp.SendAsync(authBytes, authBytes.Length, turn);

            using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var success = await udp.ReceiveAsync(cts2.Token);
            var successMsg = SIPSorcery.Net.STUNMessage.ParseSTUNMessage(success.Buffer, success.Buffer.Length);
            if (successMsg == null) return null;
            if (successMsg.Header.MessageType != SIPSorcery.Net.STUNMessageTypesEnum.AllocateSuccessResponse) return null;

            return (nonceAttr.Value, realmAttr.Value);
        }

        /// <summary>CreatePermission for a specific peer endpoint.</summary>
        private static async Task<bool> CreatePermissionAsync(
            UdpClient udp, IPEndPoint turn, string username, string realm, string password,
            byte[] nonce, byte[] realmBytes, IPEndPoint peerEp)
        {
            var txId = new byte[12];
            RandomNumberGenerator.Fill(txId);
            var req = new SIPSorcery.Net.STUNMessage(SIPSorcery.Net.STUNMessageTypesEnum.CreatePermission);
            req.Header.TransactionId = txId;
            req.Attributes.Add(new SIPSorcery.Net.STUNXORAddressAttribute(
                SIPSorcery.Net.STUNAttributeTypesEnum.XORPeerAddress,
                peerEp.Port, peerEp.Address, txId));
            req.Attributes.Add(new SIPSorcery.Net.STUNAttribute(
                SIPSorcery.Net.STUNAttributeTypesEnum.Username, Encoding.UTF8.GetBytes(username)));
            req.Attributes.Add(new SIPSorcery.Net.STUNAttribute(SIPSorcery.Net.STUNAttributeTypesEnum.Realm, realmBytes));
            req.Attributes.Add(new SIPSorcery.Net.STUNAttribute(SIPSorcery.Net.STUNAttributeTypesEnum.Nonce, nonce));

            var key = MD5.HashData(Encoding.UTF8.GetBytes($"{username}:{realm}:{password}"));
            var bytes = req.ToByteBuffer(key, false);
            await udp.SendAsync(bytes, bytes.Length, turn);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var resp = await udp.ReceiveAsync(cts.Token);
            var msg = SIPSorcery.Net.STUNMessage.ParseSTUNMessage(resp.Buffer, resp.Buffer.Length);
            return msg?.Header.MessageType == SIPSorcery.Net.STUNMessageTypesEnum.CreatePermissionSuccessResponse;
        }

        [TestMethod(Timeout = 15000)]
        public async Task TurnRelay_E2E_DataIndicationDeliversPeerPayloadToClient()
        {
            // Reverse direction of the forward relay test:
            //   1. Client A allocates a relay on the TURN server (gets XOR-RELAYED-ADDRESS).
            //   2. Client A CreatePermission for raw peer B's address (so the TURN server
            //      will accept inbound UDP from B).
            //   3. Raw peer B (a bare UdpClient) sends payload UDP -> A's relay endpoint.
            //   4. TURN server's RelayUdpToClientAsync receives on A's relay socket and
            //      wraps the payload in a DataIndication.
            //   5. Client A receives the DataIndication and extracts the original bytes.
            //
            // Proves the incoming-relay path works end-to-end (peer -> TURN server -> client).
            // Together with TurnRelay_E2E_SendIndicationForwardsDataToRawPeer this covers
            // the full bidirectional RFC 5766 §10 data path.

            var controlPort = GetFreeUdpPort();
            const string realm = "spawndev-rtc-test";
            const string user = "alice";
            const string pass = "s3cret";

            var turnCfg = new TurnServerConfig
            {
                ListenAddress = IPAddress.Loopback,
                Port = controlPort,
                EnableUdp = true,
                EnableTcp = false,
                RelayAddress = IPAddress.Loopback,
                Username = user,
                Password = pass,
                Realm = realm,
            };

            using var turn = new TurnServer(turnCfg);
            turn.Start();
            try
            {
                await Task.Delay(150);

                using var peerB = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                var peerBEndpoint = (IPEndPoint)peerB.Client.LocalEndPoint!;

                using var clientA = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                var turnEp = new IPEndPoint(IPAddress.Loopback, controlPort);

                // Allocate + capture relayed-address so peerB knows where to send.
                var (authState, relayEp) = await AllocateAndGetRelayAddressAsync(clientA, turnEp, user, realm, pass);
                if (authState == null || relayEp == null)
                    throw new Exception("TURN Allocate did not return a relay endpoint");

                // CreatePermission for peerB so the TURN server will accept + forward
                // UDP arriving from that address.
                var cpOk = await CreatePermissionAsync(clientA, turnEp, user, realm, pass,
                    authState.Value.Nonce, authState.Value.RealmBytes, peerBEndpoint);
                if (!cpOk) throw new Exception("CreatePermission request failed");

                // Peer B sends a raw UDP packet directly to A's relay address.
                var payload = Encoding.UTF8.GetBytes("turn-reverse-test-" + Guid.NewGuid().ToString("N"));
                await peerB.SendAsync(payload, payload.Length, relayEp);

                // Client A should now receive a DataIndication wrapping that payload.
                using var recvCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                while (true)
                {
                    var result = await clientA.ReceiveAsync(recvCts.Token);
                    var msg = SIPSorcery.Net.STUNMessage.ParseSTUNMessage(result.Buffer, result.Buffer.Length);
                    if (msg == null) continue;
                    if (msg.Header.MessageType != SIPSorcery.Net.STUNMessageTypesEnum.DataIndication) continue;

                    var dataAttr = msg.Attributes.FirstOrDefault(a => a.AttributeType == SIPSorcery.Net.STUNAttributeTypesEnum.Data);
                    if (dataAttr?.Value == null)
                        throw new Exception("DataIndication arrived but had no Data attribute");
                    if (!dataAttr.Value.AsSpan().SequenceEqual(payload))
                        throw new Exception(
                            $"Payload mismatch: peerB sent {payload.Length} bytes, A received {dataAttr.Value.Length} bytes via DataIndication. " +
                            "The TURN peer-to-client relay path corrupted or replaced the payload.");
                    return; // success
                }
            }
            finally
            {
                turn.Stop();
            }
        }

        /// <summary>
        /// Allocate variant that also returns the XOR-RELAYED-ADDRESS endpoint from
        /// the success response - needed for the reverse-direction relay test
        /// where a peer sends data TO the client's relay endpoint.
        /// </summary>
        private static async Task<((byte[] Nonce, byte[] RealmBytes)? Auth, IPEndPoint? Relay)> AllocateAndGetRelayAddressAsync(
            UdpClient udp, IPEndPoint turn, string username, string realm, string password)
        {
            var txId = new byte[12];
            RandomNumberGenerator.Fill(txId);
            var req = new SIPSorcery.Net.STUNMessage(SIPSorcery.Net.STUNMessageTypesEnum.Allocate);
            req.Header.TransactionId = txId;
            req.Attributes.Add(new SIPSorcery.Net.STUNAttribute(
                SIPSorcery.Net.STUNAttributeTypesEnum.RequestedTransport, new byte[] { 17, 0, 0, 0 }));
            var bytes = req.ToByteBuffer(null, false);
            await udp.SendAsync(bytes, bytes.Length, turn);

            using var cts1 = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var challenge = await udp.ReceiveAsync(cts1.Token);
            var challengeMsg = SIPSorcery.Net.STUNMessage.ParseSTUNMessage(challenge.Buffer, challenge.Buffer.Length);
            if (challengeMsg == null) return (null, null);

            var realmAttr = challengeMsg.Attributes.FirstOrDefault(a => a.AttributeType == SIPSorcery.Net.STUNAttributeTypesEnum.Realm);
            var nonceAttr = challengeMsg.Attributes.FirstOrDefault(a => a.AttributeType == SIPSorcery.Net.STUNAttributeTypesEnum.Nonce);
            if (realmAttr?.Value == null || nonceAttr?.Value == null) return (null, null);

            var txId2 = new byte[12];
            RandomNumberGenerator.Fill(txId2);
            var req2 = new SIPSorcery.Net.STUNMessage(SIPSorcery.Net.STUNMessageTypesEnum.Allocate);
            req2.Header.TransactionId = txId2;
            req2.Attributes.Add(new SIPSorcery.Net.STUNAttribute(
                SIPSorcery.Net.STUNAttributeTypesEnum.RequestedTransport, new byte[] { 17, 0, 0, 0 }));
            req2.Attributes.Add(new SIPSorcery.Net.STUNAttribute(
                SIPSorcery.Net.STUNAttributeTypesEnum.Username, Encoding.UTF8.GetBytes(username)));
            req2.Attributes.Add(new SIPSorcery.Net.STUNAttribute(SIPSorcery.Net.STUNAttributeTypesEnum.Realm, realmAttr.Value));
            req2.Attributes.Add(new SIPSorcery.Net.STUNAttribute(SIPSorcery.Net.STUNAttributeTypesEnum.Nonce, nonceAttr.Value));

            var key = MD5.HashData(Encoding.UTF8.GetBytes($"{username}:{realm}:{password}"));
            var authBytes = req2.ToByteBuffer(key, false);
            await udp.SendAsync(authBytes, authBytes.Length, turn);

            using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var success = await udp.ReceiveAsync(cts2.Token);
            var successMsg = SIPSorcery.Net.STUNMessage.ParseSTUNMessage(success.Buffer, success.Buffer.Length);
            if (successMsg?.Header.MessageType != SIPSorcery.Net.STUNMessageTypesEnum.AllocateSuccessResponse)
                return (null, null);

            IPEndPoint? relayEp = null;
            foreach (var attr in successMsg.Attributes)
            {
                if (attr.AttributeType != SIPSorcery.Net.STUNAttributeTypesEnum.XORRelayedAddress) continue;
                if (attr.Value == null || attr.Value.Length < 8) continue;
                int xorPort = ((attr.Value[2] << 8) | attr.Value[3]) ^ 0x2112;
                int a0 = attr.Value[4] ^ 0x21;
                int a1 = attr.Value[5] ^ 0x12;
                int a2 = attr.Value[6] ^ 0xA4;
                int a3 = attr.Value[7] ^ 0x42;
                relayEp = new IPEndPoint(new IPAddress(new[] { (byte)a0, (byte)a1, (byte)a2, (byte)a3 }), xorPort);
                break;
            }

            return ((nonceAttr.Value, realmAttr.Value), relayEp);
        }

        /// <summary>
        /// SendIndication: tell the TURN server to forward a payload to the peer.
        /// Indications are unauth per RFC 5766 §10 (MESSAGE-INTEGRITY is not required).
        /// </summary>
        private static async Task SendSendIndicationAsync(
            UdpClient udp, IPEndPoint turn, IPEndPoint peerEp, byte[] payload)
        {
            var txId = new byte[12];
            RandomNumberGenerator.Fill(txId);
            var msg = new SIPSorcery.Net.STUNMessage(SIPSorcery.Net.STUNMessageTypesEnum.SendIndication);
            msg.Header.TransactionId = txId;
            msg.Attributes.Add(new SIPSorcery.Net.STUNXORAddressAttribute(
                SIPSorcery.Net.STUNAttributeTypesEnum.XORPeerAddress,
                peerEp.Port, peerEp.Address, txId));
            msg.Attributes.Add(new SIPSorcery.Net.STUNAttribute(
                SIPSorcery.Net.STUNAttributeTypesEnum.Data, payload));

            var bytes = msg.ToByteBuffer(null, false);
            await udp.SendAsync(bytes, bytes.Length, turn);
        }

        // --- Relay port range (NAT port-forwarding support) --------------------

        [TestMethod]
        public async Task RelayPortRange_Allocate_BindsWithinRange()
        {
            // Pick a small, unlikely-to-be-in-use relay range and do a real
            // Allocate - the returned XOR-RELAYED-ADDRESS port must fall inside.
            var controlPort = GetFreeUdpPort();
            var rangeStart = GetFreeUdpPort();
            var rangeEnd = rangeStart + 10;
            const string realm = "spawndev-rtc-test";

            var turnCfg = new TurnServerConfig
            {
                ListenAddress = IPAddress.Loopback,
                Port = controlPort,
                EnableUdp = true,
                EnableTcp = false,
                RelayAddress = IPAddress.Loopback,
                Username = "alice",
                Password = "s3cret",
                Realm = realm,
                RelayPortRangeStart = rangeStart,
                RelayPortRangeEnd = rangeEnd,
            };

            using var turn = new TurnServer(turnCfg);
            turn.Start();
            try
            {
                await Task.Delay(150);
                var relay = await DoTurnAllocateAsync(IPAddress.Loopback, controlPort, "alice", realm, "s3cret");
                if (relay == null) throw new Exception("TURN Allocate failed");
                if (relay.Port < rangeStart || relay.Port > rangeEnd)
                    throw new Exception($"Relay port {relay.Port} is outside the configured range {rangeStart}-{rangeEnd}");
            }
            finally
            {
                turn.Stop();
            }
        }

        // --- Helpers ---------------------------------------------------------------

        /// <summary>
        /// Build a minimal ASP.NET Core app hosting <see cref="TrackerSignalingServer"/>
        /// on <paramref name="httpPort"/>. Captures the created server instance via
        /// <paramref name="capture"/> so tests can probe its state.
        /// </summary>
        private static Microsoft.AspNetCore.Builder.WebApplication BuildSignalingApp(
            int httpPort,
            IReadOnlyList<string>? allowedOrigins,
            Action<TrackerSignalingServer> capture)
        {
            var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(o =>
            {
                o.Listen(IPAddress.Loopback, httpPort);
            });
            var app = builder.Build();
            app.UseWebSockets();
            var tracker = app.UseRtcSignaling("/announce", new TrackerServerOptions
            {
                AllowedOrigins = allowedOrigins,
            });
            capture(tracker);
            return app;
        }

        private static int GetFreeTcpPort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }


        private static IHost BuildHost(Action<StunTurnServerOptions> configure)
        {
            return Host.CreateDefaultBuilder()
                .ConfigureLogging(lb => lb.ClearProviders())
                .ConfigureServices(services =>
                {
                    services.AddRtcStunTurn(configure);
                })
                .Build();
        }

        private static int GetFreeUdpPort()
        {
            // Let the kernel pick a free ephemeral port, then immediately release
            // it so the TurnServer can bind. Race window is tiny on loopback.
            using var u = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)u.Client.LocalEndPoint!).Port;
        }

        /// <summary>
        /// Send a minimal STUN Binding Request (RFC 5389, unauthenticated) and return
        /// the XOR-MAPPED-ADDRESS from the response, or null on timeout.
        /// </summary>
        private static async Task<IPEndPoint?> SendBindingRequestAsync(IPAddress host, int port, TimeSpan timeout)
        {
            using var udp = new UdpClient(0, AddressFamily.InterNetwork);
            var targetEp = new IPEndPoint(host, port);

            var req = new byte[20];
            req[0] = 0x00; req[1] = 0x01;               // Binding Request
            req[2] = 0x00; req[3] = 0x00;               // length = 0 (no attrs)
            req[4] = 0x21; req[5] = 0x12; req[6] = 0xA4; req[7] = 0x42; // magic cookie
            RandomNumberGenerator.Fill(req.AsSpan(8, 12)); // transaction ID

            await udp.SendAsync(req, req.Length, targetEp);

            using var cts = new CancellationTokenSource(timeout);
            try
            {
                var result = await udp.ReceiveAsync(cts.Token);
                return ParseXorMappedAddress(result.Buffer);
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
        /// Full TURN Allocate round-trip (RFC 5766 §6):
        /// 1. Send Allocate request with no auth, expect 401 + REALM + NONCE.
        /// 2. Resend with USERNAME + REALM + NONCE + MESSAGE-INTEGRITY signed with
        ///    <c>MD5(username:realm:password)</c>.
        /// 3. On success, parse XOR-RELAYED-ADDRESS.
        /// Returns the relay endpoint on success, null on any failure path.
        /// </summary>
        private static async Task<IPEndPoint?> DoTurnAllocateAsync(
            IPAddress host, int port, string username, string realm, string password)
        {
            using var udp = new UdpClient(0, AddressFamily.InterNetwork);
            var targetEp = new IPEndPoint(host, port);

            // --- Step 1: unauthenticated Allocate, expect 401 challenge ---
            var initialTxId = new byte[12];
            RandomNumberGenerator.Fill(initialTxId);
            var reqMsg = new STUNMessage(STUNMessageTypesEnum.Allocate);
            reqMsg.Header.TransactionId = initialTxId;
            // REQUESTED-TRANSPORT = UDP (17) - mandatory per RFC 5766 §6.1
            reqMsg.Attributes.Add(new STUNAttribute(
                STUNAttributeTypesEnum.RequestedTransport,
                new byte[] { 17, 0, 0, 0 }));
            var initialBytes = reqMsg.ToByteBuffer(null, false);

            await udp.SendAsync(initialBytes, initialBytes.Length, targetEp);

            using var cts1 = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            byte[] challengeBuf;
            try
            {
                var r = await udp.ReceiveAsync(cts1.Token);
                challengeBuf = r.Buffer;
            }
            catch (OperationCanceledException) { return null; }

            var challenge = STUNMessage.ParseSTUNMessage(challengeBuf, challengeBuf.Length);
            if (challenge == null) return null;
            // Expect 401 Unauthorized with REALM + NONCE.
            var realmAttr = challenge.Attributes.FirstOrDefault(a => a.AttributeType == STUNAttributeTypesEnum.Realm);
            var nonceAttr = challenge.Attributes.FirstOrDefault(a => a.AttributeType == STUNAttributeTypesEnum.Nonce);
            if (realmAttr?.Value == null || nonceAttr?.Value == null) return null;

            // --- Step 2: authenticated Allocate ---
            var authTxId = new byte[12];
            RandomNumberGenerator.Fill(authTxId);
            var authMsg = new STUNMessage(STUNMessageTypesEnum.Allocate);
            authMsg.Header.TransactionId = authTxId;
            authMsg.Attributes.Add(new STUNAttribute(
                STUNAttributeTypesEnum.RequestedTransport,
                new byte[] { 17, 0, 0, 0 }));
            authMsg.Attributes.Add(new STUNAttribute(
                STUNAttributeTypesEnum.Username, Encoding.UTF8.GetBytes(username)));
            authMsg.Attributes.Add(new STUNAttribute(STUNAttributeTypesEnum.Realm, realmAttr.Value));
            authMsg.Attributes.Add(new STUNAttribute(STUNAttributeTypesEnum.Nonce, nonceAttr.Value));

            // MESSAGE-INTEGRITY key = MD5(username:realm:password).
            var key = MD5.HashData(Encoding.UTF8.GetBytes($"{username}:{realm}:{password}"));
            var authBytes = authMsg.ToByteBuffer(key, false);

            await udp.SendAsync(authBytes, authBytes.Length, targetEp);

            using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            byte[] finalBuf;
            try
            {
                var r = await udp.ReceiveAsync(cts2.Token);
                finalBuf = r.Buffer;
            }
            catch (OperationCanceledException) { return null; }

            var finalMsg = STUNMessage.ParseSTUNMessage(finalBuf, finalBuf.Length);
            if (finalMsg == null) return null;
            if (finalMsg.Header.MessageType != STUNMessageTypesEnum.AllocateSuccessResponse) return null;

            // Find XOR-RELAYED-ADDRESS (0x0016) and decode.
            foreach (var attr in finalMsg.Attributes)
            {
                if (attr.AttributeType != STUNAttributeTypesEnum.XORRelayedAddress) continue;
                if (attr.Value == null || attr.Value.Length < 8) continue;
                // Family byte[1], port[2..3] XOR cookie, address[4..7] XOR cookie.
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
