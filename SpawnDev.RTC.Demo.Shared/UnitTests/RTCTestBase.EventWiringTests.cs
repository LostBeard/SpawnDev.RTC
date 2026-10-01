using SpawnDev.RTC;
using SpawnDev.UnitTesting;

namespace SpawnDev.RTC.Demo.Shared.UnitTests
{
    public abstract partial class RTCTestBase
    {
        /// <summary>
        /// Browser remote track: OnUnmute must fire. A received track starts muted and the browser fires
        /// "unmute" once RTP actually arrives. BrowserRTCMediaStreamTrack declared OnUnmute but only wired
        /// "ended" and "mute" until 2.3.0, so this event could never fire. Loopback pair, fake mic
        /// (Playwright runs with --use-fake-device-for-media-stream).
        /// </summary>
        [TestMethod(Timeout = 20000)]
        public async Task Event_RemoteTrack_OnUnmuteFires_Browser()
        {
            if (!OperatingSystem.IsBrowser()) return; // Browser-only; SipSorcery tracks have no mute state.

            IRTCMediaStream? stream = null;
            try
            {
                try
                {
                    stream = await RTCMediaDevices.GetUserMedia(new MediaStreamConstraints { Audio = true });
                }
                catch (Exception ex) when (ex.Message.Contains("NotAllowedError") || ex.Message.Contains("NotFoundError"))
                {
                    throw new UnsupportedTestException($"Mic / fake-device not available: {ex.Message}");
                }

                using var pc1 = RTCPeerConnectionFactory.Create();
                using var pc2 = RTCPeerConnectionFactory.Create();
                pc1.OnIceCandidate += c => _ = pc2.AddIceCandidate(c);
                pc2.OnIceCandidate += c => _ = pc1.AddIceCandidate(c);

                var unmuted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var trackSeen = new TaskCompletionSource<IRTCMediaStreamTrack>(TaskCreationOptions.RunContinuationsAsynchronously);
                pc2.OnTrack += e =>
                {
                    // subscribe in OnTrack: it fires at setRemoteDescription, before any media flows
                    e.Track.OnUnmute += () => unmuted.TrySetResult(true);
                    trackSeen.TrySetResult(e.Track);
                };

                pc1.AddTrack(stream.GetAudioTracks()[0], stream);

                var offer = await pc1.CreateOffer();
                await pc1.SetLocalDescription(offer);
                await pc2.SetRemoteDescription(offer);
                var answer = await pc2.CreateAnswer();
                await pc2.SetLocalDescription(answer);
                await pc1.SetRemoteDescription(answer);

                if (await Task.WhenAny(trackSeen.Task, Task.Delay(10000)) != trackSeen.Task)
                    throw new Exception("pc2.OnTrack never fired");
                if (await Task.WhenAny(unmuted.Task, Task.Delay(15000)) != unmuted.Task)
                    throw new Exception($"Remote track OnUnmute never fired (Muted={(await trackSeen.Task).Muted}); 'unmute' is not wired");
            }
            finally
            {
                stream?.Dispose();
            }
        }

        /// <summary>
        /// Desktop: a remote ICE candidate SipSorcery rejects must surface as OnIceCandidateError. SipSorcery
        /// raises onicecandidateerror for it, but DesktopRTCPeerConnection never forwarded the event until
        /// 2.3.0, so a desktop app had no way to see why a candidate was dropped.
        /// </summary>
        [TestMethod(Timeout = 20000)]
        public async Task Event_RejectedRemoteCandidate_OnIceCandidateError_Desktop()
        {
            if (OperatingSystem.IsBrowser()) return; // Desktop-only; the browser raises this for STUN/TURN failures.

            using var pc1 = RTCPeerConnectionFactory.Create();
            using var pc2 = RTCPeerConnectionFactory.Create();
            using var dc = pc1.CreateDataChannel("events");

            var offer = await pc1.CreateOffer();
            await pc1.SetLocalDescription(offer);
            await pc2.SetRemoteDescription(offer);
            var answer = await pc2.CreateAnswer();
            await pc2.SetLocalDescription(answer);
            await pc1.SetRemoteDescription(answer);

            var error = new TaskCompletionSource<RTCIceCandidateError>(TaskCreationOptions.RunContinuationsAsynchronously);
            pc2.OnIceCandidateError += e => error.TrySetResult(e);

            // a host candidate on the wildcard address is never usable, so SipSorcery rejects it
            await pc2.AddIceCandidate(new RTCIceCandidateInit
            {
                Candidate = "candidate:1 1 udp 2130706431 0.0.0.0 50000 typ host",
                SdpMid = "0",
                SdpMLineIndex = 0,
            });

            if (await Task.WhenAny(error.Task, Task.Delay(5000)) != error.Task)
                throw new Exception("OnIceCandidateError never fired for a wildcard-address candidate");
            var e = await error.Task;
            if (!e.ErrorText.Contains("wildcard")) throw new Exception($"Unexpected ErrorText '{e.ErrorText}'");
            if (e.Address != "0.0.0.0") throw new Exception($"Expected Address 0.0.0.0, got '{e.Address}'");
            if (e.Port != 50000) throw new Exception($"Expected Port 50000, got {e.Port}");
        }
    }
}
