using SpawnDev.RTC;
using SpawnDev.UnitTesting;
using SpawnDev.BlazorJS;
using SpawnDev.BlazorJS.JSObjects;

namespace SpawnDev.RTC.Demo.Shared.UnitTests
{
    public abstract partial class RTCTestBase
    {
        /// <summary>
        /// Verify HTMLVideoElement can be created and a MediaStream attached via srcObject.
        /// </summary>
        [TestMethod]
        public async Task Video_SrcObject_AttachStream()
        {
            if (!OperatingSystem.IsBrowser()) return;

            try
            {
                var stream = await RTCMediaDevices.GetUserMedia(new MediaStreamConstraints { Video = true });
                var browserStream = ((SpawnDev.RTC.Browser.BrowserRTCMediaStream)stream).NativeStream;

                using var video = new HTMLVideoElement();
                video.SrcObject = browserStream;
                await video.Play();

                // Verify stream is attached
                using var readBack = video.GetSrcObject<MediaStream>();
                if (readBack == null) throw new Exception("SrcObject is null after setting it");

                video.SrcObject = null;
                stream.Dispose();
            }
            catch (Exception ex) when (ex.Message.Contains("NotAllowedError"))
            {
                throw new Exception($"SKIP: Camera not available: {ex.Message}");
            }
        }

        /// <summary>
        /// Send fake camera video through WebRTC loopback, receive on other side,
        /// render to HTMLVideoElement, verify dimensions are non-zero (frames arriving).
        /// Playwright provides a fake camera via --use-fake-device-for-media-stream.
        /// </summary>
        [TestMethod]
        public async Task Video_Loopback_FakeCameraFramesReceived()
        {
            if (!OperatingSystem.IsBrowser()) return;

            try
            {
                // Get fake camera stream (Playwright provides this)
                var stream = await RTCMediaDevices.GetUserMedia(new MediaStreamConstraints { Video = true });
                var browserStream = ((SpawnDev.RTC.Browser.BrowserRTCMediaStream)stream).NativeStream;
                var videoTrack = browserStream.GetVideoTracks()[0];

                var config = new RTCPeerConnectionConfig
                {
                    IceServers = new[] { new RTCIceServerConfig { Urls = new[] { "stun:stun.l.google.com:19302" } } }
                };

                using var pc1 = RTCPeerConnectionFactory.Create(config);
                using var pc2 = RTCPeerConnectionFactory.Create(config);

                pc1.OnIceCandidate += c => _ = pc2.AddIceCandidate(c);
                pc2.OnIceCandidate += c => _ = pc1.AddIceCandidate(c);

                // Add video track to pc1
                var bpc1 = (SpawnDev.RTC.Browser.BrowserRTCPeerConnection)pc1;
                bpc1.NativeConnection.AddTrack(videoTrack, browserStream);

                // Need a data channel for negotiation
                pc1.CreateDataChannel("signal");
                pc2.OnDataChannel += _ => { };

                // Listen for track on pc2
                var trackReceived = new TaskCompletionSource<MediaStreamTrack>();
                var bpc2 = (SpawnDev.RTC.Browser.BrowserRTCPeerConnection)pc2;
                bpc2.NativeConnection.OnTrack += e =>
                {
                    if (e.Track.Kind == "video")
                        trackReceived.TrySetResult(e.Track);
                };

                var offer = await pc1.CreateOffer();
                await pc1.SetLocalDescription(offer);
                await pc2.SetRemoteDescription(offer);
                var answer = await pc2.CreateAnswer();
                await pc2.SetLocalDescription(answer);
                await pc1.SetRemoteDescription(answer);

                // Wait for video track
                var result = await Task.WhenAny(trackReceived.Task, Task.Delay(15000));
                if (result != trackReceived.Task) throw new Exception("Video track not received on pc2");

                var remoteTrack = await trackReceived.Task;
                if (remoteTrack.Kind != "video") throw new Exception($"Expected video, got {remoteTrack.Kind}");

                // Attach to a video element
                using var video = new HTMLVideoElement();
                var remoteStream = new MediaStream();
                remoteStream.AddTrack(remoteTrack);
                video.SrcObject = remoteStream;
                await video.Play();

                // Wait for frames to decode
                await Task.Delay(1000);

                // Verify video has dimensions (frames are arriving and decoding)
                var width = video.VideoWidth;
                var height = video.VideoHeight;

                if (width == 0 || height == 0)
                    throw new Exception($"Received video has no dimensions: {width}x{height} - frames not arriving");

                // Success - video frames are being received and decoded
                stream.Dispose();
            }
            catch (Exception ex) when (ex.Message.Contains("NotAllowedError"))
            {
                throw new Exception($"SKIP: Camera not available: {ex.Message}");
            }
        }
    }
}
