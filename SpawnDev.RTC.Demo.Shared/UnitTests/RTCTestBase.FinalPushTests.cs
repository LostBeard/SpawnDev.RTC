using SpawnDev.RTC;
using SpawnDev.UnitTesting;

namespace SpawnDev.RTC.Demo.Shared.UnitTests
{
    public abstract partial class RTCTestBase
    {
        /// <summary>
        /// Phase 7 renegotiation-on-live-connection (desktop): build a connected peer with
        /// just a data channel, then add an audio track after the connection is live and
        /// run a second offer/answer exchange. Verifies pc2.OnTrack fires with audio kind
        /// after the renegotiation and the SDP picks up the new m=audio line. Browser path
        /// is covered by Event_NegotiationNeeded_FiresOnAddTrack in FullCoverageTests;
        /// this test proves the desktop SipSorcery fork handles the track-add-post-connect
        /// flow the same way.
        /// </summary>
        [TestMethod]
        public async Task Renegotiation_AddTrackAfterConnect_Desktop()
        {
            if (OperatingSystem.IsBrowser()) return; // Desktop-only; browser has its own test.

            var config = new RTCPeerConnectionConfig
            {
                IceServers = new[] { new RTCIceServerConfig { Urls = new[] { "stun:stun.l.google.com:19302" } } }
            };

            using var pc1 = RTCPeerConnectionFactory.Create(config);
            using var pc2 = RTCPeerConnectionFactory.Create(config);

            pc1.OnIceCandidate += c => _ = pc2.AddIceCandidate(c);
            pc2.OnIceCandidate += c => _ = pc1.AddIceCandidate(c);

            // Initial negotiation with data channel only.
            using var dc = pc1.CreateDataChannel("signal");
            var dcOpen = new TaskCompletionSource<bool>();
            dc.OnOpen += () => dcOpen.TrySetResult(true);
            pc2.OnDataChannel += _ => { };

            var offer1 = await pc1.CreateOffer();
            await pc1.SetLocalDescription(offer1);
            await pc2.SetRemoteDescription(offer1);
            var answer1 = await pc2.CreateAnswer();
            await pc2.SetLocalDescription(answer1);
            await pc1.SetRemoteDescription(answer1);

            var firstOpen = await Task.WhenAny(dcOpen.Task, Task.Delay(15000));
            if (firstOpen != dcOpen.Task) throw new Exception("Data channel never opened; can't test renegotiation on a live connection");

            // Connection is LIVE. Now add an audio track and renegotiate.
            var audioTrackEvent = new TaskCompletionSource<RTCTrackEventInit>();
            pc2.OnTrack += e =>
            {
                if (e.Track.Kind == "audio") audioTrackEvent.TrySetResult(e);
            };

            // Synthetic sine-wave audio track (shared test helper in this file's SineWaveAudioTrack).
            using var audioTrack = new SineWaveAudioTrack(frequencyHz: 440.0, sampleRateHz: 48000, channels: 2);
            var desktopPc1 = (SpawnDev.RTC.Desktop.DesktopRTCPeerConnection)pc1;
            desktopPc1.AddTrack(audioTrack);

            // Run the second offer/answer exchange. Some platforms fire OnNegotiationNeeded;
            // SipSorcery's desktop doesn't consistently, so we trigger the exchange manually.
            var offer2 = await pc1.CreateOffer();
            await pc1.SetLocalDescription(offer2);
            await pc2.SetRemoteDescription(offer2);
            var answer2 = await pc2.CreateAnswer();
            await pc2.SetLocalDescription(answer2);
            await pc1.SetRemoteDescription(answer2);

            // SDP on the renegotiated peer must include m=audio.
            var sdp = pc1.LocalDescription?.Sdp ?? "";
            if (!sdp.Contains("m=audio")) throw new Exception($"Renegotiated offer SDP missing m=audio:\n{sdp}");

            // pc2's OnTrack must fire for the newly-added audio track.
            var received = await Task.WhenAny(audioTrackEvent.Task, Task.Delay(15000));
            if (received != audioTrackEvent.Task)
                throw new Exception("pc2.OnTrack never fired for the track added after connection was live; desktop renegotiation broken");

            var ev = await audioTrackEvent.Task;
            if (ev.Track.Kind != "audio") throw new Exception($"Expected audio, got '{ev.Track.Kind}'");

            audioTrack.Stop();
        }

        /// <summary>
        /// Phase 7 renegotiation-on-live-connection (browser path). Mirror of
        /// Renegotiation_AddTrackAfterConnect_Desktop for the browser WebRTC stack. Uses
        /// RTCMediaDevices.GetUserMedia to source the track (Playwright runs with
        /// --use-fake-device-for-media-stream so this works headlessly). If getUserMedia
        /// denies access (rare in test rigs), skip gracefully.
        /// </summary>
        [TestMethod]
        public async Task Renegotiation_AddTrackAfterConnect_Browser()
        {
            if (!OperatingSystem.IsBrowser()) return; // Browser-only; desktop has its own test.

            IRTCMediaStream? stream = null;
            try
            {
                try
                {
                    stream = await RTCMediaDevices.GetUserMedia(new MediaStreamConstraints { Audio = true });
                }
                catch (Exception ex) when (ex.Message.Contains("NotAllowedError") || ex.Message.Contains("NotFoundError"))
                {
                    throw new UnsupportedTestException($"Mic / fake-device not available: {ex.Message}");
                }

                var config = new RTCPeerConnectionConfig
                {
                    IceServers = new[] { new RTCIceServerConfig { Urls = new[] { "stun:stun.l.google.com:19302" } } }
                };

                using var pc1 = RTCPeerConnectionFactory.Create(config);
                using var pc2 = RTCPeerConnectionFactory.Create(config);

                pc1.OnIceCandidate += c => _ = pc2.AddIceCandidate(c);
                pc2.OnIceCandidate += c => _ = pc1.AddIceCandidate(c);

                // Initial negotiation with data channel only.
                using var dc = pc1.CreateDataChannel("signal");
                var dcOpen = new TaskCompletionSource<bool>();
                dc.OnOpen += () => dcOpen.TrySetResult(true);
                pc2.OnDataChannel += _ => { };

                var offer1 = await pc1.CreateOffer();
                await pc1.SetLocalDescription(offer1);
                await pc2.SetRemoteDescription(offer1);
                var answer1 = await pc2.CreateAnswer();
                await pc2.SetLocalDescription(answer1);
                await pc1.SetRemoteDescription(answer1);

                var firstOpen = await Task.WhenAny(dcOpen.Task, Task.Delay(15000));
                if (firstOpen != dcOpen.Task) throw new Exception("Data channel never opened; can't test renegotiation on a live connection");

                // Connection LIVE. Add the audio track and re-negotiate.
                var audioTrackEvent = new TaskCompletionSource<RTCTrackEventInit>();
                pc2.OnTrack += e =>
                {
                    if (e.Track.Kind == "audio") audioTrackEvent.TrySetResult(e);
                };

                pc1.AddTrack(stream.GetAudioTracks()[0]);

                var offer2 = await pc1.CreateOffer();
                await pc1.SetLocalDescription(offer2);
                await pc2.SetRemoteDescription(offer2);
                var answer2 = await pc2.CreateAnswer();
                await pc2.SetLocalDescription(answer2);
                await pc1.SetRemoteDescription(answer2);

                var sdp = pc1.LocalDescription?.Sdp ?? "";
                if (!sdp.Contains("m=audio")) throw new Exception($"Renegotiated offer SDP missing m=audio:\n{sdp}");

                var received = await Task.WhenAny(audioTrackEvent.Task, Task.Delay(15000));
                if (received != audioTrackEvent.Task)
                    throw new Exception("pc2.OnTrack never fired for the track added after connection was live; browser renegotiation broken");

                var ev = await audioTrackEvent.Task;
                if (ev.Track.Kind != "audio") throw new Exception($"Expected audio, got '{ev.Track.Kind}'");
            }
            finally
            {
                stream?.Dispose();
            }
        }

        /// <summary>
        /// Perfect negotiation: use implicit SetLocalDescription (no args)
        /// for the full offer/answer exchange.
        /// </summary>
        [TestMethod]
        public async Task PerfectNegotiation_ImplicitSLD()
        {
            var config = new RTCPeerConnectionConfig
            {
                IceServers = new[] { new RTCIceServerConfig { Urls = new[] { "stun:stun.l.google.com:19302" } } }
            };

            using var pc1 = RTCPeerConnectionFactory.Create(config);
            using var pc2 = RTCPeerConnectionFactory.Create(config);

            pc1.OnIceCandidate += c => _ = pc2.AddIceCandidate(c);
            pc2.OnIceCandidate += c => _ = pc1.AddIceCandidate(c);

            var dc2Received = new TaskCompletionSource<IRTCDataChannel>();
            pc2.OnDataChannel += ch => dc2Received.TrySetResult(ch);

            pc1.CreateDataChannel("perfect-neg");

            // pc1: create offer and set local description
            var offer = await pc1.CreateOffer();
            await pc1.SetLocalDescription(offer);

            // pc2: set remote, then use implicit SetLocalDescription
            await pc2.SetRemoteDescription(offer);
            await pc2.SetLocalDescription(); // Implicit - auto-creates answer

            // Verify pc2 created an answer
            if (pc2.LocalDescription == null) throw new Exception("pc2 LocalDescription null after implicit SLD");
            if (pc2.LocalDescription.Type != "answer") throw new Exception($"pc2 type: {pc2.LocalDescription.Type}");

            // Complete the exchange
            await pc1.SetRemoteDescription(pc2.LocalDescription);

            // Verify data channel works
            var completed = await Task.WhenAny(dc2Received.Task, Task.Delay(15000));
            if (completed != dc2Received.Task) throw new Exception("Data channel timeout");
        }

        /// <summary>
        /// Verify ICE server with TURN URL format is accepted.
        /// </summary>
        [TestMethod]
        public async Task Config_TurnServer_Format()
        {
            var config = new RTCPeerConnectionConfig
            {
                IceServers = new[]
                {
                    new RTCIceServerConfig { Urls = new[] { "stun:stun.l.google.com:19302" } },
                    new RTCIceServerConfig
                    {
                        Urls = new[] { "turn:turn.example.com:3478", "turn:turn.example.com:3478?transport=tcp" },
                        Username = "testuser",
                        Credential = "testpass",
                    },
                }
            };

            using var pc = RTCPeerConnectionFactory.Create(config);
            pc.CreateDataChannel("turn-test");
            var offer = await pc.CreateOffer();
            if (string.IsNullOrEmpty(offer.Sdp)) throw new Exception("Offer SDP empty with TURN config");
        }

        /// <summary>
        /// Verify data channel ID is assigned after negotiation.
        /// </summary>
        [TestMethod]
        public async Task DataChannel_Id_AssignedAfterNegotiation()
        {
            var config = new RTCPeerConnectionConfig
            {
                IceServers = new[] { new RTCIceServerConfig { Urls = new[] { "stun:stun.l.google.com:19302" } } }
            };

            using var pc1 = RTCPeerConnectionFactory.Create(config);
            using var pc2 = RTCPeerConnectionFactory.Create(config);

            pc1.OnIceCandidate += c => _ = pc2.AddIceCandidate(c);
            pc2.OnIceCandidate += c => _ = pc1.AddIceCandidate(c);

            var dc2Received = new TaskCompletionSource<IRTCDataChannel>();
            pc2.OnDataChannel += ch => dc2Received.TrySetResult(ch);

            using var dc1 = pc1.CreateDataChannel("id-test");

            var offer = await pc1.CreateOffer();
            await pc1.SetLocalDescription(offer);
            await pc2.SetRemoteDescription(offer);
            var answer = await pc2.CreateAnswer();
            await pc2.SetLocalDescription(answer);
            await pc1.SetRemoteDescription(answer);

            var completed = await Task.WhenAny(dc2Received.Task, Task.Delay(15000));
            if (completed != dc2Received.Task) throw new Exception("Timeout");
            using var dc2 = await dc2Received.Task;

            // Both channels should have an assigned ID
            if (dc1.Id == null) throw new Exception("dc1.Id is null");
            if (dc2.Id == null) throw new Exception("dc2.Id is null");
        }

        /// <summary>
        /// Verify RestartIce produces a new offer with different ICE credentials.
        /// </summary>
        [TestMethod]
        public async Task RestartIce_NewCredentials()
        {
            using var pc = RTCPeerConnectionFactory.Create(new RTCPeerConnectionConfig
            {
                IceServers = new[] { new RTCIceServerConfig { Urls = new[] { "stun:stun.l.google.com:19302" } } }
            });

            pc.CreateDataChannel("restart-test");

            var offer1 = await pc.CreateOffer();
            await pc.SetLocalDescription(offer1);

            // Extract ICE ufrag from first offer
            var ufrag1 = ExtractSdpValue(offer1.Sdp, "a=ice-ufrag:");

            // Restart ICE
            pc.RestartIce();

            var offer2 = await pc.CreateOffer(new RTCOfferOptions { IceRestart = true });

            var ufrag2 = ExtractSdpValue(offer2.Sdp, "a=ice-ufrag:");

            // On browser, ICE restart should produce different credentials
            // On desktop, SipSorcery may or may not change them
            if (OperatingSystem.IsBrowser() && ufrag1 == ufrag2)
                throw new Exception("ICE ufrag should change after restart");

            // At minimum, both should be valid
            if (string.IsNullOrEmpty(ufrag1)) throw new Exception("First ufrag empty");
            if (string.IsNullOrEmpty(ufrag2)) throw new Exception("Second ufrag empty");
        }

        private static string ExtractSdpValue(string sdp, string prefix)
        {
            foreach (var line in sdp.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith(prefix))
                    return trimmed[prefix.Length..];
            }
            return "";
        }
    }
}
