using SpawnDev.RTC;
using SpawnDev.UnitTesting;

namespace SpawnDev.RTC.Demo.Shared.UnitTests
{
    public abstract partial class RTCTestBase
    {
        protected RTCTestBase()
        {
        }

        /// <summary>
        /// Verify the test infrastructure is working.
        /// </summary>
        [TestMethod]
        public async Task TestInfrastructure_Working()
        {
            if (1 + 1 != 2) throw new Exception("Math is broken");
            await Task.CompletedTask;
        }

        /// <summary>
        /// Verify peer connection can be created on this platform.
        /// </summary>
        [TestMethod]
        public async Task PeerConnection_CanCreate()
        {
            using var pc = RTCPeerConnectionFactory.Create();
            if (pc == null) throw new Exception("PeerConnection is null");
            if (pc.SignalingState != "new") throw new Exception($"Expected signaling state 'new', got '{pc.SignalingState}'");
            await Task.CompletedTask;
        }

        /// <summary>
        /// Verify peer connection can create an offer with a data channel.
        /// </summary>
        [TestMethod]
        public async Task PeerConnection_CanCreateOffer()
        {
            using var pc = RTCPeerConnectionFactory.Create();
            var dc = pc.CreateDataChannel("test");
            var offer = await pc.CreateOffer();
            if (offer == null) throw new Exception("Offer is null");
            if (offer.Type != "offer") throw new Exception($"Expected type 'offer', got '{offer.Type}'");
            if (string.IsNullOrEmpty(offer.Sdp)) throw new Exception("Offer SDP is empty");
            if (!offer.Sdp.Contains("webrtc-datachannel")) throw new Exception("Offer SDP missing data channel");
            dc.Dispose();
        }

        /// <summary>
        /// Verify data channel can be created.
        /// </summary>
        [TestMethod]
        public async Task DataChannel_CanCreate()
        {
            using var pc = RTCPeerConnectionFactory.Create();
            using var dc = pc.CreateDataChannel("myChannel");
            if (dc == null) throw new Exception("DataChannel is null");
            if (dc.Label != "myChannel") throw new Exception($"Expected label 'myChannel', got '{dc.Label}'");
            await Task.CompletedTask;
        }

        /// <summary>
        /// Loopback: two local peers exchange offer/answer and send a string message.
        /// </summary>
        [TestMethod]
        public async Task Loopback_DataChannel_StringMessage()
        {
            var config = new RTCPeerConnectionConfig
            {
                IceServers = new[] { new RTCIceServerConfig { Urls = "stun:stun.l.google.com:19302" } }
            };

            using var pc1 = RTCPeerConnectionFactory.Create(config);
            using var pc2 = RTCPeerConnectionFactory.Create(config);

            // Trickle ICE
            pc1.OnIceCandidate += candidate => _ = pc2.AddIceCandidate(candidate);
            pc2.OnIceCandidate += candidate => _ = pc1.AddIceCandidate(candidate);

            // Track when pc2 receives the data channel
            var dc2Received = new TaskCompletionSource<IRTCDataChannel>();
            pc2.OnDataChannel += channel => dc2Received.TrySetResult(channel);

            // PC1 creates a data channel and an offer
            using var dc1 = pc1.CreateDataChannel("loopback");

            var offer = await pc1.CreateOffer();
            await pc1.SetLocalDescription(offer);
            await pc2.SetRemoteDescription(offer);

            var answer = await pc2.CreateAnswer();
            await pc2.SetLocalDescription(answer);
            await pc1.SetRemoteDescription(answer);

            // Wait for pc2 to receive the data channel
            var completed = await Task.WhenAny(dc2Received.Task, Task.Delay(15000));
            if (completed != dc2Received.Task) throw new Exception("Timed out waiting for data channel on pc2");
            using var dc2 = await dc2Received.Task;

            // Wait for both channels to open
            var dc1Open = new TaskCompletionSource<bool>();
            var dc2Open = new TaskCompletionSource<bool>();

            if (dc1.ReadyState == "open") dc1Open.TrySetResult(true);
            else dc1.OnOpen += () => dc1Open.TrySetResult(true);

            if (dc2.ReadyState == "open") dc2Open.TrySetResult(true);
            else dc2.OnOpen += () => dc2Open.TrySetResult(true);

            var openCompleted = await Task.WhenAny(Task.WhenAll(dc1Open.Task, dc2Open.Task), Task.Delay(15000));
            if (!dc1Open.Task.IsCompletedSuccessfully) throw new Exception("dc1 did not open in time");
            if (!dc2Open.Task.IsCompletedSuccessfully) throw new Exception("dc2 did not open in time");

            // Send a message from dc1 to dc2
            var messageReceived = new TaskCompletionSource<string>();
            dc2.OnStringMessage += msg => messageReceived.TrySetResult(msg);
            dc1.Send("Hello from pc1!");

            var msgCompleted = await Task.WhenAny(messageReceived.Task, Task.Delay(5000));
            if (msgCompleted != messageReceived.Task) throw new Exception("Timed out waiting for message");
            var received = await messageReceived.Task;
            if (received != "Hello from pc1!") throw new Exception($"Expected 'Hello from pc1!', got '{received}'");
        }

        /// <summary>
        /// Loopback: send binary data through a data channel.
        /// </summary>
        [TestMethod]
        public async Task Loopback_DataChannel_BinaryMessage()
        {
            var config = new RTCPeerConnectionConfig
            {
                IceServers = new[] { new RTCIceServerConfig { Urls = "stun:stun.l.google.com:19302" } }
            };

            using var pc1 = RTCPeerConnectionFactory.Create(config);
            using var pc2 = RTCPeerConnectionFactory.Create(config);

            pc1.OnIceCandidate += candidate => _ = pc2.AddIceCandidate(candidate);
            pc2.OnIceCandidate += candidate => _ = pc1.AddIceCandidate(candidate);

            var dc2Received = new TaskCompletionSource<IRTCDataChannel>();
            pc2.OnDataChannel += channel => dc2Received.TrySetResult(channel);

            using var dc1 = pc1.CreateDataChannel("binary-test");

            var offer = await pc1.CreateOffer();
            await pc1.SetLocalDescription(offer);
            await pc2.SetRemoteDescription(offer);

            var answer = await pc2.CreateAnswer();
            await pc2.SetLocalDescription(answer);
            await pc1.SetRemoteDescription(answer);

            var completed = await Task.WhenAny(dc2Received.Task, Task.Delay(15000));
            if (completed != dc2Received.Task) throw new Exception("Timed out waiting for data channel");
            using var dc2 = await dc2Received.Task;

            // Wait for open
            var dc1Open = new TaskCompletionSource<bool>();
            var dc2Open = new TaskCompletionSource<bool>();
            if (dc1.ReadyState == "open") dc1Open.TrySetResult(true);
            else dc1.OnOpen += () => dc1Open.TrySetResult(true);
            if (dc2.ReadyState == "open") dc2Open.TrySetResult(true);
            else dc2.OnOpen += () => dc2Open.TrySetResult(true);
            await Task.WhenAny(Task.WhenAll(dc1Open.Task, dc2Open.Task), Task.Delay(15000));
            if (!dc1Open.Task.IsCompletedSuccessfully) throw new Exception("dc1 did not open");
            if (!dc2Open.Task.IsCompletedSuccessfully) throw new Exception("dc2 did not open");

            // Send binary data
            var testData = new byte[] { 0x01, 0x02, 0x03, 0xFF, 0xFE, 0xFD };
            var messageReceived = new TaskCompletionSource<byte[]>();
            dc2.OnBinaryMessage += data => messageReceived.TrySetResult(data);
            dc1.Send(testData);

            var msgCompleted = await Task.WhenAny(messageReceived.Task, Task.Delay(5000));
            if (msgCompleted != messageReceived.Task) throw new Exception("Timed out waiting for binary message");
            var received = await messageReceived.Task;
            if (received.Length != testData.Length) throw new Exception($"Length mismatch: expected {testData.Length}, got {received.Length}");
            for (int i = 0; i < testData.Length; i++)
            {
                if (testData[i] != received[i]) throw new Exception($"Byte mismatch at index {i}: expected {testData[i]}, got {received[i]}");
            }
        }

        /// <summary>
        /// Verify bidirectional messaging works.
        /// </summary>
        [TestMethod]
        public async Task Loopback_DataChannel_Bidirectional()
        {
            var config = new RTCPeerConnectionConfig
            {
                IceServers = new[] { new RTCIceServerConfig { Urls = "stun:stun.l.google.com:19302" } }
            };

            using var pc1 = RTCPeerConnectionFactory.Create(config);
            using var pc2 = RTCPeerConnectionFactory.Create(config);

            pc1.OnIceCandidate += candidate => _ = pc2.AddIceCandidate(candidate);
            pc2.OnIceCandidate += candidate => _ = pc1.AddIceCandidate(candidate);

            var dc2Received = new TaskCompletionSource<IRTCDataChannel>();
            pc2.OnDataChannel += channel => dc2Received.TrySetResult(channel);

            using var dc1 = pc1.CreateDataChannel("bidi-test");

            var offer = await pc1.CreateOffer();
            await pc1.SetLocalDescription(offer);
            await pc2.SetRemoteDescription(offer);

            var answer = await pc2.CreateAnswer();
            await pc2.SetLocalDescription(answer);
            await pc1.SetRemoteDescription(answer);

            var completed = await Task.WhenAny(dc2Received.Task, Task.Delay(15000));
            if (completed != dc2Received.Task) throw new Exception("Timed out waiting for data channel");
            using var dc2 = await dc2Received.Task;

            var dc1Open = new TaskCompletionSource<bool>();
            var dc2Open = new TaskCompletionSource<bool>();
            if (dc1.ReadyState == "open") dc1Open.TrySetResult(true);
            else dc1.OnOpen += () => dc1Open.TrySetResult(true);
            if (dc2.ReadyState == "open") dc2Open.TrySetResult(true);
            else dc2.OnOpen += () => dc2Open.TrySetResult(true);
            await Task.WhenAny(Task.WhenAll(dc1Open.Task, dc2Open.Task), Task.Delay(15000));

            // pc1 -> pc2
            var msg1to2 = new TaskCompletionSource<string>();
            dc2.OnStringMessage += msg => msg1to2.TrySetResult(msg);
            dc1.Send("ping");
            var r1 = await Task.WhenAny(msg1to2.Task, Task.Delay(5000));
            if (r1 != msg1to2.Task) throw new Exception("Timed out waiting for ping");
            if (await msg1to2.Task != "ping") throw new Exception($"Expected 'ping', got '{await msg1to2.Task}'");

            // pc2 -> pc1
            var msg2to1 = new TaskCompletionSource<string>();
            dc1.OnStringMessage += msg => msg2to1.TrySetResult(msg);
            dc2.Send("pong");
            var r2 = await Task.WhenAny(msg2to1.Task, Task.Delay(5000));
            if (r2 != msg2to1.Task) throw new Exception("Timed out waiting for pong");
            if (await msg2to1.Task != "pong") throw new Exception($"Expected 'pong', got '{await msg2to1.Task}'");
        }
    }
}
