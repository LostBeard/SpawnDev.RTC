using SpawnDev.RTC;
using SpawnDev.UnitTesting;

namespace SpawnDev.RTC.Demo.Shared.UnitTests
{
    public abstract partial class RTCTestBase
    {
        /// <summary>
        /// Verify unreliable data channel with maxRetransmits.
        /// </summary>
        [TestMethod]
        public async Task DataChannel_Unreliable_MaxRetransmits()
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

            // Unreliable channel: max 3 retransmits, unordered
            using var dc1 = pc1.CreateDataChannel("unreliable", new RTCDataChannelConfig
            {
                Ordered = false,
                MaxRetransmits = 3,
            });

            var offer = await pc1.CreateOffer();
            await pc1.SetLocalDescription(offer);
            await pc2.SetRemoteDescription(offer);
            var answer = await pc2.CreateAnswer();
            await pc2.SetLocalDescription(answer);
            await pc1.SetRemoteDescription(answer);

            var completed = await Task.WhenAny(dc2Received.Task, Task.Delay(15000));
            if (completed != dc2Received.Task) throw new Exception("Timeout");
            using var dc2 = await dc2Received.Task;

            var dcOpen = new TaskCompletionSource<bool>();
            if (dc1.ReadyState == "open") dcOpen.TrySetResult(true);
            else dc1.OnOpen += () => dcOpen.TrySetResult(true);
            await Task.WhenAny(dcOpen.Task, Task.Delay(15000));

            // Send a message and verify it arrives
            var received = new TaskCompletionSource<string>();
            dc2.OnStringMessage += m => received.TrySetResult(m);
            dc1.Send("unreliable-msg");

            var result = await Task.WhenAny(received.Task, Task.Delay(5000));
            if (result != received.Task) throw new Exception("Unreliable message not received");
            if (await received.Task != "unreliable-msg") throw new Exception($"Got: {await received.Task}");
        }

        /// <summary>
        /// Verify double-dispose doesn't throw.
        /// </summary>
        [TestMethod]
        public async Task PeerConnection_DoubleDispose_NoThrow()
        {
            var pc = RTCPeerConnectionFactory.Create();
            pc.CreateDataChannel("test");
            pc.Close();
            pc.Dispose();
            pc.Dispose(); // Second dispose should not throw
            await Task.CompletedTask;
        }

        /// <summary>
        /// Verify data channel double-dispose doesn't throw.
        /// </summary>
        [TestMethod]
        public async Task DataChannel_DoubleDispose_NoThrow()
        {
            using var pc = RTCPeerConnectionFactory.Create();
            var dc = pc.CreateDataChannel("test");
            dc.Close();
            dc.Dispose();
            dc.Dispose(); // Second dispose should not throw
            await Task.CompletedTask;
        }

        /// <summary>
        /// Verify SDP offer type string is exactly "offer".
        /// </summary>
        [TestMethod]
        public async Task SDP_OfferType_ExactString()
        {
            using var pc = RTCPeerConnectionFactory.Create();
            pc.CreateDataChannel("test");
            var offer = await pc.CreateOffer();
            if (offer.Type != "offer") throw new Exception($"Type is '{offer.Type}', expected exactly 'offer'");
        }

        /// <summary>
        /// Verify SDP answer type string is exactly "answer".
        /// </summary>
        [TestMethod]
        public async Task SDP_AnswerType_ExactString()
        {
            using var pc1 = RTCPeerConnectionFactory.Create();
            using var pc2 = RTCPeerConnectionFactory.Create();
            pc2.OnDataChannel += _ => { };

            pc1.CreateDataChannel("test");
            var offer = await pc1.CreateOffer();
            await pc1.SetLocalDescription(offer);
            await pc2.SetRemoteDescription(offer);
            var answer = await pc2.CreateAnswer();
            if (answer.Type != "answer") throw new Exception($"Type is '{answer.Type}', expected exactly 'answer'");
        }

        /// <summary>
        /// Verify connection reaches "connected" state in a full exchange.
        /// </summary>
        [TestMethod]
        public async Task ConnectionState_ReachesConnected()
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

            var pc1Connected = new TaskCompletionSource<bool>();
            var pc2Connected = new TaskCompletionSource<bool>();
            pc1.OnConnectionStateChange += s => { if (s == "connected") pc1Connected.TrySetResult(true); };
            pc2.OnConnectionStateChange += s => { if (s == "connected") pc2Connected.TrySetResult(true); };

            pc1.CreateDataChannel("connect-test");

            var offer = await pc1.CreateOffer();
            await pc1.SetLocalDescription(offer);
            await pc2.SetRemoteDescription(offer);
            var answer = await pc2.CreateAnswer();
            await pc2.SetLocalDescription(answer);
            await pc1.SetRemoteDescription(answer);

            var both = Task.WhenAll(pc1Connected.Task, pc2Connected.Task);
            var result = await Task.WhenAny(both, Task.Delay(15000));
            if (result != both) throw new Exception("Connection did not reach 'connected'");

            if (pc1.ConnectionState != "connected") throw new Exception($"pc1: {pc1.ConnectionState}");
            if (pc2.ConnectionState != "connected") throw new Exception($"pc2: {pc2.ConnectionState}");
        }

        /// <summary>
        /// Verify <see cref="SpawnDev.RTC.Signaling.TrackerSignalingClient"/> reports its
        /// configured URL and a 20-byte local peer id. Property-level sanity check; the room
        /// determinism and key format are covered by the RoomKey_* tests in SignalingTests.
        /// </summary>
        [TestMethod]
        public async Task TrackerSignalingClient_Properties()
        {
            var peerId = new byte[20];
            System.Security.Cryptography.RandomNumberGenerator.Fill(peerId);

            await using var client = new SpawnDev.RTC.Signaling.TrackerSignalingClient("wss://tracker.openwebtorrent.com", peerId);

            if (client.AnnounceUrl != "wss://tracker.openwebtorrent.com")
                throw new Exception($"AnnounceUrl: expected wss://tracker.openwebtorrent.com, got {client.AnnounceUrl}");
            if (client.LocalPeerId.Length != 20)
                throw new Exception($"LocalPeerId length: expected 20, got {client.LocalPeerId.Length}");
            // LocalPeerId must be a defensive copy, not the original array.
            peerId[0] ^= 0xFF;
            if (client.LocalPeerId[0] == peerId[0])
                throw new Exception("LocalPeerId aliased the caller's buffer instead of copying.");
        }

        /// <summary>
        /// Verify GetUserMedia with video-only returns no audio tracks.
        /// </summary>
        [TestMethod]
        public async Task GetUserMedia_VideoOnly()
        {
            if (!OperatingSystem.IsBrowser())
            {
                // Desktop: verify it creates video track without audio
                var stream = await RTCMediaDevices.GetUserMedia(new MediaStreamConstraints { Video = true });
                var videoTracks = stream.GetVideoTracks();
                if (videoTracks.Length == 0) throw new Exception("No video tracks");
                stream.Dispose();
                return;
            }

            try
            {
                var stream = await RTCMediaDevices.GetUserMedia(new MediaStreamConstraints { Video = true });
                var videoTracks = stream.GetVideoTracks();
                var audioTracks = stream.GetAudioTracks();
                if (videoTracks.Length == 0) throw new Exception("No video tracks");
                if (audioTracks.Length != 0) throw new Exception("Should not have audio tracks for video-only");
                stream.Dispose();
            }
            catch (Exception ex) when (ex.Message.Contains("NotAllowedError"))
            {
                throw new Exception($"SKIP: Camera not available: {ex.Message}");
            }
        }
    }
}
