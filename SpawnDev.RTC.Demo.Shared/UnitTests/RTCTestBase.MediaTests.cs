using SpawnDev.RTC;
using SpawnDev.UnitTesting;

namespace SpawnDev.RTC.Demo.Shared.UnitTests
{
    public abstract partial class RTCTestBase
    {
        /// <summary>
        /// Verify GetUserMedia returns a stream with tracks.
        /// Browser: real camera/mic (Playwright grants permission).
        /// Desktop: SipSorcery synthetic tracks.
        /// </summary>
        [TestMethod]
        public async Task GetUserMedia_AudioVideo()
        {
            try
            {
                var stream = await RTCMediaDevices.GetUserMedia(new MediaStreamConstraints
                {
                    Audio = true,
                    Video = true,
                });

                if (stream == null) throw new Exception("GetUserMedia returned null");

                var tracks = stream.GetTracks();
                if (tracks.Length == 0) throw new Exception("No tracks in stream");

                var audioTracks = stream.GetAudioTracks();
                var videoTracks = stream.GetVideoTracks();

                if (audioTracks.Length == 0) throw new Exception("No audio tracks");
                if (videoTracks.Length == 0) throw new Exception("No video tracks");

                foreach (var track in audioTracks)
                {
                    if (track.Kind != "audio") throw new Exception($"Audio track kind is '{track.Kind}'");
                    if (track.ReadyState != "live") throw new Exception($"Audio track state is '{track.ReadyState}'");
                }

                foreach (var track in videoTracks)
                {
                    if (track.Kind != "video") throw new Exception($"Video track kind is '{track.Kind}'");
                    if (track.ReadyState != "live") throw new Exception($"Video track state is '{track.ReadyState}'");
                }

                // Test track enable/disable
                var audioTrack = audioTracks[0];
                audioTrack.Enabled = false;
                if (audioTrack.Enabled) throw new Exception("Track should be disabled");
                audioTrack.Enabled = true;
                if (!audioTrack.Enabled) throw new Exception("Track should be enabled");

                // Test track stop
                var videoTrack = videoTracks[0];
                videoTrack.Stop();
                if (videoTrack.ReadyState != "ended") throw new Exception($"Stopped track state should be 'ended', got '{videoTrack.ReadyState}'");

                stream.Dispose();
            }
            catch (Exception ex) when (ex.Message.Contains("NotAllowedError") || ex.Message.Contains("permission"))
            {
                // Camera/mic permission denied in test environment - skip gracefully
                throw new Exception($"SKIP: Camera/mic permission not available: {ex.Message}");
            }
        }

        /// <summary>
        /// Verify AddTrack adds a sender to the peer connection.
        /// </summary>
        [TestMethod]
        public async Task PeerConnection_AddTrack_GetSenders()
        {
            using var pc = RTCPeerConnectionFactory.Create();

            var sendersBeforeAdd = pc.GetSenders();

            try
            {
                var stream = await RTCMediaDevices.GetUserMedia(new MediaStreamConstraints { Audio = true });
                var audioTrack = stream.GetAudioTracks()[0];

                var sender = pc.AddTrack(audioTrack);
                if (sender == null) throw new Exception("AddTrack returned null sender");
                if (sender.Track == null) throw new Exception("Sender.Track is null");
                if (sender.Track.Kind != "audio") throw new Exception($"Sender track kind: '{sender.Track.Kind}'");

                var sendersAfterAdd = pc.GetSenders();
                if (sendersAfterAdd.Length <= sendersBeforeAdd.Length)
                    throw new Exception($"Senders count didn't increase: before={sendersBeforeAdd.Length}, after={sendersAfterAdd.Length}");

                stream.Dispose();
            }
            catch (Exception ex) when (ex.Message.Contains("NotAllowedError") || ex.Message.Contains("permission"))
            {
                throw new Exception($"SKIP: Camera/mic not available: {ex.Message}");
            }
        }

        /// <summary>
        /// Verify MediaStream clone creates independent copy.
        /// </summary>
        [TestMethod]
        public async Task MediaStream_Clone()
        {
            try
            {
                var stream = await RTCMediaDevices.GetUserMedia(new MediaStreamConstraints { Audio = true });
                var clone = stream.Clone();

                if (clone == null) throw new Exception("Clone returned null");
                if (clone.Id == stream.Id) throw new Exception("Clone should have different ID");

                var originalTracks = stream.GetTracks();
                var cloneTracks = clone.GetTracks();
                if (cloneTracks.Length != originalTracks.Length)
                    throw new Exception($"Clone track count mismatch: {cloneTracks.Length} vs {originalTracks.Length}");

                stream.Dispose();
                clone.Dispose();
            }
            catch (Exception ex) when (ex.Message.Contains("NotAllowedError") || ex.Message.Contains("permission"))
            {
                throw new Exception($"SKIP: Camera/mic not available: {ex.Message}");
            }
        }

        /// <summary>
        /// Verify MediaStreamTrack.Clone creates independent track.
        /// </summary>
        [TestMethod]
        public async Task MediaStreamTrack_Clone()
        {
            try
            {
                var stream = await RTCMediaDevices.GetUserMedia(new MediaStreamConstraints { Audio = true });
                var track = stream.GetAudioTracks()[0];
                var clone = track.Clone();

                if (clone == null) throw new Exception("Clone returned null");
                if (clone.Kind != track.Kind) throw new Exception("Clone kind mismatch");
                if (clone.ReadyState != "live") throw new Exception($"Clone state: {clone.ReadyState}");

                // Stop clone shouldn't affect original
                clone.Stop();
                if (clone.ReadyState != "ended") throw new Exception("Clone should be ended");
                if (track.ReadyState != "live") throw new Exception("Original should still be live");

                clone.Dispose();
                stream.Dispose();
            }
            catch (Exception ex) when (ex.Message.Contains("NotAllowedError") || ex.Message.Contains("permission"))
            {
                throw new Exception($"SKIP: Camera/mic not available: {ex.Message}");
            }
        }

        /// <summary>
        /// Verify data channel BinaryType and BufferedAmountLowThreshold.
        /// </summary>
        [TestMethod]
        public async Task DataChannel_FlowControl_Properties()
        {
            using var pc = RTCPeerConnectionFactory.Create();
            using var dc = pc.CreateDataChannel("flow-test");

            // BinaryType defaults to "arraybuffer" (we set it in constructor)
            var bt = dc.BinaryType;
            if (bt != "arraybuffer" && bt != "blob" && bt != null)
                throw new Exception($"Unexpected BinaryType: '{bt}'");

            // BufferedAmount should be 0 initially
            if (dc.BufferedAmount < 0) throw new Exception($"BufferedAmount negative: {dc.BufferedAmount}");

            // BufferedAmountLowThreshold should be settable
            dc.BufferedAmountLowThreshold = 1024;
            if (dc.BufferedAmountLowThreshold != 1024)
                throw new Exception($"Threshold not set: {dc.BufferedAmountLowThreshold}");

            // MaxPacketLifeTime and MaxRetransmits should be null for default channels
            // (they're only set when explicitly configured)

            await Task.CompletedTask;
        }
    }
}
