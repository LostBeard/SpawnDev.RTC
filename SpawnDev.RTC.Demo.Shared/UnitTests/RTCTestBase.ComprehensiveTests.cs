using SpawnDev.RTC;
using SpawnDev.UnitTesting;

namespace SpawnDev.RTC.Demo.Shared.UnitTests
{
    public abstract partial class RTCTestBase
    {
        /// <summary>
        /// Verify negotiated data channels work (both sides create with same ID, no DCEP).
        /// </summary>
        [TestMethod]
        public async Task DataChannel_Negotiated_MessageExchange()
        {
            var config = new RTCPeerConnectionConfig
            {
                IceServers = new[] { new RTCIceServerConfig { Urls = new[] { "stun:stun.l.google.com:19302" } } }
            };

            using var pc1 = RTCPeerConnectionFactory.Create(config);
            using var pc2 = RTCPeerConnectionFactory.Create(config);

            pc1.OnIceCandidate += c => _ = pc2.AddIceCandidate(c);
            pc2.OnIceCandidate += c => _ = pc1.AddIceCandidate(c);

            // Both sides create negotiated channel with same ID
            using var dc1 = pc1.CreateDataChannel("negotiated-chat", new RTCDataChannelConfig { Negotiated = true, Id = 100 });
            using var dc2 = pc2.CreateDataChannel("negotiated-chat", new RTCDataChannelConfig { Negotiated = true, Id = 100 });

            var offer = await pc1.CreateOffer();
            await pc1.SetLocalDescription(offer);
            await pc2.SetRemoteDescription(offer);
            var answer = await pc2.CreateAnswer();
            await pc2.SetLocalDescription(answer);
            await pc1.SetRemoteDescription(answer);

            // Wait for both to open
            var dc1Open = new TaskCompletionSource<bool>();
            var dc2Open = new TaskCompletionSource<bool>();
            if (dc1.ReadyState == "open") dc1Open.TrySetResult(true);
            else dc1.OnOpen += () => dc1Open.TrySetResult(true);
            if (dc2.ReadyState == "open") dc2Open.TrySetResult(true);
            else dc2.OnOpen += () => dc2Open.TrySetResult(true);
            await Task.WhenAny(Task.WhenAll(dc1Open.Task, dc2Open.Task), Task.Delay(15000));
            if (!dc1Open.Task.IsCompletedSuccessfully) throw new Exception("dc1 didn't open");
            if (!dc2Open.Task.IsCompletedSuccessfully) throw new Exception("dc2 didn't open");

            // Exchange messages
            var msg1 = new TaskCompletionSource<string>();
            var msg2 = new TaskCompletionSource<string>();
            dc2.OnStringMessage += m => msg1.TrySetResult(m);
            dc1.OnStringMessage += m => msg2.TrySetResult(m);

            dc1.Send("from-1");
            dc2.Send("from-2");

            await Task.WhenAny(Task.WhenAll(msg1.Task, msg2.Task), Task.Delay(5000));
            if (!msg1.Task.IsCompletedSuccessfully) throw new Exception("dc2 didn't receive");
            if (!msg2.Task.IsCompletedSuccessfully) throw new Exception("dc1 didn't receive");
            if (await msg1.Task != "from-1") throw new Exception($"dc2 got: {await msg1.Task}");
            if (await msg2.Task != "from-2") throw new Exception($"dc1 got: {await msg2.Task}");
        }

        /// <summary>
        /// Verify SDP offer contains expected fields (session, media, ICE).
        /// </summary>
        [TestMethod]
        public async Task SDP_OfferContent_Verification()
        {
            using var pc = RTCPeerConnectionFactory.Create(new RTCPeerConnectionConfig
            {
                IceServers = new[] { new RTCIceServerConfig { Urls = new[] { "stun:stun.l.google.com:19302" } } }
            });

            pc.CreateDataChannel("sdp-test");
            var offer = await pc.CreateOffer();
            var sdp = offer.Sdp;

            // Session level
            if (!sdp.Contains("v=0")) throw new Exception("Missing v=0");
            if (!sdp.Contains("o=")) throw new Exception("Missing o= (origin)");
            if (!sdp.Contains("s=")) throw new Exception("Missing s= (session name)");

            // Data channel media
            if (!sdp.Contains("m=application")) throw new Exception("Missing m=application");
            if (!sdp.Contains("webrtc-datachannel")) throw new Exception("Missing webrtc-datachannel");
            if (!sdp.Contains("UDP/DTLS/SCTP") && !sdp.Contains("DTLS/SCTP"))
                throw new Exception("Missing SCTP profile");

            // ICE
            if (!sdp.Contains("a=ice-ufrag:")) throw new Exception("Missing ICE ufrag");
            if (!sdp.Contains("a=ice-pwd:")) throw new Exception("Missing ICE pwd");

            // DTLS fingerprint
            if (!sdp.Contains("a=fingerprint:")) throw new Exception("Missing DTLS fingerprint");

            // Setup role
            if (!sdp.Contains("a=setup:")) throw new Exception("Missing setup role");
        }

        /// <summary>
        /// Verify ICE candidates are gathered after setting local description.
        /// </summary>
        [TestMethod]
        public async Task ICE_CandidatesGathered()
        {
            using var pc = RTCPeerConnectionFactory.Create(new RTCPeerConnectionConfig
            {
                IceServers = new[] { new RTCIceServerConfig { Urls = new[] { "stun:stun.l.google.com:19302" } } }
            });

            var candidates = new List<RTCIceCandidateInit>();
            pc.OnIceCandidate += c => candidates.Add(c);

            var gatheringComplete = new TaskCompletionSource<bool>();
            pc.OnIceGatheringStateChange += state =>
            {
                if (state == "complete") gatheringComplete.TrySetResult(true);
            };

            pc.CreateDataChannel("ice-test");
            var offer = await pc.CreateOffer();
            await pc.SetLocalDescription(offer);

            // Wait for gathering to complete (or at least some candidates)
            await Task.WhenAny(gatheringComplete.Task, Task.Delay(10000));

            if (candidates.Count == 0)
                throw new Exception("No ICE candidates gathered");

            // Verify candidate format
            foreach (var c in candidates)
            {
                if (string.IsNullOrEmpty(c.Candidate))
                    throw new Exception("Empty candidate string");
                // Browser prefixes with "candidate:", SipSorcery doesn't - both valid
                if (c.Candidate.Length < 10)
                    throw new Exception($"Candidate too short: {c.Candidate}");
            }
        }

        /// <summary>
        /// Renegotiation: add a data channel AFTER connection is already established.
        /// Browser only - SipSorcery's createDataChannel blocks waiting for DCEP ACK
        /// which exceeds the 30s test runner timeout on desktop.
        /// </summary>
        [TestMethod]
        public async Task Renegotiation_AddChannelAfterConnect()
        {
            if (!OperatingSystem.IsBrowser()) return; // Desktop renegotiation needs SipSorcery fork work
            var config = new RTCPeerConnectionConfig
            {
                IceServers = new[] { new RTCIceServerConfig { Urls = new[] { "stun:stun.l.google.com:19302" } } }
            };

            using var pc1 = RTCPeerConnectionFactory.Create(config);
            using var pc2 = RTCPeerConnectionFactory.Create(config);

            pc1.OnIceCandidate += c => _ = pc2.AddIceCandidate(c);
            pc2.OnIceCandidate += c => _ = pc1.AddIceCandidate(c);

            // First channel and negotiation
            var dc2First = new TaskCompletionSource<IRTCDataChannel>();
            pc2.OnDataChannel += ch =>
            {
                if (ch.Label == "first") dc2First.TrySetResult(ch);
            };

            using var dc1First = pc1.CreateDataChannel("first");

            var offer = await pc1.CreateOffer();
            await pc1.SetLocalDescription(offer);
            await pc2.SetRemoteDescription(offer);
            var answer = await pc2.CreateAnswer();
            await pc2.SetLocalDescription(answer);
            await pc1.SetRemoteDescription(answer);

            // Wait for first channel
            var r1 = await Task.WhenAny(dc2First.Task, Task.Delay(15000));
            if (r1 != dc2First.Task) throw new Exception("First channel timeout");

            // Wait for open
            var firstOpen = new TaskCompletionSource<bool>();
            if (dc1First.ReadyState == "open") firstOpen.TrySetResult(true);
            else dc1First.OnOpen += () => firstOpen.TrySetResult(true);
            await Task.WhenAny(firstOpen.Task, Task.Delay(15000));

            // Verify first channel works
            var firstMsg = new TaskCompletionSource<string>();
            (await dc2First.Task).OnStringMessage += m => firstMsg.TrySetResult(m);
            dc1First.Send("first-message");
            var fm = await Task.WhenAny(firstMsg.Task, Task.Delay(5000));
            if (fm != firstMsg.Task) throw new Exception("First channel message timeout");
            if (await firstMsg.Task != "first-message") throw new Exception($"First msg: {await firstMsg.Task}");

            // Now add a SECOND channel on the already-connected connection
            // This requires renegotiation
            var dc2Second = new TaskCompletionSource<IRTCDataChannel>();
            pc2.OnDataChannel += ch =>
            {
                if (ch.Label == "second") dc2Second.TrySetResult(ch);
            };

            using var dc1Second = pc1.CreateDataChannel("second");

            // Renegotiate
            var offer2 = await pc1.CreateOffer();
            await pc1.SetLocalDescription(offer2);
            await pc2.SetRemoteDescription(offer2);
            var answer2 = await pc2.CreateAnswer();
            await pc2.SetLocalDescription(answer2);
            await pc1.SetRemoteDescription(answer2);

            // Wait for second channel
            var r2 = await Task.WhenAny(dc2Second.Task, Task.Delay(15000));
            if (r2 != dc2Second.Task) throw new Exception("Second channel timeout (renegotiation failed?)");

            // Wait for open and verify
            var secondOpen = new TaskCompletionSource<bool>();
            if (dc1Second.ReadyState == "open") secondOpen.TrySetResult(true);
            else dc1Second.OnOpen += () => secondOpen.TrySetResult(true);
            await Task.WhenAny(secondOpen.Task, Task.Delay(15000));

            var secondMsg = new TaskCompletionSource<string>();
            (await dc2Second.Task).OnStringMessage += m => secondMsg.TrySetResult(m);
            dc1Second.Send("second-message");
            var sm = await Task.WhenAny(secondMsg.Task, Task.Delay(5000));
            if (sm != secondMsg.Task) throw new Exception("Second channel message timeout");
            if (await secondMsg.Task != "second-message") throw new Exception($"Second msg: {await secondMsg.Task}");
        }

        /// <summary>
        /// Verify GetStats returns candidate-pair stats after connection (browser only).
        /// </summary>
        [TestMethod]
        public async Task GetStats_CandidatePair_Browser()
        {
            if (!OperatingSystem.IsBrowser()) return;

            var config = new RTCPeerConnectionConfig
            {
                IceServers = new[] { new RTCIceServerConfig { Urls = new[] { "stun:stun.l.google.com:19302" } } }
            };

            using var pc1 = RTCPeerConnectionFactory.Create(config);
            using var pc2 = RTCPeerConnectionFactory.Create(config);

            pc1.OnIceCandidate += c => _ = pc2.AddIceCandidate(c);
            pc2.OnIceCandidate += c => _ = pc1.AddIceCandidate(c);

            var connected = new TaskCompletionSource<bool>();
            pc1.OnConnectionStateChange += s => { if (s == "connected") connected.TrySetResult(true); };

            pc2.OnDataChannel += _ => { };
            pc1.CreateDataChannel("stats-test");

            var offer = await pc1.CreateOffer();
            await pc1.SetLocalDescription(offer);
            await pc2.SetRemoteDescription(offer);
            var answer = await pc2.CreateAnswer();
            await pc2.SetLocalDescription(answer);
            await pc1.SetRemoteDescription(answer);

            await Task.WhenAny(connected.Task, Task.Delay(15000));

            using var stats = await pc1.GetStats();
            var entries = stats.Entries();

            if (entries.Length == 0) throw new Exception("No stats entries");

            // Look for candidate-pair stats
            var hasCandidatePair = entries.Any(e => e.Type == "candidate-pair");
            if (!hasCandidatePair) throw new Exception($"No candidate-pair stats. Types found: {string.Join(", ", entries.Select(e => e.Type).Distinct())}");
        }
    }
}
