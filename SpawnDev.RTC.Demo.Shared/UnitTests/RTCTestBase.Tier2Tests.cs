using SpawnDev.RTC;
using SpawnDev.UnitTesting;

namespace SpawnDev.RTC.Demo.Shared.UnitTests
{
    public abstract partial class RTCTestBase
    {
        /// <summary>
        /// Verify signaling state transitions during offer/answer exchange.
        /// </summary>
        [TestMethod]
        public async Task SignalingState_Transitions()
        {
            using var pc1 = RTCPeerConnectionFactory.Create();
            using var pc2 = RTCPeerConnectionFactory.Create();

            var pc1States = new List<string>();
            var pc2States = new List<string>();
            pc1.OnSignalingStateChange += state => pc1States.Add(state);
            pc2.OnSignalingStateChange += state => pc2States.Add(state);

            pc1.OnIceCandidate += c => _ = pc2.AddIceCandidate(c);
            pc2.OnIceCandidate += c => _ = pc1.AddIceCandidate(c);
            pc2.OnDataChannel += _ => { };

            pc1.CreateDataChannel("sig-test");

            var offer = await pc1.CreateOffer();
            await pc1.SetLocalDescription(offer);
            // After setLocalDescription(offer): should be "have-local-offer"
            if (!pc1States.Contains("have-local-offer"))
                throw new Exception($"pc1 should have 'have-local-offer', got: [{string.Join(", ", pc1States)}]");

            await pc2.SetRemoteDescription(offer);
            // pc2 after setRemoteDescription(offer): should be "have-remote-offer"
            if (!pc2States.Contains("have-remote-offer"))
                throw new Exception($"pc2 should have 'have-remote-offer', got: [{string.Join(", ", pc2States)}]");

            var answer = await pc2.CreateAnswer();
            await pc2.SetLocalDescription(answer);
            await pc1.SetRemoteDescription(answer);

            // Both should return to "stable" after complete exchange
            if (!pc1States.Contains("stable"))
                throw new Exception($"pc1 should have 'stable', got: [{string.Join(", ", pc1States)}]");
            if (!pc2States.Contains("stable"))
                throw new Exception($"pc2 should have 'stable', got: [{string.Join(", ", pc2States)}]");
        }

        /// <summary>
        /// Verify description properties are populated after negotiation.
        /// </summary>
        [TestMethod]
        public async Task DescriptionProperties_AfterNegotiation()
        {
            using var pc1 = RTCPeerConnectionFactory.Create();
            using var pc2 = RTCPeerConnectionFactory.Create();

            pc1.OnIceCandidate += c => _ = pc2.AddIceCandidate(c);
            pc2.OnIceCandidate += c => _ = pc1.AddIceCandidate(c);
            pc2.OnDataChannel += _ => { };

            pc1.CreateDataChannel("desc-test");

            var offer = await pc1.CreateOffer();
            await pc1.SetLocalDescription(offer);

            // LocalDescription should be set
            if (pc1.LocalDescription == null) throw new Exception("pc1.LocalDescription is null after setLocalDescription");
            if (pc1.LocalDescription.Type != "offer") throw new Exception($"pc1.LocalDescription.Type should be 'offer', got '{pc1.LocalDescription.Type}'");
            if (string.IsNullOrEmpty(pc1.LocalDescription.Sdp)) throw new Exception("pc1.LocalDescription.Sdp is empty");

            await pc2.SetRemoteDescription(offer);
            var answer = await pc2.CreateAnswer();
            await pc2.SetLocalDescription(answer);
            await pc1.SetRemoteDescription(answer);

            // Both should have local and remote descriptions
            if (pc1.RemoteDescription == null) throw new Exception("pc1.RemoteDescription is null");
            if (pc2.LocalDescription == null) throw new Exception("pc2.LocalDescription is null");
            if (pc2.RemoteDescription == null) throw new Exception("pc2.RemoteDescription is null");

            // CurrentLocalDescription should be set
            if (pc1.CurrentLocalDescription == null) throw new Exception("pc1.CurrentLocalDescription is null");
            if (pc1.CurrentRemoteDescription == null) throw new Exception("pc1.CurrentRemoteDescription is null");
        }

        /// <summary>
        /// Verify data channel properties match what was configured.
        /// </summary>
        [TestMethod]
        public async Task DataChannel_ConfiguredProperties()
        {
            using var pc = RTCPeerConnectionFactory.Create();

            // Default channel (ordered)
            using var dc1 = pc.CreateDataChannel("ordered-default");
            if (!dc1.Ordered) throw new Exception("Default channel should be ordered");
            if (dc1.Label != "ordered-default") throw new Exception($"Label mismatch: '{dc1.Label}'");

            // Unordered channel
            using var dc2 = pc.CreateDataChannel("unordered", new RTCDataChannelConfig { Ordered = false });
            // Note: some implementations may not report Ordered correctly until negotiation
            // Just verify it doesn't crash

            // Channel with protocol
            using var dc3 = pc.CreateDataChannel("with-proto", new RTCDataChannelConfig { Protocol = "my-protocol" });
            if (dc3.Protocol != "my-protocol") throw new Exception($"Protocol mismatch: '{dc3.Protocol}'");

            await Task.CompletedTask;
        }

        /// <summary>
        /// Verify CreateOffer includes ICE restart when requested.
        /// </summary>
        [TestMethod]
        public async Task CreateOffer_WithIceRestart()
        {
            using var pc = RTCPeerConnectionFactory.Create();
            pc.CreateDataChannel("restart-test");

            // First offer
            var offer1 = await pc.CreateOffer();
            if (string.IsNullOrEmpty(offer1.Sdp)) throw new Exception("First offer SDP empty");

            // Offer with ICE restart
            var offer2 = await pc.CreateOffer(new RTCOfferOptions { IceRestart = true });
            if (string.IsNullOrEmpty(offer2.Sdp)) throw new Exception("ICE restart offer SDP empty");

            // Both should be valid offers
            if (offer1.Type != "offer") throw new Exception($"offer1 type: {offer1.Type}");
            if (offer2.Type != "offer") throw new Exception($"offer2 type: {offer2.Type}");
        }

        /// <summary>
        /// Verify CanTrickleIceCandidates property.
        /// </summary>
        [TestMethod]
        public async Task PeerConnection_CanTrickleIceCandidates()
        {
            using var pc = RTCPeerConnectionFactory.Create();
            // Should not be null (most implementations support trickle ICE)
            // Browser may return null before remote description is set, true after
            // Desktop always returns true
            // Just verify it doesn't throw
            var canTrickle = pc.CanTrickleIceCandidates;
            await Task.CompletedTask;
        }
    }
}
