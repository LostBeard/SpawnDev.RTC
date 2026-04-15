using SpawnDev.RTC;
using SpawnDev.UnitTesting;
using System.Security.Cryptography;

namespace SpawnDev.RTC.Demo.Shared.UnitTests
{
    public abstract partial class RTCTestBase
    {
        /// <summary>
        /// Send a known byte pattern with SHA-256 checksum through WebRTC data channel,
        /// verify the exact bytes arrive on the other side with matching checksum.
        /// </summary>
        [TestMethod]
        public async Task DataChannel_VerifiedBinaryPayload_SHA256()
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

            using var dc1 = pc1.CreateDataChannel("verified");

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

            // Generate 32KB of deterministic test data
            var testData = new byte[32 * 1024];
            for (int i = 0; i < testData.Length; i++)
                testData[i] = (byte)((i * 7 + 13) % 256); // Deterministic pattern

            var expectedHash = Convert.ToHexString(SHA256.HashData(testData));

            var received = new TaskCompletionSource<byte[]>();
            dc2.OnBinaryMessage += data => received.TrySetResult(data);

            dc1.Send(testData);

            var result = await Task.WhenAny(received.Task, Task.Delay(10000));
            if (result != received.Task) throw new Exception("Payload not received");

            var receivedData = await received.Task;
            var receivedHash = Convert.ToHexString(SHA256.HashData(receivedData));

            if (receivedData.Length != testData.Length)
                throw new Exception($"Size mismatch: sent {testData.Length}, received {receivedData.Length}");

            if (receivedHash != expectedHash)
                throw new Exception($"SHA-256 mismatch!\nExpected: {expectedHash}\nReceived: {receivedHash}");
        }

        /// <summary>
        /// Send multiple chunks of verified data in sequence, verify all arrive
        /// in order with correct content.
        /// </summary>
        [TestMethod]
        public async Task DataChannel_VerifiedMultiChunk_Ordered()
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

            using var dc1 = pc1.CreateDataChannel("multi-chunk");

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

            // Send 50 chunks, each 1KB with a unique pattern
            const int chunkCount = 50;
            const int chunkSize = 1024;
            var receivedChunks = new List<byte[]>();
            var allReceived = new TaskCompletionSource<bool>();

            dc2.OnBinaryMessage += data =>
            {
                lock (receivedChunks)
                {
                    receivedChunks.Add(data);
                    if (receivedChunks.Count >= chunkCount)
                        allReceived.TrySetResult(true);
                }
            };

            for (int i = 0; i < chunkCount; i++)
            {
                var chunk = new byte[chunkSize];
                // Each chunk starts with its index as a 4-byte header
                BitConverter.GetBytes(i).CopyTo(chunk, 0);
                // Fill rest with deterministic pattern based on chunk index
                for (int j = 4; j < chunkSize; j++)
                    chunk[j] = (byte)((i * 17 + j * 3) % 256);
                dc1.Send(chunk);
            }

            var result = await Task.WhenAny(allReceived.Task, Task.Delay(10000));
            if (result != allReceived.Task)
                throw new Exception($"Only received {receivedChunks.Count}/{chunkCount} chunks");

            // Verify each chunk arrived in order with correct content
            for (int i = 0; i < chunkCount; i++)
            {
                var chunk = receivedChunks[i];
                if (chunk.Length != chunkSize)
                    throw new Exception($"Chunk {i} size: {chunk.Length}, expected {chunkSize}");

                var receivedIndex = BitConverter.ToInt32(chunk, 0);
                if (receivedIndex != i)
                    throw new Exception($"Chunk {i} has index {receivedIndex} - out of order!");

                // Verify pattern
                for (int j = 4; j < chunkSize; j++)
                {
                    var expected = (byte)((i * 17 + j * 3) % 256);
                    if (chunk[j] != expected)
                        throw new Exception($"Chunk {i} byte {j}: expected {expected}, got {chunk[j]}");
                }
            }
        }

        /// <summary>
        /// Send and receive data bidirectionally at the same time,
        /// verify both directions arrive correctly.
        /// </summary>
        [TestMethod]
        public async Task DataChannel_BidirectionalSimultaneous_Verified()
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

            using var dc1 = pc1.CreateDataChannel("bidi-verified");

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

            const int messageCount = 30;
            var from1to2 = new List<string>();
            var from2to1 = new List<string>();
            var all1to2 = new TaskCompletionSource<bool>();
            var all2to1 = new TaskCompletionSource<bool>();

            dc2.OnStringMessage += msg =>
            {
                lock (from1to2)
                {
                    from1to2.Add(msg);
                    if (from1to2.Count >= messageCount) all1to2.TrySetResult(true);
                }
            };

            dc1.OnStringMessage += msg =>
            {
                lock (from2to1)
                {
                    from2to1.Add(msg);
                    if (from2to1.Count >= messageCount) all2to1.TrySetResult(true);
                }
            };

            // Send from both sides simultaneously
            for (int i = 0; i < messageCount; i++)
            {
                dc1.Send($"1to2-{i:D4}");
                dc2.Send($"2to1-{i:D4}");
            }

            await Task.WhenAny(Task.WhenAll(all1to2.Task, all2to1.Task), Task.Delay(10000));

            if (!all1to2.Task.IsCompletedSuccessfully)
                throw new Exception($"1->2: only {from1to2.Count}/{messageCount}");
            if (!all2to1.Task.IsCompletedSuccessfully)
                throw new Exception($"2->1: only {from2to1.Count}/{messageCount}");

            // Verify order and content
            for (int i = 0; i < messageCount; i++)
            {
                if (from1to2[i] != $"1to2-{i:D4}")
                    throw new Exception($"1->2 msg {i}: expected '1to2-{i:D4}', got '{from1to2[i]}'");
                if (from2to1[i] != $"2to1-{i:D4}")
                    throw new Exception($"2->1 msg {i}: expected '2to1-{i:D4}', got '{from2to1[i]}'");
            }
        }
    }
}
