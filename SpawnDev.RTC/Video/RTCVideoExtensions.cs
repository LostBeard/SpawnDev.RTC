using System;

namespace SpawnDev.RTC.Video
{
    /// <summary>Cross-platform video-receive helper over <see cref="IRTCPeerConnection"/>, mirroring <see cref="Audio.RTCAudioExtensions.ReceivePcmAudio"/>.</summary>
    public static class RTCVideoExtensions
    {
        /// <summary>
        /// Surfaces the pictures of the remote video track as <see cref="RTCVideoFrame"/>s. Call <see cref="IRTCVideoReceiver.Start"/> to begin. On desktop the video is read
        /// from the peer connection's RTP path, so <paramref name="receivedTrack"/> is not required there.
        /// </summary>
        /// <remarks>Desktop only so far: the browser already has the track in a video element, and a canvas read-back receiver is not written yet (this throws <see cref="PlatformNotSupportedException"/>).</remarks>
        public static IRTCVideoReceiver ReceiveVideoFrames(this IRTCPeerConnection pc, IRTCMediaStreamTrack? receivedTrack = null)
        {
            if (pc is null) throw new ArgumentNullException(nameof(pc));
            if (OperatingSystem.IsBrowser())
                throw new PlatformNotSupportedException("ReceiveVideoFrames is not implemented for the browser yet: attach the track to a video element, or read it through a canvas.");
            if (pc is Desktop.DesktopRTCPeerConnection desktop)
                return new Desktop.DesktopVideoReceiver(desktop);
            throw new NotSupportedException($"ReceiveVideoFrames: unsupported peer connection type {pc.GetType().Name}.");
        }
    }
}
