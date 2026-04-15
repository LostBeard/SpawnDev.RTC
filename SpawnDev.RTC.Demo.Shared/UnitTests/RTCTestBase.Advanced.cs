using SpawnDev.RTC;
using SpawnDev.UnitTesting;

namespace SpawnDev.RTC.Demo.Shared.UnitTests
{
    public abstract partial class RTCTestBase
    {
        /// <summary>
        /// Test multiple data channels on the same peer connection.
        /// </summary>
        [TestMethod]
        public async Task Loopback_MultipleDataChannels()
        {
            var config = new RTCPeerConnectionConfig
            {
                IceServers = new[] { new RTCIceServerConfig { Urls = "stun:stun.l.google.com:19302" } }
            };

            using var pc1 = RTCPeerConnectionFactory.Create(config);
            using var pc2 = RTCPeerConnectionFactory.Create(config);

            pc1.OnIceCandidate += candidate => _ = pc2.AddIceCandidate(candidate);
            pc2.OnIceCandidate += candidate => _ = pc1.AddIceCandidate(candidate);

            var dc2Channels = new List<IRTCDataChannel>();
            var allChannelsReceived = new TaskCompletionSource<bool>();
            int channelCount = 0;
            pc2.OnDataChannel += channel =>
            {
                dc2Channels.Add(channel);
                if (Interlocked.Increment(ref channelCount) >= 3)
                    allChannelsReceived.TrySetResult(true);
            };

            // Create 3 data channels
            using var dcA = pc1.CreateDataChannel("channel-a");
            using var dcB = pc1.CreateDataChannel("channel-b");
            using var dcC = pc1.CreateDataChannel("channel-c");

            var offer = await pc1.CreateOffer();
            await pc1.SetLocalDescription(offer);
            await pc2.SetRemoteDescription(offer);
            var answer = await pc2.CreateAnswer();
            await pc2.SetLocalDescription(answer);
            await pc1.SetRemoteDescription(answer);

            // Wait for all 3 channels to arrive on pc2
            var completed = await Task.WhenAny(allChannelsReceived.Task, Task.Delay(15000));
            if (completed != allChannelsReceived.Task) throw new Exception($"Only {channelCount}/3 channels received");

            // Verify labels
            var labels = dc2Channels.Select(c => c.Label).OrderBy(l => l).ToArray();
            if (labels.Length != 3) throw new Exception($"Expected 3 channels, got {labels.Length}");
            if (labels[0] != "channel-a") throw new Exception($"Expected 'channel-a', got '{labels[0]}'");
            if (labels[1] != "channel-b") throw new Exception($"Expected 'channel-b', got '{labels[1]}'");
            if (labels[2] != "channel-c") throw new Exception($"Expected 'channel-c', got '{labels[2]}'");

            // Wait for all to open
            var openTasks = new List<Task>();
            foreach (var dc in new[] { dcA, dcB, dcC })
            {
                var tcs = new TaskCompletionSource<bool>();
                if (dc.ReadyState == "open") tcs.TrySetResult(true);
                else dc.OnOpen += () => tcs.TrySetResult(true);
                openTasks.Add(tcs.Task);
            }
            foreach (var dc in dc2Channels)
            {
                var tcs = new TaskCompletionSource<bool>();
                if (dc.ReadyState == "open") tcs.TrySetResult(true);
                else dc.OnOpen += () => tcs.TrySetResult(true);
                openTasks.Add(tcs.Task);
            }
            await Task.WhenAny(Task.WhenAll(openTasks), Task.Delay(15000));

            // Send a message on each channel and verify independent delivery
            var msgA = new TaskCompletionSource<string>();
            var msgB = new TaskCompletionSource<string>();
            var msgC = new TaskCompletionSource<string>();

            foreach (var dc in dc2Channels)
            {
                dc.OnStringMessage += msg =>
                {
                    if (dc.Label == "channel-a") msgA.TrySetResult(msg);
                    else if (dc.Label == "channel-b") msgB.TrySetResult(msg);
                    else if (dc.Label == "channel-c") msgC.TrySetResult(msg);
                };
            }

            dcA.Send("msg-a");
            dcB.Send("msg-b");
            dcC.Send("msg-c");

            await Task.WhenAny(Task.WhenAll(msgA.Task, msgB.Task, msgC.Task), Task.Delay(5000));
            if (!msgA.Task.IsCompletedSuccessfully) throw new Exception("channel-a message not received");
            if (!msgB.Task.IsCompletedSuccessfully) throw new Exception("channel-b message not received");
            if (!msgC.Task.IsCompletedSuccessfully) throw new Exception("channel-c message not received");
            if (await msgA.Task != "msg-a") throw new Exception($"channel-a: expected 'msg-a', got '{await msgA.Task}'");
            if (await msgB.Task != "msg-b") throw new Exception($"channel-b: expected 'msg-b', got '{await msgB.Task}'");
            if (await msgC.Task != "msg-c") throw new Exception($"channel-c: expected 'msg-c', got '{await msgC.Task}'");

            foreach (var dc in dc2Channels) dc.Dispose();
        }

        /// <summary>
        /// Test sending a large message (64KB) through a data channel.
        /// </summary>
        [TestMethod]
        public async Task Loopback_DataChannel_LargeMessage()
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

            using var dc1 = pc1.CreateDataChannel("large-msg");

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

            // Create 64KB of test data with a known pattern
            var testData = new byte[64 * 1024];
            for (int i = 0; i < testData.Length; i++)
                testData[i] = (byte)(i % 256);

            var messageReceived = new TaskCompletionSource<byte[]>();
            dc2.OnBinaryMessage += data => messageReceived.TrySetResult(data);

            dc1.Send(testData);

            var msgCompleted = await Task.WhenAny(messageReceived.Task, Task.Delay(10000));
            if (msgCompleted != messageReceived.Task) throw new Exception("Timed out waiting for large message");
            var received = await messageReceived.Task;

            if (received.Length != testData.Length)
                throw new Exception($"Size mismatch: sent {testData.Length}, received {received.Length}");

            // Verify first, middle, and last bytes
            if (received[0] != 0) throw new Exception($"First byte wrong: expected 0, got {received[0]}");
            if (received[32768] != 0) throw new Exception($"Middle byte wrong: expected 0, got {received[32768]}");
            if (received[received.Length - 1] != 255) throw new Exception($"Last byte wrong: expected 255, got {received[received.Length - 1]}");

            // Full verification
            for (int i = 0; i < testData.Length; i++)
            {
                if (testData[i] != received[i])
                    throw new Exception($"Byte mismatch at index {i}: expected {testData[i]}, got {received[i]}");
            }
        }

        /// <summary>
        /// Test connection state transitions during peer connection lifecycle.
        /// </summary>
        [TestMethod]
        public async Task Loopback_ConnectionStateChanges()
        {
            var config = new RTCPeerConnectionConfig
            {
                IceServers = new[] { new RTCIceServerConfig { Urls = "stun:stun.l.google.com:19302" } }
            };

            using var pc1 = RTCPeerConnectionFactory.Create(config);
            using var pc2 = RTCPeerConnectionFactory.Create(config);

            var pc1States = new List<string>();
            var pc2States = new List<string>();
            var pc1Connected = new TaskCompletionSource<bool>();
            var pc2Connected = new TaskCompletionSource<bool>();

            pc1.OnConnectionStateChange += state =>
            {
                pc1States.Add(state);
                if (state == "connected") pc1Connected.TrySetResult(true);
            };
            pc2.OnConnectionStateChange += state =>
            {
                pc2States.Add(state);
                if (state == "connected") pc2Connected.TrySetResult(true);
            };

            pc1.OnIceCandidate += candidate => _ = pc2.AddIceCandidate(candidate);
            pc2.OnIceCandidate += candidate => _ = pc1.AddIceCandidate(candidate);

            var dc2Received = new TaskCompletionSource<IRTCDataChannel>();
            pc2.OnDataChannel += channel => dc2Received.TrySetResult(channel);

            pc1.CreateDataChannel("state-test");

            var offer = await pc1.CreateOffer();
            await pc1.SetLocalDescription(offer);
            await pc2.SetRemoteDescription(offer);
            var answer = await pc2.CreateAnswer();
            await pc2.SetLocalDescription(answer);
            await pc1.SetRemoteDescription(answer);

            // Wait for both to reach connected state
            await Task.WhenAny(Task.WhenAll(pc1Connected.Task, pc2Connected.Task), Task.Delay(15000));

            if (!pc1Connected.Task.IsCompletedSuccessfully) throw new Exception("pc1 never reached 'connected'");
            if (!pc2Connected.Task.IsCompletedSuccessfully) throw new Exception("pc2 never reached 'connected'");

            // Verify we saw the connected state
            if (!pc1States.Contains("connected")) throw new Exception($"pc1 states missing 'connected': [{string.Join(", ", pc1States)}]");
            if (!pc2States.Contains("connected")) throw new Exception($"pc2 states missing 'connected': [{string.Join(", ", pc2States)}]");
        }

        /// <summary>
        /// Test rapid message sending - send 100 messages and verify all received.
        /// </summary>
        [TestMethod]
        public async Task Loopback_DataChannel_RapidMessages()
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

            using var dc1 = pc1.CreateDataChannel("rapid");

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

            // Send 100 messages rapidly
            const int messageCount = 100;
            var receivedMessages = new List<string>();
            var allReceived = new TaskCompletionSource<bool>();

            dc2.OnStringMessage += msg =>
            {
                lock (receivedMessages)
                {
                    receivedMessages.Add(msg);
                    if (receivedMessages.Count >= messageCount)
                        allReceived.TrySetResult(true);
                }
            };

            for (int i = 0; i < messageCount; i++)
            {
                dc1.Send($"msg-{i}");
            }

            var msgCompleted = await Task.WhenAny(allReceived.Task, Task.Delay(10000));
            if (msgCompleted != allReceived.Task)
                throw new Exception($"Only received {receivedMessages.Count}/{messageCount} messages");

            // Verify order preserved (data channels are ordered by default)
            for (int i = 0; i < messageCount; i++)
            {
                if (receivedMessages[i] != $"msg-{i}")
                    throw new Exception($"Message {i} out of order: expected 'msg-{i}', got '{receivedMessages[i]}'");
            }
        }
    }
}
