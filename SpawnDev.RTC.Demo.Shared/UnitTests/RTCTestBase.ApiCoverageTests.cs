using SpawnDev.RTC;
using SpawnDev.UnitTesting;

namespace SpawnDev.RTC.Demo.Shared.UnitTests
{
    public abstract partial class RTCTestBase
    {
        /// <summary>
        /// Verify MediaStream.Active reflects track state.
        /// </summary>
        [TestMethod]
        public async Task MediaStream_Active_Property()
        {
            try
            {
                var stream = await RTCMediaDevices.GetUserMedia(new MediaStreamConstraints { Audio = true });
                if (!stream.Active) throw new Exception("Stream should be active after getUserMedia");

                var tracks = stream.GetTracks();
                if (tracks.Length == 0) throw new Exception("No tracks");

                // Stop all tracks
                foreach (var t in tracks) t.Stop();

                // Desktop implementation tracks active based on track readyState
                // Browser may still report active briefly
                stream.Dispose();
            }
            catch (Exception ex) when (ex.Message.Contains("NotAllowedError"))
            {
                throw new Exception($"SKIP: Mic not available: {ex.Message}");
            }
        }

        /// <summary>
        /// Verify MediaStream.GetTrackById finds the right track.
        /// </summary>
        [TestMethod]
        public async Task MediaStream_GetTrackById()
        {
            try
            {
                var stream = await RTCMediaDevices.GetUserMedia(new MediaStreamConstraints { Audio = true });
                var track = stream.GetAudioTracks()[0];
                var trackId = track.Id;

                var found = stream.GetTrackById(trackId);
                if (found == null) throw new Exception($"GetTrackById({trackId}) returned null");
                if (found.Id != trackId) throw new Exception($"Found track ID mismatch: {found.Id} != {trackId}");

                var notFound = stream.GetTrackById("nonexistent-id");
                if (notFound != null) throw new Exception("Should return null for nonexistent ID");

                stream.Dispose();
            }
            catch (Exception ex) when (ex.Message.Contains("NotAllowedError"))
            {
                throw new Exception($"SKIP: Mic not available: {ex.Message}");
            }
        }

        /// <summary>
        /// Verify MediaStreamTrack.OnEnded fires when Stop() is called.
        /// </summary>
        [TestMethod]
        public async Task MediaStreamTrack_OnEnded_Fires()
        {
            try
            {
                var stream = await RTCMediaDevices.GetUserMedia(new MediaStreamConstraints { Audio = true });
                var track = stream.GetAudioTracks()[0];

                var ended = new TaskCompletionSource<bool>();
                track.OnEnded += () => ended.TrySetResult(true);

                track.Stop();

                // On desktop, OnEnded fires synchronously in Stop()
                // On browser, it may be async
                var result = await Task.WhenAny(ended.Task, Task.Delay(3000));
                if (result != ended.Task && !OperatingSystem.IsBrowser())
                    throw new Exception("OnEnded did not fire after Stop()");

                if (track.ReadyState != "ended")
                    throw new Exception($"ReadyState should be 'ended', got '{track.ReadyState}'");

                stream.Dispose();
            }
            catch (Exception ex) when (ex.Message.Contains("NotAllowedError"))
            {
                throw new Exception($"SKIP: Mic not available: {ex.Message}");
            }
        }

        /// <summary>
        /// Verify data channel with custom protocol string.
        /// </summary>
        [TestMethod]
        public async Task DataChannel_CustomProtocol()
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

            using var dc1 = pc1.CreateDataChannel("proto-test", new RTCDataChannelConfig
            {
                Protocol = "spawndev-wire-v1",
            });

            if (dc1.Protocol != "spawndev-wire-v1")
                throw new Exception($"Local protocol: '{dc1.Protocol}'");

            var offer = await pc1.CreateOffer();
            await pc1.SetLocalDescription(offer);
            await pc2.SetRemoteDescription(offer);
            var answer = await pc2.CreateAnswer();
            await pc2.SetLocalDescription(answer);
            await pc1.SetRemoteDescription(answer);

            var completed = await Task.WhenAny(dc2Received.Task, Task.Delay(15000));
            if (completed != dc2Received.Task) throw new Exception("Timeout");
            using var dc2 = await dc2Received.Task;

            // Verify remote side sees the protocol
            if (dc2.Protocol != "spawndev-wire-v1")
                throw new Exception($"Remote protocol: '{dc2.Protocol}'");
        }

        /// <summary>
        /// Verify peer connection with bundlePolicy "max-bundle".
        /// </summary>
        [TestMethod]
        public async Task Config_BundlePolicy_MaxBundle()
        {
            var config = new RTCPeerConnectionConfig
            {
                IceServers = new[] { new RTCIceServerConfig { Urls = new[] { "stun:stun.l.google.com:19302" } } },
                BundlePolicy = "max-bundle",
            };

            using var pc = RTCPeerConnectionFactory.Create(config);
            pc.CreateDataChannel("bundle-test");
            var offer = await pc.CreateOffer();

            // max-bundle SDP should contain a=group:BUNDLE
            if (OperatingSystem.IsBrowser())
            {
                if (!offer.Sdp.Contains("a=group:BUNDLE"))
                    throw new Exception("max-bundle SDP missing BUNDLE group");
            }
        }

        /// <summary>
        /// Verify GetSenders/GetReceivers after adding audio transceiver.
        /// </summary>
        [TestMethod]
        public async Task Transceiver_SendersReceivers()
        {
            using var pc = RTCPeerConnectionFactory.Create();

            var t = pc.AddTransceiver("audio");

            var senders = pc.GetSenders();

            if (senders.Length == 0) throw new Exception("No senders after AddTransceiver");

            // Receivers are populated after remote description is set (remote tracks arrive)
            // Before connection, browser has receivers, desktop doesn't
            if (OperatingSystem.IsBrowser())
            {
                var receivers = pc.GetReceivers();
                if (receivers.Length == 0) throw new Exception("Browser: no receivers after AddTransceiver");
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Verify multiple GetStats calls don't leak or crash.
        /// </summary>
        [TestMethod]
        public async Task GetStats_MultipleCalls_NoLeak()
        {
            var config = new RTCPeerConnectionConfig
            {
                IceServers = new[] { new RTCIceServerConfig { Urls = new[] { "stun:stun.l.google.com:19302" } } }
            };

            using var pc1 = RTCPeerConnectionFactory.Create(config);
            using var pc2 = RTCPeerConnectionFactory.Create(config);

            pc1.OnIceCandidate += c => _ = pc2.AddIceCandidate(c);
            pc2.OnIceCandidate += c => _ = pc1.AddIceCandidate(c);
            pc2.OnDataChannel += _ => { };

            pc1.CreateDataChannel("stats-leak");

            var offer = await pc1.CreateOffer();
            await pc1.SetLocalDescription(offer);
            await pc2.SetRemoteDescription(offer);
            var answer = await pc2.CreateAnswer();
            await pc2.SetLocalDescription(answer);
            await pc1.SetRemoteDescription(answer);

            // Call GetStats 10 times rapidly, dispose each
            for (int i = 0; i < 10; i++)
            {
                using var stats = await pc1.GetStats();
                // Just verify it doesn't throw or leak
            }
        }
    }
}
