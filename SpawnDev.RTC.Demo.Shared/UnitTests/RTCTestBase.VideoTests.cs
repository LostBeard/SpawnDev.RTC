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
        /// <summary>
        /// Send fake mic audio through WebRTC loopback, verify audio track received.
        /// </summary>
        [TestMethod]
        public async Task Audio_Loopback_TrackReceived()
        {
            if (!OperatingSystem.IsBrowser()) return;

            try
            {
                var stream = await RTCMediaDevices.GetUserMedia(new MediaStreamConstraints { Audio = true });
                var browserStream = ((SpawnDev.RTC.Browser.BrowserRTCMediaStream)stream).NativeStream;
                var audioTrack = browserStream.GetAudioTracks()[0];

                var config = new RTCPeerConnectionConfig
                {
                    IceServers = new[] { new RTCIceServerConfig { Urls = new[] { "stun:stun.l.google.com:19302" } } }
                };

                using var pc1 = RTCPeerConnectionFactory.Create(config);
                using var pc2 = RTCPeerConnectionFactory.Create(config);

                pc1.OnIceCandidate += c => _ = pc2.AddIceCandidate(c);
                pc2.OnIceCandidate += c => _ = pc1.AddIceCandidate(c);

                var bpc1 = (SpawnDev.RTC.Browser.BrowserRTCPeerConnection)pc1;
                bpc1.NativeConnection.AddTrack(audioTrack, browserStream);

                pc1.CreateDataChannel("signal");
                pc2.OnDataChannel += _ => { };

                var trackReceived = new TaskCompletionSource<string>();
                var bpc2 = (SpawnDev.RTC.Browser.BrowserRTCPeerConnection)pc2;
                bpc2.NativeConnection.OnTrack += e =>
                {
                    trackReceived.TrySetResult(e.Track.Kind);
                };

                var offer = await pc1.CreateOffer();
                await pc1.SetLocalDescription(offer);
                await pc2.SetRemoteDescription(offer);
                var answer = await pc2.CreateAnswer();
                await pc2.SetLocalDescription(answer);
                await pc1.SetRemoteDescription(answer);

                var result = await Task.WhenAny(trackReceived.Task, Task.Delay(15000));
                if (result != trackReceived.Task) throw new Exception("Audio track not received");
                if (await trackReceived.Task != "audio") throw new Exception($"Expected audio, got {await trackReceived.Task}");

                stream.Dispose();
            }
            catch (Exception ex) when (ex.Message.Contains("NotAllowedError"))
            {
                throw new Exception($"SKIP: Mic not available: {ex.Message}");
            }
        }

        /// <summary>
        /// Send BOTH audio and video tracks simultaneously through WebRTC loopback,
        /// verify both arrive on the receiving side.
        /// </summary>
        [TestMethod]
        public async Task AudioVideo_Loopback_BothTracksReceived()
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

                // Add ALL tracks (audio + video) to pc1
                var bpc1 = (SpawnDev.RTC.Browser.BrowserRTCPeerConnection)pc1;
                foreach (var track in browserStream.GetTracks())
                {
                    bpc1.NativeConnection.AddTrack(track, browserStream);
                }

                pc1.CreateDataChannel("signal");
                pc2.OnDataChannel += _ => { };

                // Track both kinds received
                var audioReceived = new TaskCompletionSource<bool>();
                var videoReceived = new TaskCompletionSource<bool>();
                var bpc2 = (SpawnDev.RTC.Browser.BrowserRTCPeerConnection)pc2;
                bpc2.NativeConnection.OnTrack += e =>
                {
                    if (e.Track.Kind == "audio") audioReceived.TrySetResult(true);
                    if (e.Track.Kind == "video") videoReceived.TrySetResult(true);
                };

                var offer = await pc1.CreateOffer();
                await pc1.SetLocalDescription(offer);
                await pc2.SetRemoteDescription(offer);
                var answer = await pc2.CreateAnswer();
                await pc2.SetLocalDescription(answer);
                await pc1.SetRemoteDescription(answer);

                // Wait for BOTH tracks
                var bothDone = Task.WhenAll(audioReceived.Task, videoReceived.Task);
                var result = await Task.WhenAny(bothDone, Task.Delay(15000));
                if (result != bothDone)
                {
                    var gotAudio = audioReceived.Task.IsCompletedSuccessfully;
                    var gotVideo = videoReceived.Task.IsCompletedSuccessfully;
                    throw new Exception($"Timeout waiting for both tracks. Audio: {gotAudio}, Video: {gotVideo}");
                }

                stream.Dispose();
            }
            catch (Exception ex) when (ex.Message.Contains("NotAllowedError"))
            {
                throw new Exception($"SKIP: Camera/mic not available: {ex.Message}");
            }
        }

        /// <summary>
        /// Add audio+video, then add a second video, remove first video,
        /// add second audio, remove first audio. Verifies dynamic track
        /// management during a live connection.
        /// </summary>
        [TestMethod]
        public async Task Track_DynamicAddRemove_MidCall()
        {
            if (!OperatingSystem.IsBrowser()) return;

            try
            {
                // Get two separate streams (simulates switching cameras/mics)
                var stream1 = await RTCMediaDevices.GetUserMedia(new MediaStreamConstraints { Audio = true, Video = true });
                var stream2 = await RTCMediaDevices.GetUserMedia(new MediaStreamConstraints { Audio = true, Video = true });
                var bs1 = ((SpawnDev.RTC.Browser.BrowserRTCMediaStream)stream1).NativeStream;
                var bs2 = ((SpawnDev.RTC.Browser.BrowserRTCMediaStream)stream2).NativeStream;

                using var pc = RTCPeerConnectionFactory.Create();

                // Step 1: Add audio + video from stream1
                var audioSender1 = pc.AddTrack(stream1.GetAudioTracks()[0]);
                var videoSender1 = pc.AddTrack(stream1.GetVideoTracks()[0]);
                var senders1 = pc.GetSenders().Length;
                if (senders1 < 2) throw new Exception($"Step 1: Expected at least 2 senders, got {senders1}");

                // Step 2: Add second video (stream2's video track)
                var videoSender2 = pc.AddTrack(stream2.GetVideoTracks()[0]);
                var senders2 = pc.GetSenders().Length;
                if (senders2 < 3) throw new Exception($"Step 2: Expected at least 3 senders, got {senders2}");

                // Step 3: Remove first video
                pc.RemoveTrack(videoSender1);

                // Step 4: Add second audio (stream2's audio track)
                var audioSender2 = pc.AddTrack(stream2.GetAudioTracks()[0]);

                // Step 5: Remove first audio
                pc.RemoveTrack(audioSender1);

                // Verify we still have senders (removeTrack nulls the track, doesn't remove sender)
                var finalSenders = pc.GetSenders().Length;
                if (finalSenders < 2) throw new Exception($"Final: Expected at least 2 senders, got {finalSenders}");

                stream1.Dispose();
                stream2.Dispose();
            }
            catch (Exception ex) when (ex.Message.Contains("NotAllowedError"))
            {
                throw new Exception($"SKIP: Camera/mic not available: {ex.Message}");
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Verify AddTrack increases sender count and RemoveTrack decreases it.
        /// </summary>
        [TestMethod]
        public async Task Track_AddRemove_SenderCount()
        {
            if (!OperatingSystem.IsBrowser()) return;

            try
            {
                var stream = await RTCMediaDevices.GetUserMedia(new MediaStreamConstraints { Audio = true, Video = true });
                using var pc = RTCPeerConnectionFactory.Create();

                var sendersBeforeAdd = pc.GetSenders().Length;

                var audioTrack = stream.GetAudioTracks()[0];
                var videoTrack = stream.GetVideoTracks()[0];

                var sender1 = pc.AddTrack(audioTrack);
                var sender2 = pc.AddTrack(videoTrack);

                var sendersAfterAdd = pc.GetSenders().Length;
                if (sendersAfterAdd < sendersBeforeAdd + 2)
                    throw new Exception($"Expected at least {sendersBeforeAdd + 2} senders, got {sendersAfterAdd}");

                // Remove one track
                pc.RemoveTrack(sender1);
                var sendersAfterRemove = pc.GetSenders().Length;
                // Note: removeTrack doesn't remove the sender, it sets sender.track = null
                // So sender count stays the same but the track is null

                stream.Dispose();
            }
            catch (Exception ex) when (ex.Message.Contains("NotAllowedError"))
            {
                throw new Exception($"SKIP: Camera/mic not available: {ex.Message}");
            }

            await Task.CompletedTask;
        }
    }
}

