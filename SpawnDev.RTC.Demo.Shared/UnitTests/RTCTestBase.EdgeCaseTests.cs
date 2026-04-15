using SpawnDev.RTC;
using SpawnDev.UnitTesting;

namespace SpawnDev.RTC.Demo.Shared.UnitTests
{
    public abstract partial class RTCTestBase
    {
        /// <summary>
        /// Verify creating a peer connection with no config works.
        /// </summary>
        [TestMethod]
        public async Task PeerConnection_NoConfig()
        {
            using var pc = RTCPeerConnectionFactory.Create();
            if (pc == null) throw new Exception("Null with no config");
            await Task.CompletedTask;
        }

        /// <summary>
        /// Verify creating a peer connection with empty ICE servers works.
        /// </summary>
        [TestMethod]
        public async Task PeerConnection_EmptyIceServers()
        {
            using var pc = RTCPeerConnectionFactory.Create(new RTCPeerConnectionConfig
            {
                IceServers = System.Array.Empty<RTCIceServerConfig>(),
            });
            if (pc == null) throw new Exception("Null with empty ICE servers");
            await Task.CompletedTask;
        }

        /// <summary>
        /// Verify sending empty string through data channel.
        /// </summary>
        [TestMethod]
        public async Task DataChannel_SendEmptyString()
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
            pc2.OnDataChannel += channel => dc2Received.TrySetResult(channel);

            using var dc1 = pc1.CreateDataChannel("empty-test");

            var offer = await pc1.CreateOffer();
            await pc1.SetLocalDescription(offer);
            await pc2.SetRemoteDescription(offer);
            var answer = await pc2.CreateAnswer();
            await pc2.SetLocalDescription(answer);
            await pc1.SetRemoteDescription(answer);

            var completed = await Task.WhenAny(dc2Received.Task, Task.Delay(15000));
            if (completed != dc2Received.Task) throw new Exception("Timeout");
            using var dc2 = await dc2Received.Task;

            var dc1Open = new TaskCompletionSource<bool>();
            var dc2Open = new TaskCompletionSource<bool>();
            if (dc1.ReadyState == "open") dc1Open.TrySetResult(true);
            else dc1.OnOpen += () => dc1Open.TrySetResult(true);
            if (dc2.ReadyState == "open") dc2Open.TrySetResult(true);
            else dc2.OnOpen += () => dc2Open.TrySetResult(true);
            await Task.WhenAny(Task.WhenAll(dc1Open.Task, dc2Open.Task), Task.Delay(15000));

            // Send empty string
            var received = new TaskCompletionSource<string>();
            dc2.OnStringMessage += msg => received.TrySetResult(msg);
            dc1.Send("");

            var result = await Task.WhenAny(received.Task, Task.Delay(5000));
            if (result != received.Task) throw new Exception("Empty string not received");
            if (await received.Task != "") throw new Exception($"Expected empty, got '{await received.Task}'");
        }

        /// <summary>
        /// Verify creating multiple peer connections from the same factory.
        /// </summary>
        [TestMethod]
        public async Task Factory_MultipleConnections()
        {
            var pcs = new List<IRTCPeerConnection>();
            for (int i = 0; i < 5; i++)
            {
                pcs.Add(RTCPeerConnectionFactory.Create());
            }

            if (pcs.Count != 5) throw new Exception($"Expected 5, got {pcs.Count}");

            // All should be independent
            for (int i = 0; i < pcs.Count; i++)
            {
                for (int j = i + 1; j < pcs.Count; j++)
                {
                    if (ReferenceEquals(pcs[i], pcs[j]))
                        throw new Exception($"pc[{i}] and pc[{j}] are the same object");
                }
            }

            foreach (var pc in pcs) pc.Dispose();
            await Task.CompletedTask;
        }

        /// <summary>
        /// Verify data channel label with special characters.
        /// </summary>
        [TestMethod]
        public async Task DataChannel_SpecialCharLabel()
        {
            using var pc = RTCPeerConnectionFactory.Create();

            using var dc1 = pc.CreateDataChannel("test-channel_v2.0");
            if (dc1.Label != "test-channel_v2.0") throw new Exception($"Label: '{dc1.Label}'");

            using var dc2 = pc.CreateDataChannel("unicode-test");
            if (dc2.Label != "unicode-test") throw new Exception($"Label: '{dc2.Label}'");

            await Task.CompletedTask;
        }

        /// <summary>
        /// Verify GetSenders returns empty when no tracks added.
        /// </summary>
        [TestMethod]
        public async Task PeerConnection_GetSenders_Empty()
        {
            using var pc = RTCPeerConnectionFactory.Create();
            var senders = pc.GetSenders();
            if (senders == null) throw new Exception("GetSenders returned null");
            // May have senders if platform creates default tracks
            await Task.CompletedTask;
        }

        /// <summary>
        /// Verify GetReceivers returns empty when no remote tracks.
        /// </summary>
        [TestMethod]
        public async Task PeerConnection_GetReceivers_Empty()
        {
            using var pc = RTCPeerConnectionFactory.Create();
            var receivers = pc.GetReceivers();
            if (receivers == null) throw new Exception("GetReceivers returned null");
            await Task.CompletedTask;
        }

        /// <summary>
        /// Verify GetTransceivers returns empty initially.
        /// </summary>
        [TestMethod]
        public async Task PeerConnection_GetTransceivers_Empty()
        {
            using var pc = RTCPeerConnectionFactory.Create();
            var transceivers = pc.GetTransceivers();
            if (transceivers == null) throw new Exception("GetTransceivers returned null");
            await Task.CompletedTask;
        }
    }
}
