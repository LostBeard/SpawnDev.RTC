using SpawnDev.RTC;
using SpawnDev.UnitTesting;

namespace SpawnDev.RTC.Demo.Shared.UnitTests
{
    public abstract partial class RTCTestBase
    {
        /// <summary>
        /// Verify DTMF sender is accessible on audio sender (browser only).
        /// </summary>
        [TestMethod]
        public async Task DTMF_Sender_Available()
        {
            if (!OperatingSystem.IsBrowser()) return;

            try
            {
                var stream = await RTCMediaDevices.GetUserMedia(new MediaStreamConstraints { Audio = true });
                using var pc = RTCPeerConnectionFactory.Create();
                var sender = pc.AddTrack(stream.GetAudioTracks()[0]);

                var dtmf = sender.DTMF;
                // DTMF should be available on audio senders in browser
                if (dtmf == null) throw new Exception("DTMF is null on audio sender");
                if (dtmf.ToneBuffer == null) throw new Exception("ToneBuffer is null");

                stream.Dispose();
            }
            catch (Exception ex) when (ex.Message.Contains("NotAllowedError"))
            {
                throw new Exception($"SKIP: Mic not available: {ex.Message}");
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Verify MediaStreamTrack.ContentHint can be read.
        /// </summary>
        [TestMethod]
        public async Task MediaStreamTrack_ContentHint_Readable()
        {
            try
            {
                var stream = await RTCMediaDevices.GetUserMedia(new MediaStreamConstraints { Audio = true });
                var track = stream.GetAudioTracks()[0];

                var hint = track.ContentHint;
                // Default is empty string
                if (hint == null) throw new Exception("ContentHint is null");

                stream.Dispose();
            }
            catch (Exception ex) when (ex.Message.Contains("NotAllowedError"))
            {
                throw new Exception($"SKIP: Mic not available: {ex.Message}");
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Verify GetConstraints returns without throwing.
        /// </summary>
        [TestMethod]
        public async Task MediaStreamTrack_GetConstraints()
        {
            try
            {
                var stream = await RTCMediaDevices.GetUserMedia(new MediaStreamConstraints { Audio = true });
                var track = stream.GetAudioTracks()[0];

                var constraints = track.GetConstraints();
                if (constraints == null) throw new Exception("GetConstraints returned null");

                stream.Dispose();
            }
            catch (Exception ex) when (ex.Message.Contains("NotAllowedError"))
            {
                throw new Exception($"SKIP: Mic not available: {ex.Message}");
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Verify ApplyConstraints doesn't throw.
        /// </summary>
        [TestMethod]
        public async Task MediaStreamTrack_ApplyConstraints_NoThrow()
        {
            if (!OperatingSystem.IsBrowser()) return;

            try
            {
                var stream = await RTCMediaDevices.GetUserMedia(new MediaStreamConstraints { Video = true });
                var track = stream.GetVideoTracks()[0];

                await track.ApplyConstraints(new MediaTrackConstraints { Width = 640, Height = 480 });
                // Just verify it doesn't throw

                stream.Dispose();
            }
            catch (Exception ex) when (ex.Message.Contains("NotAllowedError") || ex.Message.Contains("OverconstrainedError"))
            {
                throw new Exception($"SKIP: {ex.Message}");
            }
        }

        /// <summary>
        /// Verify IRTCRtpSender.GetStats returns a report.
        /// </summary>
        [TestMethod]
        public async Task RtpSender_GetStats()
        {
            if (!OperatingSystem.IsBrowser()) return;

            try
            {
                var stream = await RTCMediaDevices.GetUserMedia(new MediaStreamConstraints { Audio = true });
                using var pc = RTCPeerConnectionFactory.Create();
                var sender = pc.AddTrack(stream.GetAudioTracks()[0]);

                using var stats = await sender.GetStats();
                if (stats == null) throw new Exception("Sender GetStats returned null");

                stream.Dispose();
            }
            catch (Exception ex) when (ex.Message.Contains("NotAllowedError"))
            {
                throw new Exception($"SKIP: Mic not available: {ex.Message}");
            }
        }

        /// <summary>
        /// Verify IRTCRtpSender.SetStreams doesn't throw.
        /// </summary>
        [TestMethod]
        public async Task RtpSender_SetStreams()
        {
            if (!OperatingSystem.IsBrowser()) return;

            try
            {
                var stream = await RTCMediaDevices.GetUserMedia(new MediaStreamConstraints { Audio = true });
                var browserStream = ((SpawnDev.RTC.Browser.BrowserRTCMediaStream)stream).NativeStream;
                using var pc = RTCPeerConnectionFactory.Create();
                var sender = pc.AddTrack(stream.GetAudioTracks()[0]);

                // SetStreams should not throw
                sender.SetStreams();

                stream.Dispose();
            }
            catch (Exception ex) when (ex.Message.Contains("NotAllowedError"))
            {
                throw new Exception($"SKIP: Mic not available: {ex.Message}");
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Verify GetDisplayMedia throws PlatformNotSupportedException on desktop.
        /// </summary>
        [TestMethod]
        public async Task GetDisplayMedia_DesktopThrows()
        {
            if (OperatingSystem.IsBrowser()) return;

            try
            {
                await RTCMediaDevices.GetDisplayMedia();
                throw new Exception("Should have thrown PlatformNotSupportedException");
            }
            catch (PlatformNotSupportedException)
            {
                // Expected
            }
        }

        /// <summary>
        /// Verify all interface methods on IRTCPeerConnection don't throw
        /// when called on a fresh connection.
        /// </summary>
        [TestMethod]
        public async Task PeerConnection_AllProperties_Readable()
        {
            using var pc = RTCPeerConnectionFactory.Create();

            // All properties should be readable without throwing
            var cs = pc.ConnectionState;
            var ics = pc.IceConnectionState;
            var igs = pc.IceGatheringState;
            var ss = pc.SignalingState;
            var cti = pc.CanTrickleIceCandidates;
            var ld = pc.LocalDescription;
            var rd = pc.RemoteDescription;
            var cld = pc.CurrentLocalDescription;
            var crd = pc.CurrentRemoteDescription;
            var pld = pc.PendingLocalDescription;
            var prd = pc.PendingRemoteDescription;
            var senders = pc.GetSenders();
            var receivers = pc.GetReceivers();
            var transceivers = pc.GetTransceivers();

            // None should be null (descriptions can be null, arrays should be empty not null)
            if (senders == null) throw new Exception("GetSenders null");
            if (receivers == null) throw new Exception("GetReceivers null");
            if (transceivers == null) throw new Exception("GetTransceivers null");

            await Task.CompletedTask;
        }
    }
}
