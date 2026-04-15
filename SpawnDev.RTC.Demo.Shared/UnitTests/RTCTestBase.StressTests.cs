using SpawnDev.RTC;
using SpawnDev.UnitTesting;

namespace SpawnDev.RTC.Demo.Shared.UnitTests
{
    public abstract partial class RTCTestBase
    {
        /// <summary>
        /// Create 5 peer connections simultaneously, each with a data channel,
        /// verify all connect and exchange messages.
        /// </summary>
        [TestMethod]
        public async Task Stress_FivePeerPairs_AllConnect()
        {
            var config = new RTCPeerConnectionConfig
            {
                IceServers = new[] { new RTCIceServerConfig { Urls = new[] { "stun:stun.l.google.com:19302" } } }
            };

            var tasks = new List<Task>();
            for (int pair = 0; pair < 5; pair++)
            {
                var pairIndex = pair;
                tasks.Add(Task.Run(async () =>
                {
                    using var pc1 = RTCPeerConnectionFactory.Create(config);
                    using var pc2 = RTCPeerConnectionFactory.Create(config);

                    pc1.OnIceCandidate += c => _ = pc2.AddIceCandidate(c);
                    pc2.OnIceCandidate += c => _ = pc1.AddIceCandidate(c);

                    var dc2Received = new TaskCompletionSource<IRTCDataChannel>();
                    pc2.OnDataChannel += ch => dc2Received.TrySetResult(ch);

                    using var dc1 = pc1.CreateDataChannel($"pair-{pairIndex}");

                    var offer = await pc1.CreateOffer();
                    await pc1.SetLocalDescription(offer);
                    await pc2.SetRemoteDescription(offer);
                    var answer = await pc2.CreateAnswer();
                    await pc2.SetLocalDescription(answer);
                    await pc1.SetRemoteDescription(answer);

                    var completed = await Task.WhenAny(dc2Received.Task, Task.Delay(15000));
                    if (completed != dc2Received.Task) throw new Exception($"Pair {pairIndex}: timeout");
                    using var dc2 = await dc2Received.Task;

                    var dc1Open = new TaskCompletionSource<bool>();
                    var dc2Open = new TaskCompletionSource<bool>();
                    if (dc1.ReadyState == "open") dc1Open.TrySetResult(true);
                    else dc1.OnOpen += () => dc1Open.TrySetResult(true);
                    if (dc2.ReadyState == "open") dc2Open.TrySetResult(true);
                    else dc2.OnOpen += () => dc2Open.TrySetResult(true);
                    await Task.WhenAny(Task.WhenAll(dc1Open.Task, dc2Open.Task), Task.Delay(15000));

                    var msgReceived = new TaskCompletionSource<string>();
                    dc2.OnStringMessage += msg => msgReceived.TrySetResult(msg);
                    dc1.Send($"hello from pair {pairIndex}");

                    var msgResult = await Task.WhenAny(msgReceived.Task, Task.Delay(5000));
                    if (msgResult != msgReceived.Task) throw new Exception($"Pair {pairIndex}: message timeout");
                    var received = await msgReceived.Task;
                    if (received != $"hello from pair {pairIndex}")
                        throw new Exception($"Pair {pairIndex}: expected 'hello from pair {pairIndex}', got '{received}'");
                }));
            }

            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// Send 256KB binary payload (max typical SCTP message) through data channel.
        /// </summary>
        [TestMethod]
        public async Task DataChannel_MaxSizePayload_256KB()
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

            using var dc1 = pc1.CreateDataChannel("max-size");

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

            // 256KB payload - max typical SCTP message
            var payload = new byte[256 * 1024];
            for (int i = 0; i < payload.Length; i++)
                payload[i] = (byte)(i % 251); // Prime modulus for better pattern

            var received = new TaskCompletionSource<byte[]>();
            dc2.OnBinaryMessage += data => received.TrySetResult(data);

            dc1.Send(payload);

            var result = await Task.WhenAny(received.Task, Task.Delay(15000));
            if (result != received.Task) throw new Exception("256KB payload not received");

            var data = await received.Task;
            if (data.Length != payload.Length)
                throw new Exception($"Size mismatch: sent {payload.Length}, received {data.Length}");

            // Verify first, middle, last bytes
            if (data[0] != 0) throw new Exception($"First byte: {data[0]}");
            if (data[128 * 1024] != (byte)((128 * 1024) % 251)) throw new Exception("Middle byte mismatch");
            if (data[data.Length - 1] != (byte)((data.Length - 1) % 251)) throw new Exception("Last byte mismatch");
        }

        /// <summary>
        /// Rapidly create and close data channels to test lifecycle management.
        /// </summary>
        [TestMethod]
        public async Task DataChannel_RapidCreateClose()
        {
            using var pc = RTCPeerConnectionFactory.Create();

            // Create and dispose 20 channels rapidly
            var channels = new List<IRTCDataChannel>();
            for (int i = 0; i < 20; i++)
            {
                channels.Add(pc.CreateDataChannel($"rapid-{i}"));
            }

            // Verify all created with correct labels
            for (int i = 0; i < 20; i++)
            {
                if (channels[i].Label != $"rapid-{i}")
                    throw new Exception($"Channel {i} label: '{channels[i].Label}'");
            }

            // Close and dispose all
            foreach (var ch in channels)
            {
                ch.Close();
                ch.Dispose();
            }

            await Task.CompletedTask;
        }
    }
}
