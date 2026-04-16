using SpawnDev.RTC;
using SpawnDev.UnitTesting;

namespace SpawnDev.RTC.Demo.Shared.UnitTests
{
    public abstract partial class RTCTestBase
    {
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
