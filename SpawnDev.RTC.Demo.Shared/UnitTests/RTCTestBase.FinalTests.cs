using SpawnDev.RTC;
using SpawnDev.UnitTesting;

namespace SpawnDev.RTC.Demo.Shared.UnitTests
{
    public abstract partial class RTCTestBase
    {
        /// <summary>
        /// Verify GetStats returns a report after connection is established.
        /// </summary>
        [TestMethod]
        public async Task PeerConnection_GetStats()
        {
            var config = new RTCPeerConnectionConfig
            {
                IceServers = new[] { new RTCIceServerConfig { Urls = new[] { "stun:stun.l.google.com:19302" } } }
            };

            using var pc1 = RTCPeerConnectionFactory.Create(config);
            using var pc2 = RTCPeerConnectionFactory.Create(config);

            pc1.OnIceCandidate += c => _ = pc2.AddIceCandidate(c);
            pc2.OnIceCandidate += c => _ = pc1.AddIceCandidate(c);

            var connected = new TaskCompletionSource<bool>();
            pc1.OnConnectionStateChange += state =>
            {
                if (state == "connected") connected.TrySetResult(true);
            };

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
            if (stats == null) throw new Exception("GetStats returned null");

            // Both platforms now return entries. Browser returns rich per-candidate-pair /
            // per-codec / per-transport stats from the native stack. Desktop returns a
            // best-effort peer-connection + transport pair sourced from SipSorcery state.
            if (stats.Size == 0) throw new Exception("Stats should have entries after connection");
            var keys = stats.Keys();
            if (keys.Length == 0) throw new Exception("Stats keys should not be empty");

            // Assert at least one entry has a populated Values dict. This guards against
            // the pre-2026-04-22 bug where BrowserRTCStatsReport left every entry's
            // Values empty (dropping bytesReceived / rtt / packetsLost / jitter) and
            // DesktopRTCStatsReport returned nothing at all. We don't assert specific
            // field names because the W3C-standard fields vary across stat types
            // (e.g. peer-connection stats only have dataChannelsOpened/Closed; transport
            // stats have dtlsState; candidate-pair has roundTripTime; etc.).
            var entries = stats.Entries();
            var populated = entries.Where(e => e.Values.Count > 0).ToArray();
            if (populated.Length == 0)
                throw new Exception($"No stats entry has populated Values. Types found: {string.Join(",", entries.Select(e => e.Type).Distinct())}");
        }

        /// <summary>
        /// Verify MediaStream AddTrack/RemoveTrack fire events.
        /// </summary>
        [TestMethod]
        public async Task MediaStream_AddRemoveTrack_Events()
        {
            try
            {
                var stream = await RTCMediaDevices.GetUserMedia(new MediaStreamConstraints { Audio = true });
                var track = stream.GetAudioTracks()[0];

                // Create empty stream and add track
                IRTCMediaStream testStream;
                if (OperatingSystem.IsBrowser())
                {
                    testStream = new SpawnDev.RTC.Browser.BrowserRTCMediaStream(
                        new SpawnDev.BlazorJS.JSObjects.MediaStream());
                }
                else
                {
                    testStream = new SpawnDev.RTC.Desktop.DesktopRTCMediaStream(
                        System.Array.Empty<IRTCMediaStreamTrack>());
                }

                var addFired = false;
                var removeFired = false;
                testStream.OnAddTrack += _ => addFired = true;
                testStream.OnRemoveTrack += _ => removeFired = true;

                testStream.AddTrack(track);
                if (!addFired) throw new Exception("OnAddTrack did not fire");
                if (testStream.GetTracks().Length != 1) throw new Exception("Track count should be 1 after add");

                testStream.RemoveTrack(track);
                if (!removeFired) throw new Exception("OnRemoveTrack did not fire");
                if (testStream.GetTracks().Length != 0) throw new Exception("Track count should be 0 after remove");

                testStream.Dispose();
                stream.Dispose();
            }
            catch (Exception ex) when (ex.Message.Contains("NotAllowedError") || ex.Message.Contains("permission"))
            {
                throw new Exception($"SKIP: Camera/mic not available: {ex.Message}");
            }
        }

        /// <summary>
        /// Verify peer connection can be closed and state reflects it.
        /// </summary>
        [TestMethod]
        public async Task PeerConnection_Close_StateChanges()
        {
            using var pc = RTCPeerConnectionFactory.Create();

            var closedState = "";
            pc.OnConnectionStateChange += state => closedState = state;

            pc.CreateDataChannel("close-test");
            pc.Close();

            // After close, connection state should reflect closed
            var state = pc.ConnectionState;
            if (state != "closed") throw new Exception($"Expected 'closed', got '{state}'");

            await Task.CompletedTask;
        }

        /// <summary>
        /// Verify implicit SetLocalDescription creates SDP automatically.
        /// </summary>
        [TestMethod]
        public async Task PeerConnection_ImplicitSetLocalDescription()
        {
            using var pc1 = RTCPeerConnectionFactory.Create();
            using var pc2 = RTCPeerConnectionFactory.Create();

            pc1.OnIceCandidate += c => _ = pc2.AddIceCandidate(c);
            pc2.OnIceCandidate += c => _ = pc1.AddIceCandidate(c);
            pc2.OnDataChannel += _ => { };

            pc1.CreateDataChannel("implicit-test");

            // Use implicit setLocalDescription (no args)
            var offer = await pc1.CreateOffer();
            await pc1.SetLocalDescription(offer);

            if (pc1.LocalDescription == null) throw new Exception("LocalDescription null after implicit set");
            if (string.IsNullOrEmpty(pc1.LocalDescription.Sdp)) throw new Exception("LocalDescription SDP empty");

            await pc2.SetRemoteDescription(offer);

            // pc2 uses implicit SetLocalDescription for answer
            await pc2.SetLocalDescription();
            if (pc2.LocalDescription == null) throw new Exception("pc2 LocalDescription null after implicit set");
            if (pc2.LocalDescription.Type != "answer") throw new Exception($"pc2 should have answer, got '{pc2.LocalDescription.Type}'");
        }

        /// <summary>
        /// Verify creating a data channel with negotiated option.
        /// </summary>
        [TestMethod]
        public async Task DataChannel_Negotiated()
        {
            using var pc1 = RTCPeerConnectionFactory.Create();
            using var pc2 = RTCPeerConnectionFactory.Create();

            // Create negotiated channels on both sides with same ID
            using var dc1 = pc1.CreateDataChannel("negotiated", new RTCDataChannelConfig
            {
                Negotiated = true,
                Id = 42,
            });

            using var dc2 = pc2.CreateDataChannel("negotiated", new RTCDataChannelConfig
            {
                Negotiated = true,
                Id = 42,
            });

            if (dc1.Label != "negotiated") throw new Exception($"dc1 label: '{dc1.Label}'");
            if (dc2.Label != "negotiated") throw new Exception($"dc2 label: '{dc2.Label}'");
            if (dc1.Negotiated != true) throw new Exception("dc1 should be negotiated");
            if (dc2.Negotiated != true) throw new Exception("dc2 should be negotiated");

            await Task.CompletedTask;
        }

        /// <summary>
        /// Verify RTCPeerConnectionFactory creates the correct type for each platform.
        /// </summary>
        [TestMethod]
        public async Task Factory_CreatesPlatformCorrectType()
        {
            using var pc = RTCPeerConnectionFactory.Create();

            if (OperatingSystem.IsBrowser())
            {
                if (pc is not SpawnDev.RTC.Browser.BrowserRTCPeerConnection)
                    throw new Exception($"Browser should create BrowserRTCPeerConnection, got {pc.GetType().Name}");
            }
            else
            {
                if (pc is not SpawnDev.RTC.Desktop.DesktopRTCPeerConnection)
                    throw new Exception($"Desktop should create DesktopRTCPeerConnection, got {pc.GetType().Name}");
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Verify RTCMediaDevices.GetUserMedia with audio only.
        /// </summary>
        [TestMethod]
        public async Task GetUserMedia_AudioOnly()
        {
            try
            {
                var stream = await RTCMediaDevices.GetUserMedia(new MediaStreamConstraints { Audio = true });
                var audioTracks = stream.GetAudioTracks();
                var videoTracks = stream.GetVideoTracks();

                if (audioTracks.Length == 0) throw new Exception("Should have audio tracks");
                if (videoTracks.Length != 0) throw new Exception("Should NOT have video tracks for audio-only request");

                stream.Dispose();
            }
            catch (Exception ex) when (ex.Message.Contains("NotAllowedError") || ex.Message.Contains("permission"))
            {
                throw new Exception($"SKIP: Mic not available: {ex.Message}");
            }
        }

        /// <summary>
        /// Verify peer connection with ICE transport policy "relay" config.
        /// </summary>
        [TestMethod]
        public async Task PeerConnection_Config_IceTransportRelay()
        {
            var config = new RTCPeerConnectionConfig
            {
                IceServers = new[] { new RTCIceServerConfig { Urls = new[] { "stun:stun.l.google.com:19302" } } },
                IceTransportPolicy = "relay",
            };

            using var pc = RTCPeerConnectionFactory.Create(config);
            if (pc == null) throw new Exception("Failed to create with relay config");

            // Just verify creation succeeds with relay policy
            // (without TURN server, no candidates will be gathered, but creation should work)
            await Task.CompletedTask;
        }
    }
}
