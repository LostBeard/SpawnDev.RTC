using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace SpawnDev.RTC.Desktop
{
    /// <summary>
    /// Desktop implementation of media device access.
    /// Creates SipSorcery media tracks with standard codec capabilities.
    /// Real device capture (cameras, microphones) requires SpawnDev.MultiMedia.
    /// </summary>
    public static class DesktopMediaDevices
    {
        public static Task<IRTCMediaStream> GetUserMedia(MediaStreamConstraints constraints)
        {
            var tracks = new List<IRTCMediaStreamTrack>();

            if (constraints.Audio != null)
            {
                var audioFormats = new List<SDPAudioVideoMediaFormat>
                {
                    new SDPAudioVideoMediaFormat(SDPWellKnownMediaFormatsEnum.PCMU),
                    new SDPAudioVideoMediaFormat(SDPWellKnownMediaFormatsEnum.PCMA),
                };
                var audioTrack = new MediaStreamTrack(SDPMediaTypesEnum.audio, false, audioFormats, MediaStreamStatusEnum.SendRecv);
                tracks.Add(new DesktopRTCMediaStreamTrack(audioTrack));
            }

            if (constraints.Video != null)
            {
                var videoFormats = new List<SDPAudioVideoMediaFormat>
                {
                    new SDPAudioVideoMediaFormat(new VideoFormat(VideoCodecsEnum.VP8, 96)),
                    new SDPAudioVideoMediaFormat(new VideoFormat(VideoCodecsEnum.H264, 100)),
                };
                var videoTrack = new MediaStreamTrack(SDPMediaTypesEnum.video, false, videoFormats, MediaStreamStatusEnum.SendRecv);
                tracks.Add(new DesktopRTCMediaStreamTrack(videoTrack));
            }

            if (tracks.Count == 0)
                throw new ArgumentException("At least one of audio or video must be requested.");

            IRTCMediaStream stream = new DesktopRTCMediaStream(tracks.ToArray());
            return Task.FromResult(stream);
        }

        /// <summary>
        /// Enumerates available media devices on desktop.
        /// SipSorcery does not provide hardware device enumeration - this returns
        /// an empty array. For real device enumeration, use SpawnDev.MultiMedia.
        /// </summary>
        public static Task<RTCMediaDeviceInfo[]> EnumerateDevices()
        {
            // SipSorcery handles codec negotiation but not hardware device discovery.
            // Real device enumeration requires SpawnDev.MultiMedia (MediaFoundation/DirectShow/WASAPI).
            return Task.FromResult(Array.Empty<RTCMediaDeviceInfo>());
        }
    }
}
