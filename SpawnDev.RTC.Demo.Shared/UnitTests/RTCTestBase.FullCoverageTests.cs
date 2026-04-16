using SpawnDev.RTC;
using SpawnDev.UnitTesting;

namespace SpawnDev.RTC.Demo.Shared.UnitTests
{
    public abstract partial class RTCTestBase
    {
        /// <summary>
        /// Audio + video + data channel all in one connection simultaneously.
        /// </summary>
        [TestMethod]
        public async Task FullConnection_AudioVideoData_Simultaneous()
        {
            if (!OperatingSystem.IsBrowser()) return;

            try
            {
                var stream = await RTCMediaDevices.GetUserMedia(new MediaStreamConstraints { Audio = true, Video = true });
                var browserStream = ((SpawnDev.RTC.Browser.BrowserRTCMediaStream)stream).NativeStream;

                var config = new RTCPeerConnectionConfig
                {
                    IceServers = new[] { new RTCIceServerConfig { Urls = new[] { "stun:stun.l.google.com:19302" } } }
                };

                using var pc1 = RTCPeerConnectionFactory.Create(config);
                using var pc2 = RTCPeerConnectionFactory.Create(config);

                pc1.OnIceCandidate += c => _ = pc2.AddIceCandidate(c);
                pc2.OnIceCandidate += c => _ = pc1.AddIceCandidate(c);

                // Add audio + video tracks
                var bpc1 = (SpawnDev.RTC.Browser.BrowserRTCPeerConnection)pc1;
                foreach (var track in browserStream.GetTracks())
                    bpc1.NativeConnection.AddTrack(track, browserStream);

                // Add data channel
                using var dc1 = pc1.CreateDataChannel("chat");

                // Track what pc2 receives
                var audioReceived = new TaskCompletionSource<bool>();
                var videoReceived = new TaskCompletionSource<bool>();
                var dcReceived = new TaskCompletionSource<IRTCDataChannel>();

                var bpc2 = (SpawnDev.RTC.Browser.BrowserRTCPeerConnection)pc2;
                bpc2.NativeConnection.OnTrack += e =>
                {
                    if (e.Track.Kind == "audio") audioReceived.TrySetResult(true);
                    if (e.Track.Kind == "video") videoReceived.TrySetResult(true);
                };
                pc2.OnDataChannel += ch => dcReceived.TrySetResult(ch);

                var offer = await pc1.CreateOffer();
                await pc1.SetLocalDescription(offer);
                await pc2.SetRemoteDescription(offer);
                var answer = await pc2.CreateAnswer();
                await pc2.SetLocalDescription(answer);
                await pc1.SetRemoteDescription(answer);

                // Wait for all three
                var all = Task.WhenAll(audioReceived.Task, videoReceived.Task, dcReceived.Task);
                var result = await Task.WhenAny(all, Task.Delay(15000));
                if (result != all)
                {
                    var a = audioReceived.Task.IsCompletedSuccessfully;
                    var v = videoReceived.Task.IsCompletedSuccessfully;
                    var d = dcReceived.Task.IsCompletedSuccessfully;
                    throw new Exception($"Timeout. Audio: {a}, Video: {v}, Data: {d}");
                }

                // Verify data channel works
                var dc2 = await dcReceived.Task;
                var msgReceived = new TaskCompletionSource<string>();
                dc2.OnStringMessage += m => msgReceived.TrySetResult(m);

                var dcOpen = new TaskCompletionSource<bool>();
                if (dc1.ReadyState == "open") dcOpen.TrySetResult(true);
                else dc1.OnOpen += () => dcOpen.TrySetResult(true);
                await Task.WhenAny(dcOpen.Task, Task.Delay(5000));

                dc1.Send("all-three-working");
                var mr = await Task.WhenAny(msgReceived.Task, Task.Delay(5000));
                if (mr != msgReceived.Task) throw new Exception("Data channel message timeout");
                if (await msgReceived.Task != "all-three-working") throw new Exception($"Got: {await msgReceived.Task}");

                stream.Dispose();
            }
            catch (Exception ex) when (ex.Message.Contains("NotAllowedError"))
            {
                throw new Exception($"SKIP: Camera/mic not available: {ex.Message}");
            }
        }

        /// <summary>
        /// Verify OnNegotiationNeeded fires when adding a track.
        /// </summary>
        [TestMethod]
        public async Task Event_NegotiationNeeded_FiresOnAddTrack()
        {
            if (!OperatingSystem.IsBrowser()) return;

            try
            {
                var stream = await RTCMediaDevices.GetUserMedia(new MediaStreamConstraints { Audio = true });

                using var pc = RTCPeerConnectionFactory.Create();

                var fired = new TaskCompletionSource<bool>();
                pc.OnNegotiationNeeded += () => fired.TrySetResult(true);

                pc.AddTrack(stream.GetAudioTracks()[0]);

                var result = await Task.WhenAny(fired.Task, Task.Delay(5000));
                if (result != fired.Task) throw new Exception("OnNegotiationNeeded did not fire after AddTrack");

                stream.Dispose();
            }
            catch (Exception ex) when (ex.Message.Contains("NotAllowedError"))
            {
                throw new Exception($"SKIP: Mic not available: {ex.Message}");
            }
        }

        /// <summary>
        /// Verify MediaStreamTrack.GetSettings returns real values from fake camera.
        /// </summary>
        [TestMethod]
        public async Task MediaStreamTrack_GetSettings_HasValues()
        {
            if (!OperatingSystem.IsBrowser()) return;

            try
            {
                var stream = await RTCMediaDevices.GetUserMedia(new MediaStreamConstraints { Video = true });
                var track = stream.GetVideoTracks()[0];
                var settings = track.GetSettings();

                if (settings.Width == null || settings.Width == 0)
                    throw new Exception($"Width is {settings.Width}");
                if (settings.Height == null || settings.Height == 0)
                    throw new Exception($"Height is {settings.Height}");

                stream.Dispose();
            }
            catch (Exception ex) when (ex.Message.Contains("NotAllowedError"))
            {
                throw new Exception($"SKIP: Camera not available: {ex.Message}");
            }
        }

        /// <summary>
        /// Verify transceiver direction shows in SDP offer.
        /// </summary>
        [TestMethod]
        public async Task Transceiver_Direction_InSDP()
        {
            if (!OperatingSystem.IsBrowser()) return; // SipSorcery doesn't map transceiver direction to SDP
            using var pc = RTCPeerConnectionFactory.Create();

            var t = pc.AddTransceiver("audio");
            t.Direction = "recvonly";

            var offer = await pc.CreateOffer();

            if (!offer.Sdp.Contains("a=recvonly"))
                throw new Exception("SDP missing a=recvonly for recvonly transceiver");
        }

        /// <summary>
        /// Verify SDP answer contains expected fields.
        /// </summary>
        [TestMethod]
        public async Task SDP_AnswerContent_Verification()
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

            pc1.CreateDataChannel("sdp-answer-test");

            var offer = await pc1.CreateOffer();
            await pc1.SetLocalDescription(offer);
            await pc2.SetRemoteDescription(offer);
            var answer = await pc2.CreateAnswer();

            var sdp = answer.Sdp;
            if (answer.Type != "answer") throw new Exception($"Type: {answer.Type}");
            if (!sdp.Contains("v=0")) throw new Exception("Missing v=0");
            if (!sdp.Contains("a=ice-ufrag:")) throw new Exception("Missing ICE ufrag");
            if (!sdp.Contains("a=ice-pwd:")) throw new Exception("Missing ICE pwd");
            if (!sdp.Contains("a=fingerprint:")) throw new Exception("Missing fingerprint");
        }

        /// <summary>
        /// Verify empty binary message (zero bytes) transfers correctly.
        /// </summary>
        [TestMethod]
        public async Task DataChannel_EmptyBinaryMessage()
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

            using var dc1 = pc1.CreateDataChannel("empty-binary");

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

            var received = new TaskCompletionSource<byte[]>();
            dc2.OnBinaryMessage += data => received.TrySetResult(data);

            dc1.Send(new byte[0]);

            var result = await Task.WhenAny(received.Task, Task.Delay(5000));
            if (result != received.Task) throw new Exception("Empty binary not received");
            if ((await received.Task).Length != 0) throw new Exception($"Expected 0 bytes, got {(await received.Task).Length}");
        }
    }
}
