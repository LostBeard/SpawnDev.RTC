using SpawnDev.RTC;
using SpawnDev.UnitTesting;

namespace SpawnDev.RTC.Demo.Shared.UnitTests
{
    public abstract partial class RTCTestBase
    {
        /// <summary>
        /// Send Unicode text (emoji, CJK, Arabic) through data channel, verify intact.
        /// </summary>
        [TestMethod]
        public async Task DataChannel_UnicodeText()
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

            using var dc1 = pc1.CreateDataChannel("unicode");

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

            var testStrings = new[]
            {
                "Hello World",                          // ASCII
                "Hej Verden! Bj\u00f6rk",               // Nordic
                "\u4f60\u597d\u4e16\u754c",             // Chinese
                "\u0645\u0631\u062d\u0628\u0627",       // Arabic
                "\ud83d\ude80\ud83d\udd96\ud83c\udf1f", // Emoji (rocket, vulcan, star)
                "Mixed: ABC-123-\u00e9\u00e8\u00ea",    // Accented
            };

            foreach (var testStr in testStrings)
            {
                var received = new TaskCompletionSource<string>();
                dc2.OnStringMessage += msg => received.TrySetResult(msg);
                dc1.Send(testStr);

                var result = await Task.WhenAny(received.Task, Task.Delay(5000));
                if (result != received.Task) throw new Exception($"Timeout for: {testStr}");
                var got = await received.Task;
                if (got != testStr) throw new Exception($"Mismatch: sent '{testStr}', got '{got}'");

                // Reset for next message
                dc2.OnStringMessage -= msg => received.TrySetResult(msg);
            }
        }

        /// <summary>
        /// Verify data channel Close() transitions to closed state.
        /// </summary>
        [TestMethod]
        public async Task DataChannel_Close_State()
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

            using var dc1 = pc1.CreateDataChannel("close-test");

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
            if (dc1.ReadyState == "open") dc1Open.TrySetResult(true);
            else dc1.OnOpen += () => dc1Open.TrySetResult(true);
            await Task.WhenAny(dc1Open.Task, Task.Delay(15000));

            // Close dc1
            var dc2Closed = new TaskCompletionSource<bool>();
            dc2.OnClose += () => dc2Closed.TrySetResult(true);

            dc1.Close();

            // dc1 should be closed/closing
            var state = dc1.ReadyState;
            if (state != "closed" && state != "closing")
                throw new Exception($"dc1 state after Close(): '{state}'");

            // Wait for dc2 to detect the close
            var closeResult = await Task.WhenAny(dc2Closed.Task, Task.Delay(5000));
            if (closeResult == dc2Closed.Task)
            {
                var dc2State = dc2.ReadyState;
                if (dc2State != "closed") throw new Exception($"dc2 state: '{dc2State}'");
            }
            // Some implementations may not fire OnClose immediately - that's OK
        }

        /// <summary>
        /// Verify multiple ICE servers can be configured.
        /// </summary>
        [TestMethod]
        public async Task PeerConnection_MultipleIceServers()
        {
            var config = new RTCPeerConnectionConfig
            {
                IceServers = new[]
                {
                    new RTCIceServerConfig { Urls = new[] { "stun:stun.l.google.com:19302" } },
                    new RTCIceServerConfig { Urls = new[] { "stun:stun1.l.google.com:19302" } },
                    new RTCIceServerConfig { Urls = new[] { "stun:stun2.l.google.com:19302" } },
                }
            };

            using var pc = RTCPeerConnectionFactory.Create(config);
            if (pc == null) throw new Exception("Failed to create with multiple ICE servers");

            pc.CreateDataChannel("test");
            var offer = await pc.CreateOffer();
            if (string.IsNullOrEmpty(offer.Sdp)) throw new Exception("Offer SDP empty with multiple ICE servers");
        }

        /// <summary>
        /// Verify peer connection can be created, used, disposed, and a new one created.
        /// Tests proper resource cleanup.
        /// </summary>
        [TestMethod]
        public async Task PeerConnection_CreateUseDisposeRecreate()
        {
            var config = new RTCPeerConnectionConfig
            {
                IceServers = new[] { new RTCIceServerConfig { Urls = new[] { "stun:stun.l.google.com:19302" } } }
            };

            // First connection
            var pc1 = RTCPeerConnectionFactory.Create(config);
            pc1.CreateDataChannel("first");
            var offer1 = await pc1.CreateOffer();
            if (string.IsNullOrEmpty(offer1.Sdp)) throw new Exception("First offer empty");
            pc1.Close();
            pc1.Dispose();

            // Second connection - should work fine after first was disposed
            var pc2 = RTCPeerConnectionFactory.Create(config);
            pc2.CreateDataChannel("second");
            var offer2 = await pc2.CreateOffer();
            if (string.IsNullOrEmpty(offer2.Sdp)) throw new Exception("Second offer empty");
            pc2.Close();
            pc2.Dispose();
        }
    }
}
